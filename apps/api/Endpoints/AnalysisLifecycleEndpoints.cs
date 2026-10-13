using System.Security.Cryptography;
using System.Text;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Endpoints;

public static class AnalysisLifecycleEndpoints
{
    public static void Map(RouteGroupBuilder routes)
    {
        routes.MapPost("/start", StartAsync).Produces<AnalysisResponse>();
        routes.MapPost("/{analysis_id}/complete", CompleteAsync).Produces<AnalysisResponse>();
        routes.MapPost("/{analysis_id}/fail", FailAsync).Produces<AnalysisResponse>();
        routes.MapPost("/{analysis_id}/sources", DeclareSourcesAsync);
        routes.MapPut("/{analysis_id}/sources/{source_id}", UploadSourceAsync).DisableAntiforgery();
    }

    private static async Task<IResult> StartAsync(
        AnalysisStartRequest request,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, "ci", ct);
        await auth.AssertProjectAccessAsync(user, request.ProjectKey, "analysis:write", ct);
        return Results.Ok(
            GateEvaluation.ToResponse(await AnalysisLifecycle.StartAsync(db, request, ct))
        );
    }

    private static async Task<IResult> CompleteAsync(
        string analysis_id,
        AnalysisRequest request,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var record = await WritableAsync(analysis_id, context, db, auth, ct);
        return Results.Ok(
            GateEvaluation.ToResponse(
                await AnalysisLifecycle.CompleteAsync(db, record, request, ct)
            )
        );
    }

    private static async Task<IResult> FailAsync(
        string analysis_id,
        AnalysisFailureRequest request,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var record = await WritableAsync(analysis_id, context, db, auth, ct);
        return Results.Ok(
            GateEvaluation.ToResponse(
                await AnalysisLifecycle.FailAsync(db, record, request.ErrorCode, ct)
            )
        );
    }

    private static async Task<IResult> DeclareSourcesAsync(
        string analysis_id,
        AnalysisSourcesRequest request,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        IOptions<IngestionOptions> options,
        CancellationToken ct
    )
    {
        var analysis = await WritableAsync(analysis_id, context, db, auth, ct);
        if (analysis.Status != "running")
            throw new ApiException(
                409,
                "Sources can only be declared while an analysis is running"
            );
        if (request.Sources.Count > options.Value.MaxSourceFiles)
            throw new ApiException(413, "Too many source files");
        var declarations = request
            .Sources.Select(x => new SourceDeclaration(
                AnalysisLifecycle.SafePath(x.Path),
                x.SizeBytes,
                x.Sha256.ToLowerInvariant()
            ))
            .ToList();
        if (
            declarations.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count()
            != declarations.Count
        )
            throw new ApiException(422, "Source paths must be unique");
        if (
            declarations.Any(x =>
                x.SizeBytes < 0
                || x.SizeBytes > options.Value.MaxSourceBytes
                || x.Sha256.Length != 64
                || x.Sha256.Any(c => !Uri.IsHexDigit(c))
            )
        )
            throw new ApiException(422, "Invalid source size or SHA-256");
        if (declarations.Sum(x => x.SizeBytes) > options.Value.MaxAnalysisSourceBytes)
            throw new ApiException(413, "Analysis source size limit exceeded");

        var existing = await db.Set<AnalysisSource>()
            .Where(x => x.AnalysisId == analysis_id)
            .ToListAsync(ct);
        foreach (var source in declarations)
        {
            var old = existing.SingleOrDefault(x => x.Path == source.Path);
            if (
                old is not null
                && (old.SizeBytes != source.SizeBytes || old.Sha256 != source.Sha256)
            )
                throw new ApiException(409, "A declared source is immutable");
            if (old is null)
            {
                old = new AnalysisSource
                {
                    AnalysisId = analysis_id,
                    Path = source.Path,
                    SizeBytes = source.SizeBytes,
                    Sha256 = source.Sha256,
                };
                db.Add(old);
                existing.Add(old);
            }
        }
        if (
            existing.Count > options.Value.MaxSourceFiles
            || existing.Sum(x => x.SizeBytes) > options.Value.MaxAnalysisSourceBytes
        )
            throw new ApiException(413, "Analysis source limits exceeded");
        await db.SaveChangesAsync(ct);
        return Results.Ok(
            new
            {
                sources = declarations
                    .Select(d => existing.Single(x => x.Path == d.Path))
                    .Select(x => new
                    {
                        source_id = x.SourceId,
                        path = x.Path,
                        state = x.State,
                        upload_url = $"/api/v1/analyses/{analysis_id}/sources/{x.SourceId}",
                    }),
            }
        );
    }

    private static async Task<IResult> UploadSourceAsync(
        string analysis_id,
        string source_id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        ReportStorage storage,
        IOptions<IngestionOptions> options,
        CancellationToken ct
    )
    {
        var analysis = await WritableAsync(analysis_id, context, db, auth, ct);
        var source =
            await db.Set<AnalysisSource>()
                .SingleOrDefaultAsync(
                    x => x.AnalysisId == analysis_id && x.SourceId == source_id,
                    ct
                )
            ?? throw new ApiException(404, "Source not declared");
        if (source.State == "received")
            return Results.Ok(new { source_id, state = source.State });
        if (analysis.Status != "running")
            throw new ApiException(409, "Analysis is no longer accepting sources");
        if (context.Request.ContentLength is { } length && length != source.SizeBytes)
            throw new ApiException(422, "Content length does not match declaration");
        if (source.SizeBytes > options.Value.MaxSourceBytes || source.SizeBytes > int.MaxValue)
            throw new ApiException(413, "Source file too large");
        await using var copy = new MemoryStream((int)source.SizeBytes);
        await context.Request.Body.CopyToAsync(copy, ct);
        if (copy.Length != source.SizeBytes)
            throw new ApiException(422, "Source length does not match declaration");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(copy.GetBuffer(), 0, (int)copy.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new ApiException(422, "Source must be UTF-8 text");
        }
        copy.Position = 0;
        var name = $"sources/{analysis_id}/{source_id}";
        await storage.UploadAsync(name, copy, source.SizeBytes, source.Sha256, ct);
        source.BlobName = name;
        source.State = "received";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { source_id, state = source.State });
    }

    private static async Task<AnalysisRecord> WritableAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, "ci", ct);
        var record =
            await db.Set<AnalysisRecord>().FindAsync([id], ct)
            ?? throw new ApiException(404, "Analysis not found");
        await auth.AssertProjectAccessAsync(user, record.ProjectKey, "analysis:write", ct);
        return record;
    }
}
