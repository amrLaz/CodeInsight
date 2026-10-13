using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Endpoints;

/// <summary>Bounded native coverage and duplication evidence for one exact analysis.</summary>
internal static class CodeQualityInsightEndpoints
{
    public static RouteGroupBuilder MapCodeQualityInsightEndpoints(this RouteGroupBuilder routes)
    {
        routes.MapGet("/analyses/{id}/code-quality", SummaryAsync);
        routes.MapGet("/analyses/{id}/coverage/files", CoverageFilesAsync);
        routes.MapGet("/analyses/{id}/coverage/lines", CoverageLinesAsync);
        routes.MapGet("/analyses/{id}/duplications", DuplicationsAsync);
        routes.MapGet("/analyses/{id}/source", SourceAsync);
        return routes;
    }

    private static async Task<IResult> SummaryAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var detail = await DetailAsync(id, context, db, auth, ct);
        var coverageState = State(detail, "coverage");
        var duplicationState = State(detail, "duplication");
        long? covered = null;
        long? total = null;
        if (coverageState.State == "measured")
        {
            var counts = await db.Set<CoverageLine>()
                .AsNoTracking()
                .Where(x => x.IngestionId == detail.Ingestion!.Id)
                .GroupBy(_ => 1)
                .Select(g => new { Covered = g.LongCount(x => x.Covered), Total = g.LongCount() })
                .SingleOrDefaultAsync(ct);
            covered = counts?.Covered ?? 0;
            total = counts?.Total ?? 0;
        }

        CodeQualityTotals? totals = null;
        if (duplicationState.State == "measured" && detail.Snapshot is not null)
            totals = CodeQualityData.Summarize(detail.Snapshot, []);
        return Results.Ok(new
        {
            analysis_id = detail.Analysis.AnalysisId,
            project_key = detail.Analysis.ProjectKey,
            branch = detail.Analysis.Branch,
            commit_sha = detail.Analysis.CommitSha,
            coverage = new
            {
                state = coverageState.State,
                reason = coverageState.Reason,
                covered_lines = covered,
                uncovered_lines = total - covered,
                total_lines = total,
                percentage = total > 0 ? 100d * covered / total : (double?)null,
            },
            duplication = new
            {
                state = duplicationState.State,
                reason = duplicationState.Reason,
                density_percentage = totals?.DuplicatedLinesDensity.Percentage,
                duplicate_blocks = totals?.DuplicateBlocks,
                duplicated_lines = totals?.DuplicatedLines,
            },
        });
    }

    private static async Task<IResult> CoverageFilesAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var search = Search(context.Request);
        var cursor = InsightEndpoints.DecodeCursor(context.Request, 2);
        var cursorUncovered = cursor is null ? (long?)null : NonNegative(cursor[0]);
        var cursorPath = cursor?[1];
        var size = InsightEndpoints.PageSize(context.Request);
        var detail = await DetailAsync(id, context, db, auth, ct);
        var state = State(detail, "coverage");
        if (state.State != "measured")
            return Results.Ok(new { state = state.State, reason = state.Reason, files = Array.Empty<object>() });

        var query = db.Set<CoverageLine>()
            .AsNoTracking()
            .Where(x => x.IngestionId == detail.Ingestion!.Id);
        if (search.Length > 0)
        {
            var pattern = SqlLike.Containing(search);
            query = query.Where(x => EF.Functions.ILike(x.Path, pattern, SqlLike.EscapeCharacter));
        }
        var grouped = query.GroupBy(x => x.Path).Select(g => new
        {
            Path = g.Key,
            Covered = g.LongCount(x => x.Covered),
            Total = g.LongCount(),
        });
        if (cursorUncovered is not null)
            grouped = grouped.Where(x =>
                x.Total - x.Covered < cursorUncovered
                || x.Total - x.Covered == cursorUncovered && string.Compare(x.Path, cursorPath) > 0
            );
        var rows = await grouped
            .OrderByDescending(x => x.Total - x.Covered)
            .ThenBy(x => x.Path)
            .Take(size + 1)
            .ToListAsync(ct);
        if (rows.Count > size)
        {
            rows.RemoveAt(size);
            InsightEndpoints.Continuation(
                context,
                InsightEndpoints.EncodeCursor((rows[^1].Total - rows[^1].Covered).ToString(), rows[^1].Path)
            );
        }
        return Results.Ok(new
        {
            state = state.State,
            reason = state.Reason,
            files = rows.Select(x => new
            {
                path = x.Path,
                covered_lines = x.Covered,
                uncovered_lines = x.Total - x.Covered,
                total_lines = x.Total,
                percentage = x.Total == 0 ? (double?)null : 100d * x.Covered / x.Total,
                source_url = SourceUrl(detail, x.Path, null),
            }),
        });
    }

    private static async Task<IResult> CoverageLinesAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var path = context.Request.Query["path"].ToString();
        if (!IngestionValidation.SafePath(path))
            throw new ApiException(422, "path must be a repository-relative path");
        var covered = context.Request.Query["covered"].ToString().ToLowerInvariant();
        if (covered.Length == 0)
            covered = "false";
        if (covered is not ("false" or "true" or "all"))
            throw new ApiException(422, "covered must be false, true or all");
        var after = InsightEndpoints.NumericCursor(context.Request);
        var size = InsightEndpoints.PageSize(context.Request);
        var detail = await DetailAsync(id, context, db, auth, ct);
        var state = State(detail, "coverage");
        if (state.State != "measured")
            return Results.Ok(new
            {
                state = state.State,
                reason = state.Reason,
                path,
                covered,
                lines = Array.Empty<object>(),
            });

        var query = db.Set<CoverageLine>()
            .AsNoTracking()
            .Where(x => x.IngestionId == detail.Ingestion!.Id && x.Path == path && x.Line > after);
        if (covered != "all")
        {
            var expected = covered == "true";
            query = query.Where(x => x.Covered == expected);
        }
        var rows = await query.OrderBy(x => x.Line).Take(size + 1).ToListAsync(ct);
        if (rows.Count > size)
        {
            rows.RemoveAt(size);
            InsightEndpoints.Continuation(context, rows[^1].Line.ToString());
        }
        return Results.Ok(new
        {
            state = state.State,
            reason = state.Reason,
            path,
            covered,
            lines = rows.Select(x => new
            {
                line = x.Line,
                covered = x.Covered,
                source_url = SourceUrl(detail, x.Path, x.Line),
            }),
        });
    }

    private static async Task<IResult> DuplicationsAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var search = Search(context.Request);
        var after = InsightEndpoints.NumericCursor(context.Request);
        var size = InsightEndpoints.PageSize(context.Request);
        var detail = await DetailAsync(id, context, db, auth, ct);
        var state = State(detail, "duplication");
        if (state.State != "measured" || detail.Snapshot is null)
            return Results.Ok(new
            {
                state = state.State,
                reason = state.Reason,
                duplications = Array.Empty<object>(),
            });

        var rows = detail.Snapshot.Reports
            .Where(x => x.DuplicationMeasured)
            .SelectMany((report, reportIndex) => report.Duplicates.Select((duplicate, duplicateIndex) => new
            {
                Ordinal = reportIndex * 250_000L + duplicateIndex + 1,
                Report = report,
                Duplicate = duplicate,
            }))
            .Where(x => x.Ordinal > after)
            .Where(x => search.Length == 0
                || x.Report.ReportId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Report.Tool.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Duplicate.Language.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Duplicate.First.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Duplicate.Second.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(size + 1)
            .ToList();
        if (rows.Count > size)
        {
            rows.RemoveAt(size);
            InsightEndpoints.Continuation(context, rows[^1].Ordinal.ToString());
        }
        return Results.Ok(new
        {
            state = state.State,
            reason = state.Reason,
            duplications = rows.Select(x => new
            {
                report_id = x.Report.ReportId,
                tool = x.Report.Tool,
                tool_version = x.Report.ToolVersion,
                language = x.Duplicate.Language,
                lines = x.Duplicate.Lines,
                tokens = x.Duplicate.Tokens,
                first = Location(detail, x.Duplicate.First),
                second = Location(detail, x.Duplicate.Second),
            }),
        });
    }

    private static async Task<IResult> SourceAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        IHttpClientFactory clients,
        AzureDevOpsAuthentication azureDevOpsAuthentication,
        IOptions<IngestionOptions> options,
        CancellationToken ct
    )
    {
        var path = context.Request.Query["path"].ToString();
        if (!IngestionValidation.SafePath(path))
            throw new ApiException(422, "path must be a repository-relative path");
        var detail = await DetailAsync(id, context, db, auth, ct);
        if (detail.Analysis.DetailsPurged || detail.Ingestion?.DetailsPurged == true)
            throw new ApiException(410, "Analysis details were purged");
        if (detail.Ingestion is null || detail.Manifest is null)
            throw new ApiException(409, "Native analysis evidence is unavailable");

        var coverage = await db.Set<CoverageLine>()
            .AsNoTracking()
            .Where(x => x.IngestionId == detail.Ingestion.Id && x.Path == path)
            .OrderBy(x => x.Line)
            .Select(x => new { line = x.Line, covered = x.Covered })
            .ToListAsync(ct);
        var duplicated = detail.Snapshot?.Reports.Any(report =>
            report.Duplicates.Any(duplicate =>
                duplicate.First.Path == path || duplicate.Second.Path == path
            )
        ) == true;
        if (coverage.Count == 0 && !duplicated)
            throw new ApiException(404, "Source path was not found in this analysis evidence");

        var repositoryUrl = detail.Manifest?.RepositoryUrl ?? detail.Analysis.RepositoryUrl;
        var integration = await db.Set<ProjectIntegration>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectKey == detail.Analysis.ProjectKey, ct);
        if (
            integration is null
            || integration.RepoProvider != "azure-devops"
            || !IngestionAdo.SameRepository(integration.RepoUrl, repositoryUrl)
        )
            throw new ApiException(409, "The Azure DevOps repository is not associated with this project");

        AzureDevOpsRepository repository;
        try
        {
            repository = AzureDevOpsSource.Repository(repositoryUrl);
        }
        catch (Exception exception) when (exception is ApiException or UriFormatException)
        {
            throw new ApiException(409, "The analyzed Azure DevOps repository is invalid");
        }
        var configs = await db.Set<AzureDevOpsOrganizationConfig>().AsNoTracking().ToListAsync(ct);
        var config = configs.FirstOrDefault(x =>
            x.OrganizationUrl.TrimEnd('/').Equals(
                repository.OrganizationUrl,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (config is null)
            throw new ApiException(409, "Source browsing is not configured for this Azure DevOps organization");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string content;
        try
        {
            using var client = clients.CreateClient("azure-devops-source");
            await azureDevOpsAuthentication.AuthorizeAsync(
                client,
                repository.OrganizationUrl,
                timeout.Token
            );
            content = await AzureDevOpsSource.DownloadAsync(
                client,
                repository,
                detail.Analysis.CommitSha,
                path,
                options.Value.MaxSourceBytes,
                timeout.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(504, "Azure DevOps source browsing timed out");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(new
        {
            analysis_id = detail.Analysis.AnalysisId,
            path,
            commit_sha = detail.Analysis.CommitSha,
            provider = "azure-devops",
            source_url = SourceUrl(detail, path, null),
            line_count = content.Length == 0 ? 0 : content.Count(x => x == '\n') + 1,
            content,
            coverage,
        });
    }

    private static object Location(QualityDetail detail, DuplicateLocation location) => new
    {
        path = location.Path,
        start_line = location.Start,
        end_line = location.End,
        source_url = SourceUrl(detail, location.Path, location.Start, location.End),
    };

    private static string? SourceUrl(QualityDetail detail, string path, int? line, int? endLine = null) =>
        FindingEvidenceLinks.AzureDevOpsSource(
            detail.Manifest?.RepositoryUrl ?? detail.Analysis.RepositoryUrl,
            detail.Analysis.CommitSha,
            path,
            line,
            endLine
        );

    private static (string State, string? Reason) State(QualityDetail detail, string metric)
    {
        if (detail.Analysis.DetailsPurged || detail.Ingestion?.DetailsPurged == true)
            return ("unavailable", "details_purged");
        if (detail.Ingestion is null)
            return ("unavailable", "direct_ingestion");
        if (detail.Manifest is null)
            return ("unavailable", "details_unavailable");
        var formats = metric == "coverage" ? new[] { "lcov", "cobertura" } : new[] { "jscpd" };
        if (!detail.Manifest.Reports.Any(x => formats.Contains(x.Format) && x.State == "measured"))
            return ("not_measured", "report_not_measured");
        return metric == "duplication" && detail.Snapshot is null
            ? ("unavailable", "details_unavailable")
            : ("measured", null);
    }

    private static async Task<QualityDetail> DetailAsync(
        string id,
        HttpContext context,
        AppDbContext db,
        AuthService auth,
        CancellationToken ct
    )
    {
        var ingestion = await InsightEndpoints.LatestAsync(id, context, db, auth, ct);
        var analysis = await db.Set<AnalysisRecord>()
            .AsNoTracking()
            .SingleAsync(x => x.AnalysisId == id, ct);
        IngestionManifest? manifest = null;
        CodeQualitySnapshot? snapshot = null;
        if (ingestion is not null && !analysis.DetailsPurged && !ingestion.DetailsPurged)
        {
            try
            {
                manifest = JsonSerializer.Deserialize<IngestionManifest>(ingestion.Manifest, ApiJson.Options);
                snapshot = JsonSerializer.Deserialize<CodeQualitySnapshot>(ingestion.CodeData, ApiJson.Options);
            }
            catch (JsonException)
            {
                // ponytail: normalized data is bounded and validated at ingestion; a corrupt legacy row remains unavailable.
            }
        }
        return new(analysis, ingestion, manifest, snapshot);
    }

    private static string Search(HttpRequest request)
    {
        var raw = request.Query["search"].ToString();
        if (raw.Length > 500 || raw.Any(char.IsControl))
            throw new ApiException(422, "search must contain at most 500 printable characters");
        return raw.Trim().ToLowerInvariant();
    }

    private static long NonNegative(string value) =>
        long.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ApiException(422, "Invalid cursor");

    private sealed record QualityDetail(
        AnalysisRecord Analysis,
        IngestionRecord? Ingestion,
        IngestionManifest? Manifest,
        CodeQualitySnapshot? Snapshot
    );
}
