using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Endpoints;

public static class AnalysisEndpoints
{
    private static readonly string[] MissingProjectKeyLocation = ["query", "project_key"];

    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/analyses").RequireAuthorization();
        routes.MapGet("", InsightEndpoints.ListAnalysesAsync);
        routes.MapPost("", CreateAsync).Produces<AnalysisResponse>();
        AnalysisLifecycleEndpoints.Map(routes);
        routes.MapGet(
            "/export/csv",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                var user = await auth.RequireAsync(context, ct: ct);
                var key = context.Request.Query["project_key"].ToString();
                if (key.Length == 0)
                    throw new ApiException(
                        422,
                        new[]
                        {
                            new
                            {
                                type = "missing",
                                loc = MissingProjectKeyLocation,
                                msg = "Field required",
                            },
                        }
                    );
                await auth.AssertProjectAccessAsync(user, key, ct: ct);
                var rows = db.Set<AnalysisRecord>().AsNoTracking()
                    .Where(x => x.ProjectKey == key)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(InsightEndpoints.Summary)
                    .AsAsyncEnumerable();
                return Results.Stream(
                    async output =>
                    {
                        var header = ExportService.Csv([]);
                        await output.WriteAsync(header, ct);
                        await foreach (var row in rows.WithCancellation(ct))
                        {
                            var csv = ExportService.Csv([row]);
                            await output.WriteAsync(csv.AsMemory(header.Length), ct);
                        }
                    },
                    "text/csv",
                    $"sast-{new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray())}-history.csv"
                );
            }
        );
        routes
            .MapGet(
                "/{analysis_id}",
                async (
                    string analysis_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                    Results.Ok(
                        GateEvaluation.ToResponse(
                            await FindAsync(analysis_id, context, db, auth, ct)
                        )
                    )
            )
            .Produces<AnalysisResponse>();
        routes.MapGet(
            "/{analysis_id}/view",
            async (
                string analysis_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                Microsoft.Extensions.Options.IOptions<IngestionOptions> ingestion,
                CancellationToken ct
            ) =>
            {
                await FindAsync(analysis_id, context, db, auth, ct);
                // The link published to Azure DevOps, notifications and IDEs is the configured hub
                // URL; a hard-coded development origin would send every reader to their own machine.
                var platform = ingestion.Value.PlatformUrl.Trim().TrimEnd('/');
                if (platform.Length == 0)
                    throw new ApiException(503, "Ingestion:PlatformUrl is not configured");
                return Results.Redirect(
                    $"{platform}/?analysis_id={Uri.EscapeDataString(analysis_id)}"
                );
            }
        );
        routes.MapGet(
            "/{analysis_id}/export/csv",
            async (
                string analysis_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
                Results.File(
                    ExportService.Csv([await FindAsync(analysis_id, context, db, auth, ct)]),
                    "text/csv",
                    $"sast-{analysis_id[..Math.Min(8, analysis_id.Length)]}.csv"
                )
        );
        routes.MapDelete(
            "/projects/{project_key}",
            async (
                string project_key,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                ReportStorage storage,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return Results.Ok(await DeleteAsync(db, storage, project_key, ct));
            }
        );
        return app;
    }

    public static async Task<AnalysisRecord> FindAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, ct: ct);
        var record =
            await db.Set<AnalysisRecord>().FindAsync([id], ct)
            ?? throw new ApiException(404, "Analysis not found");
        await auth.AssertProjectAccessAsync(user, record.ProjectKey, ct: ct);
        return record;
    }

    private static async Task<IResult> CreateAsync(
        AnalysisRequest request,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        NotificationService notifications,
        CancellationToken ct
    )
    {
        var user = await auth.RequireAsync(context, "ci", ct);
        GateEvaluation.Validate(request);
        request.RepositoryUrl = RepositoryUrls.Normalize(request.CiProvider, request.RepositoryUrl);
        var project =
            await db.Set<Project>().FirstOrDefaultAsync(x => x.Key == request.ProjectKey, ct)
            ?? throw new ApiException(
                404,
                "Project must be registered before publishing an analysis"
            );
        await auth.AssertProjectAccessAsync(user, request.ProjectKey, "analysis:write", ct);
        var assignment = await db.Set<ProjectGateAssignment>().FindAsync([request.ProjectKey], ct);
        var gate =
            assignment == null
                ? null
                : await db.Set<QualityGateConfig>().FindAsync([assignment.GateId], ct);
        gate ??=
            await db.Set<QualityGateConfig>().FirstOrDefaultAsync(x => x.IsDefault, ct)
            ?? await db.Set<QualityGateConfig>().FindAsync(["default"], ct);
        if (gate == null)
        {
            gate = new QualityGateConfig
            {
                Id = "default",
                Name = "Default Gate",
                IsDefault = true,
            };
            db.Add(gate);
        }
        var metrics = GateEvaluation.BuildMetrics(request);
        var measurements = request.Measurements;
        var profileId = request.ProfileId;
        var profileVersion = request.ProfileVersion;
        var completeness = GateEvaluation.Completeness(request, project);
        var (verdict, failures, snapshot) = GateEvaluation.Evaluate(
            metrics,
            gate,
            measurements,
            profileId,
            profileVersion,
            request.FormulaVersion
        );
        var technical = GateEvaluation.TechnicalState(
            completeness,
            request.ToolExecutions,
            measurements
        );
        if (completeness != "complete" && verdict != "fail")
            verdict = "unknown";
        snapshot["completeness"] = completeness;
        snapshot["technical_state"] = technical;
        var record = new AnalysisRecord
        {
            AnalysisId = Guid.NewGuid().ToString(),
            ProjectKey = request.ProjectKey,
            CommitSha = request.CommitSha,
            Branch = BranchRefs.Canonical(request.Branch),
            CiProvider = request.CiProvider,
            ProviderRunId = request.ProviderRunId ?? "",
            RepositoryUrl = request.RepositoryUrl ?? "",
            Status = "completed",
            QualityGate = verdict,
            MaintainabilityScore = metrics.MaintainabilityScore,
            SecurityScore = metrics.SecurityScore,
            ReliabilityScore = metrics.ReliabilityScore,
            CodeSmells = metrics.CodeSmells,
            Vulnerabilities = metrics.Vulnerabilities,
            SecurityHotspots = metrics.SecurityHotspots,
            Bugs = metrics.Bugs,
            Coverage = metrics.Coverage,
            Duplication = metrics.Duplication,
            DependencyVulnerabilities = metrics.DependencyVulnerabilities,
            Measurements = JsonSerializer.Serialize(measurements, ApiJson.Options),
            ToolExecutions = JsonSerializer.Serialize(request.ToolExecutions, ApiJson.Options),
            Completeness = completeness,
            ProfileId = profileId,
            ProfileVersion = profileVersion,
            FormulaVersion = request.FormulaVersion,
            GateSnapshot = JsonSerializer.Serialize(snapshot, ApiJson.Options),
            AffectedFiles = JsonSerializer.Serialize(metrics.AffectedFiles ?? [], ApiJson.Options),
            GateFailures = JsonSerializer.Serialize(failures),
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz"),
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            PrId = request.PrId ?? "",
            PrRepoId = request.PrRepoId ?? "",
            PrAdoProject = request.PrAdoProject ?? "",
            PrTargetBranch = request.PrTargetBranch ?? "",
            PrAdoOrgUrl = request.PrAdoOrgUrl ?? "",
            PrSourceSha = request.PrSourceSha ?? request.CommitSha,
            PrTargetSha = request.PrTargetSha ?? "",
            PrDecorationStatus = "queued",
            PrDecorationError = "",
            DecorationRevision = 1,
        };
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.Add(record);
            var occurrences = AnalysisOccurrences.From(record).ToList();
            record.ErrorCount = occurrences.Count(x => x.Severity.ToLowerInvariant() is "blocker" or "critical" or "error" or "high");
            record.WarningCount = occurrences.Count(x => x.Severity.ToLowerInvariant() is "warning" or "medium");
            record.InfoCount = occurrences.Count - record.ErrorCount - record.WarningCount;
            record.QualityGateSummary = GateEvaluation.Summary(record.QualityGate, record.ErrorCount);
            db.AddRange(occurrences);
            await FindingCollaboration.ObserveAsync(db, record, occurrences, ct);
            db.Add(new DurableJob { Kind = "pr-decoration", ResourceId = $"{record.AnalysisId}:1" });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        notifications.Dispatch(
            verdict,
            new()
            {
                ["project_key"] = record.ProjectKey,
                ["branch"] = record.Branch,
                ["commit_sha"] = record.CommitSha,
                ["analysis_id"] = record.AnalysisId,
                ["vulnerabilities"] = record.Vulnerabilities,
                ["bugs"] = record.Bugs,
                ["code_smells"] = record.CodeSmells,
                ["security_hotspots"] = record.SecurityHotspots,
                ["gate_failures"] = failures,
            }
        );
        return Results.Ok(GateEvaluation.ToResponse(record));
    }

    private static async Task<object> DeleteAsync(
        AppDbContext db,
        ReportStorage storage,
        string key,
        CancellationToken ct
    )
    {
        var sourceBlobs = await db.Set<AnalysisSource>()
            .Where(x =>
                x.BlobName != ""
                && db.Set<AnalysisRecord>()
                    .Any(a => a.AnalysisId == x.AnalysisId && a.ProjectKey == key)
            )
            .Select(x => x.BlobName)
            .ToListAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<AnalysisRecord>().Where(x => x.ProjectKey == key);
        var ingestions = db.Set<IngestionRecord>()
            .FromSqlInterpolated($"SELECT * FROM ingestions WHERE project_key = {key}");
        var ingestionIds = ingestions.Select(x => x.Id);
        var analysisIds = query.Select(x => x.AnalysisId);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM durable_jobs WHERE split_part(resource_id, ':', 1) IN (SELECT id FROM ingestions WHERE project_key = {key})",
            ct
        );
        await db.Set<FindingOccurrence>()
            .Where(x => ingestionIds.Contains(x.IngestionId) || analysisIds.Contains(x.AnalysisId))
            .ExecuteDeleteAsync(ct);
        await db.Set<CoverageLine>()
            .Where(x => ingestionIds.Contains(x.IngestionId))
            .ExecuteDeleteAsync(ct);
        await db.Set<SbomComponent>()
            .Where(x => ingestionIds.Contains(x.IngestionId))
            .ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ingestions WHERE project_key = {key}",
            ct
        );
        var deletedCount = await query.ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var blob in sourceBlobs)
            await storage.DeleteAsync(blob, ct);
        var message =
            deletedCount == 0
                ? $"No analyses found for project {key}"
                : $"Deleted {deletedCount} analyses for project {key}";
        return new Dictionary<string, object?>
        {
            ["project_key"] = key,
            ["deleted_analyses"] = deletedCount,
            ["message"] = message,
        };
    }
}
