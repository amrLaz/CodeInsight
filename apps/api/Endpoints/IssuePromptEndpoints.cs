using System.Text;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Endpoints;

public static class IssuePromptEndpoints
{
    public static IEndpointRouteBuilder MapIssuePromptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/issues/{occurrence_id:long}/prompt", GetAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetAsync(
        long occurrence_id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        ReportStorage storage,
        IOptions<IngestionOptions> options,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, "user", ct);
        var issue =
            await db.Set<FindingOccurrence>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == occurrence_id, ct)
            ?? throw new ApiException(404, "Issue not found");
        var analysis =
            await db.Set<AnalysisRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.AnalysisId == issue.AnalysisId, ct)
            ?? throw new ApiException(404, "Analysis not found");
        await auth.AssertProjectAccessAsync(user, analysis.ProjectKey, ct: ct);
        if (analysis.DetailsPurged)
            throw new ApiException(410, "Source context was purged");
        if (issue.Path is null || issue.Line is null)
            throw new ApiException(409, "Issue has no source location");
        var path = AnalysisLifecycle.SafePath(issue.Path);
        var source = await db.Set<AnalysisSource>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.AnalysisId == analysis.AnalysisId && x.Path == path, ct);
        if (source?.State != "received" || string.IsNullOrEmpty(source.BlobName))
            throw new ApiException(409, "Source context is unavailable");

        await using var input = await storage.OpenAsync(source.BlobName, ct);
        using var reader = new StreamReader(
            input,
            new UTF8Encoding(false, true),
            false,
            4096,
            leaveOpen: false
        );
        string text;
        try
        {
            text = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw new ApiException(409, "Stored source is not valid UTF-8");
        }
        var snippet = PromptContext.Extract(
            text,
            issue.Line.Value,
            options.Value.PromptContextLines
        );
        var prompt = $"""
You are fixing an issue detected by CodeInsights.

Rule:
{issue.RuleId}

Severity:
{issue.Severity}

Issue:
{issue.Message}

File:
{path}

Line:
{issue.Line}

Relevant source code:
{snippet}

Fix the issue while minimizing unrelated changes.
""";
        return Results.Ok(new { prompt });
    }
}
