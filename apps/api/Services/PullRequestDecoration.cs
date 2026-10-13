using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CodeInsights.Api.Services;

internal sealed record PullRequestIssueSelection(
    IReadOnlyList<FindingOccurrence> Issues,
    int EligibleCount
)
{
    public int OmittedCount => EligibleCount - Issues.Count;
}

internal static class PullRequestDetails
{
    internal static string Url(string platformUrl, string analysisId) =>
        platformUrl.Length == 0
            ? ""
            : $"{platformUrl.TrimEnd('/')}/?analysis_id={Uri.EscapeDataString(analysisId)}";

    internal static string Truncation(
        PullRequestIssueSelection selection,
        string displayed,
        string target
    )
    {
        var omitted =
            selection.OmittedCount == 1
                ? "1 additional finding is available."
                : $"{selection.OmittedCount} additional findings are available.";
        return $"Only {selection.Issues.Count} of {selection.EligibleCount} findings on added PR lines are {displayed}. "
            + omitted
            + $"\n\n[Open the complete analysis in CodeInsights]({target})";
    }
}

internal static class PullRequestIssues
{
    private const int MaximumDecoratedFindings = 49;

    public static async Task<PullRequestIssueSelection> CurrentAsync(
        AppDbContext db,
        string analysisId,
        CancellationToken ct
    )
    {
        var ingestion = await db.Set<IngestionRecord>()
            .AsNoTracking()
            .Where(x => x.AnalysisId == analysisId && x.State == "completed")
            .OrderByDescending(x => x.Revision)
            .Select(x => new { x.Id, x.Manifest })
            .FirstOrDefaultAsync(ct);
        if (ingestion is null)
            return new([], 0);
        IngestionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<IngestionManifest>(
                ingestion.Manifest,
                ApiJson.Options
            );
        }
        catch (JsonException)
        {
            return new([], 0);
        }
        if (manifest is null)
            return new([], 0);
        var issues = await db.Set<FindingOccurrence>()
            .AsNoTracking()
            .Where(x =>
                x.AnalysisId == analysisId
                && !x.Excepted
                && x.IngestionId == ingestion.Id
            )
            .ToListAsync(ct);
        return Select(manifest, issues);
    }

    internal static PullRequestIssueSelection Select(
        IngestionManifest? manifest,
        IEnumerable<FindingOccurrence> issues
    )
    {
        var diff = manifest?.GitDiff;
        if (
            diff is null
            || diff.State != "available"
            || !string.Equals(diff.BaseSha, manifest!.TargetSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(diff.HeadSha, manifest.CommitSha, StringComparison.OrdinalIgnoreCase)
            || diff.Files is null
            || diff.Files.Any(file =>
                file is null
                || string.IsNullOrWhiteSpace(file.Path)
                || file.AddedLines is null
                || file.AddedLines.Any(line => line <= 0)
            )
        )
            return new([], 0);
        var addedLines = diff
            .Files.GroupBy(x => NormalizePath(x.Path), StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.SelectMany(file => file.AddedLines).ToHashSet(),
                StringComparer.Ordinal
            );
        var eligible = issues
            .Where(issue =>
                !issue.Excepted
                && issue.Path is { } path
                && issue.Line is > 0
                && addedLines.TryGetValue(NormalizePath(path), out var lines)
                && lines.Contains(issue.Line.Value)
            )
            .OrderBy(issue => SeverityOrder(issue.Severity))
            .ThenBy(issue => NormalizePath(issue.Path!), StringComparer.Ordinal)
            .ThenBy(issue => issue.Line)
            .ThenBy(issue => issue.Id)
            .ToList();
        return new(eligible.Take(MaximumDecoratedFindings).ToList(), eligible.Count);
    }

    private static int SeverityOrder(string severity) =>
        severity.ToLowerInvariant() is "blocker" or "critical" or "error" or "high" ? 0
        : severity.ToLowerInvariant() is "warning" or "medium" ? 1
        : 2;

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}

public interface IPullRequestDecorator
{
    Task PublishPendingAsync(AnalysisRecord analysis, CancellationToken cancellationToken);
    Task PublishResultAsync(
        AnalysisRecord analysis,
        QualityGateResult result,
        CancellationToken cancellationToken
    );
}

public sealed class PullRequestDecoration(
    AppDbContext db,
    AzureDevOpsDecorator ado,
    GitHubDecorator github
)
{
    public async Task PublishCurrentAsync(AnalysisRecord analysis, CancellationToken ct)
    {
        IPullRequestDecorator? decorator = analysis.CiProvider switch
        {
            "azure-devops" => ado,
            "github-actions" => github,
            _ => null,
        };
        if (decorator is null || string.IsNullOrEmpty(analysis.PrId))
            return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({analysis.CiProvider + ":" + analysis.PrRepoId + ":" + analysis.PrId}, 0))",
            ct
        );
        await db.Entry(analysis).ReloadAsync(ct);
        if (analysis.Status == "running")
            await decorator.PublishPendingAsync(analysis, ct);
        else
            await decorator.PublishResultAsync(analysis, GateEvaluation.Result(analysis), ct);
        await transaction.CommitAsync(ct);
    }
}

public sealed class AzureDevOpsDecorator(
    AppDbContext db,
    IHttpClientFactory clients,
    IOptions<IngestionOptions> options,
    AzureDevOpsAuthentication authentication
) : IPullRequestDecorator
{
    private const string Genre = "CodeInsights";
    private const string Name = "quality-gate";

    public async Task PublishPendingAsync(AnalysisRecord analysis, CancellationToken ct)
    {
        var (client, baseUrl, _) = await ClientAsync(analysis, ct);
        using (client)
            await StatusAsync(
                client,
                baseUrl,
                analysis,
                "pending",
                GateEvaluation.Summary("pending", 0),
                null,
                ct
            );
    }

    public async Task PublishResultAsync(
        AnalysisRecord analysis,
        QualityGateResult result,
        CancellationToken ct
    )
    {
        var (client, baseUrl, botIdentity) = await ClientAsync(analysis, ct);
        using (client)
        {
            var iteration = await CurrentIterationAsync(client, baseUrl, analysis, ct);
            if (iteration is null)
                return;
            var changes = iteration is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : await ChangesAsync(client, baseUrl, iteration.Value, ct);
            await CleanupAsync(client, baseUrl, botIdentity, ct);
            var selection = await PullRequestIssues.CurrentAsync(db, analysis.AnalysisId, ct);
            foreach (var issue in selection.Issues)
                await IssueAsync(client, baseUrl, analysis, issue, iteration, changes, ct);
            if (selection.OmittedCount > 0)
                await OverflowAsync(client, baseUrl, analysis, selection, ct);
            var state =
                analysis.Status == "failed" || result.Status == "unknown" ? "error"
                : result.Status == "pass" ? "succeeded"
                : "failed";
            await StatusAsync(client, baseUrl, analysis, state, result.Summary, iteration, ct);
        }
    }

    private async Task<(HttpClient Client, string BaseUrl, string BotIdentity)> ClientAsync(
        AnalysisRecord analysis,
        CancellationToken ct
    )
    {
        var org = analysis.PrAdoOrgUrl?.TrimEnd('/') ?? "";
        if (
            org.Length == 0
            || string.IsNullOrEmpty(analysis.PrAdoProject)
            || string.IsNullOrEmpty(analysis.PrRepoId)
        )
            throw new InvalidDataException("Incomplete Azure DevOps PR identity.");
        var config = await db.Set<AzureDevOpsOrganizationConfig>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationUrl == org && x.QualityGateEnabled, ct);
        if (
            config is null || string.IsNullOrEmpty(config.BotIdentityId)
        )
            throw new InvalidDataException("Azure DevOps requires a tested CodeInsights managed identity.");
        var integration = await db.Set<ProjectIntegration>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectKey == analysis.ProjectKey, ct);
        if (
            integration is null
            || !IngestionAdo.SameRepository(integration.RepoUrl, analysis.RepositoryUrl)
        )
            throw new InvalidDataException(
                "The Azure DevOps repository is not associated with this project."
            );
        var client = clients.CreateClient("pr-decoration");
        client.Timeout = TimeSpan.FromSeconds(30);
        await authentication.AuthorizeAsync(client, org, ct);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");
        return (
            client,
            $"{org}/{Uri.EscapeDataString(analysis.PrAdoProject)}/_apis/git/repositories/{Uri.EscapeDataString(analysis.PrRepoId)}/pullRequests/{Uri.EscapeDataString(analysis.PrId!)}",
            config.BotIdentityId
        );
    }

    private async Task StatusAsync(
        HttpClient client,
        string baseUrl,
        AnalysisRecord analysis,
        string state,
        string description,
        int? iteration,
        CancellationToken ct
    ) =>
        await WriteAsync(
            client,
            HttpMethod.Post,
            baseUrl + "/statuses?api-version=7.1",
            new
            {
                state,
                description = description[..Math.Min(200, description.Length)],
                iterationId = iteration,
                context = new { genre = Genre, name = Name },
                targetUrl = Target(analysis),
            },
            ct
        );

    private static async Task<int?> CurrentIterationAsync(
        HttpClient client,
        string baseUrl,
        AnalysisRecord analysis,
        CancellationToken ct
    )
    {
        using var pr = await GetAsync(client, baseUrl + "?api-version=7.1", ct);
        if (
            Text(pr.RootElement, "status") != "active"
            || Commit(pr.RootElement, "lastMergeSourceCommit") != analysis.PrSourceSha
        )
            return null;
        var iterations = await ValuesAsync(client, baseUrl + "/iterations?api-version=7.1", ct);
        return iterations
            // Azure DevOps can keep the iteration target stale after the target branch advances.
            // The live PR source check above is the identity boundary for the analyzed revision.
            .Where(x => Commit(x, "sourceRefCommit") == analysis.PrSourceSha)
            .Select(x => (int?)x.GetProperty("id").GetInt32())
            .Max();
    }

    private static async Task<Dictionary<string, int>> ChangesAsync(
        HttpClient client,
        string baseUrl,
        int iteration,
        CancellationToken ct
    )
    {
        using var document = await GetAsync(
            client,
            $"{baseUrl}/iterations/{iteration}/changes?$top=2000&api-version=7.1",
            ct
        );
        // ponytail: a PR above 2,000 changes will need ADO nextTop/nextSkip pagination.
        var rows = document
            .RootElement.GetProperty("changeEntries")
            .EnumerateArray()
            .Select(x => x.Clone())
            .ToList();
        return rows.Where(x =>
                Property(x, "item").ValueKind == JsonValueKind.Object
                && Text(Property(x, "item"), "path") is not null
                && Property(x, "changeTrackingId").TryGetInt32(out _)
            )
            .GroupBy(
                x => Text(Property(x, "item"), "path")!.TrimStart('/'),
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary(
                x => x.Key,
                x => x.First().GetProperty("changeTrackingId").GetInt32(),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static async Task CleanupAsync(
        HttpClient client,
        string baseUrl,
        string botIdentity,
        CancellationToken ct
    )
    {
        foreach (var thread in await ValuesAsync(client, baseUrl + "/threads?api-version=7.1", ct))
        {
            var properties = Property(thread, "properties");
            if (!Managed(properties) || !thread.TryGetProperty("id", out var tid))
                continue;
            var comments = Property(thread, "comments");
            if (comments.ValueKind != JsonValueKind.Array)
                continue;
            foreach (
                var comment in comments
                    .EnumerateArray()
                    .Where(x => Text(Property(x, "author"), "id") == botIdentity)
            )
            {
                using var response = await client.DeleteAsync(
                    $"{baseUrl}/threads/{tid.GetInt32()}/comments/{comment.GetProperty("id").GetInt32()}?api-version=7.1",
                    ct
                );
                await EnsureAsync(response, ct);
            }
        }
    }

    private static bool Managed(JsonElement properties)
    {
        var value = Property(properties, "CodeInsights.Managed");
        return value.ValueKind == JsonValueKind.True
            || Property(value, "$value").ValueKind == JsonValueKind.True
            || Property(value, "value").ValueKind == JsonValueKind.True
            || Text(value, "$value")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            || Text(value, "value")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task IssueAsync(
        HttpClient client,
        string baseUrl,
        AnalysisRecord analysis,
        FindingOccurrence issue,
        int? iteration,
        Dictionary<string, int> changes,
        CancellationToken ct
    )
    {
        object? threadContext = null;
        object? prContext = null;
        var path = issue.Path?.Replace('\\', '/').TrimStart('/');
        if (
            iteration is not null
            && issue.Line is > 0
            && path is not null
            && changes.TryGetValue(path, out var tracking)
        )
        {
            threadContext = new
            {
                filePath = "/" + path,
                rightFileStart = new { line = issue.Line, offset = 1 },
                rightFileEnd = new { line = issue.Line, offset = 1 },
            };
            prContext = new
            {
                iterationContext = new
                {
                    firstComparingIteration = 1,
                    secondComparingIteration = iteration,
                },
                changeTrackingId = tracking,
            };
        }
        var content =
            $"**{Escape(issue.Severity)}** · `{Escape(issue.RuleId)}`\n\n{Escape(issue.Message)}\n\n[Analysis details]({Target(analysis)})";
        var payload = new
        {
            comments = new[]
            {
                new
                {
                    parentCommentId = 0,
                    content,
                    commentType = 1,
                },
            },
            status = "active",
            threadContext,
            pullRequestThreadContext = prContext,
            properties = new Dictionary<string, object>
            {
                ["CodeInsights.Managed"] = new { type = "System.String", value = "true" },
                ["CodeInsights.AnalysisId"] = new
                {
                    type = "System.String",
                    value = analysis.AnalysisId,
                },
                ["CodeInsights.IssueId"] = new
                {
                    type = "System.String",
                    value = issue.Id.ToString(),
                },
                ["CodeInsights.Rule"] = new { type = "System.String", value = issue.RuleId },
            },
        };
        await WriteAsync(
            client,
            HttpMethod.Post,
            baseUrl + "/threads?api-version=7.1",
            payload,
            ct
        );
    }

    private async Task OverflowAsync(
        HttpClient client,
        string baseUrl,
        AnalysisRecord analysis,
        PullRequestIssueSelection selection,
        CancellationToken ct
    )
    {
        var content = PullRequestDetails.Truncation(
            selection,
            "shown here",
            Target(analysis)
        );
        await WriteAsync(
            client,
            HttpMethod.Post,
            baseUrl + "/threads?api-version=7.1",
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content,
                        commentType = 1,
                    },
                },
                status = "active",
                properties = new Dictionary<string, object>
                {
                    ["CodeInsights.Managed"] = new { type = "System.String", value = "true" },
                    ["CodeInsights.AnalysisId"] = new
                    {
                        type = "System.String",
                        value = analysis.AnalysisId,
                    },
                },
            },
            ct
        );
    }

    private string Target(AnalysisRecord analysis) =>
        PullRequestDetails.Url(options.Value.PlatformUrl, analysis.AnalysisId);

    private static string Escape(string value) =>
        WebUtility
            .HtmlEncode(value.Length > 500 ? value[..500] + "…" : value)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string? Commit(JsonElement value, string property) =>
        Text(Property(value, property), "commitId");

    private static JsonElement Property(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child)
            ? child
            : default;

    private static string? Text(JsonElement value, string property) =>
        Property(value, property) is { ValueKind: JsonValueKind.String } child
            ? child.GetString()
            : null;

    private static async Task<JsonDocument> GetAsync(
        HttpClient client,
        string url,
        CancellationToken ct
    )
    {
        using var response = await client.GetAsync(url, ct);
        await EnsureAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<List<JsonElement>> ValuesAsync(
        HttpClient client,
        string url,
        CancellationToken ct
    )
    {
        using var doc = await GetAsync(client, url, ct);
        return doc
            .RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(x => x.Clone())
            .ToList();
    }

    private static async Task WriteAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object payload,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload),
        };
        using var response = await client.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (
            response.StatusCode == HttpStatusCode.TooManyRequests
            && response.Headers.RetryAfter?.Delta is { } retry
        )
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry.TotalSeconds)), ct);
        throw new HttpRequestException(
            $"ADO HTTP {(int)response.StatusCode}",
            null,
            response.StatusCode
        );
    }
}

public sealed class GitHubOptions
{
    public long AppId { get; set; }
    public string PrivateKey { get; set; } = "";
    public string ApiUrl { get; set; } = "https://api.github.com";
}

public sealed class GitHubDecorator(
    AppDbContext db,
    IHttpClientFactory clients,
    IOptions<GitHubOptions> options,
    IOptions<IngestionOptions> ingestion
) : IPullRequestDecorator
{
    public async Task PublishPendingAsync(AnalysisRecord analysis, CancellationToken ct)
    {
        var (client, owner, repo) = await ClientAsync(analysis, ct);
        using (client)
        {
            if (analysis.GitHubCheckRunId is null)
                analysis.GitHubCheckRunId = await FindOrCreateAsync(
                    client,
                    owner,
                    repo,
                    analysis,
                    Details(analysis),
                    ct
                );
            await PatchAsync(
                client,
                owner,
                repo,
                analysis.GitHubCheckRunId.Value,
                new
                {
                    status = "in_progress",
                    output = new
                    {
                        title = "CodeInsights",
                        summary = GateEvaluation.Summary("pending", 0),
                    },
                },
                ct
            );
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task PublishResultAsync(
        AnalysisRecord analysis,
        QualityGateResult result,
        CancellationToken ct
    )
    {
        var (client, owner, repo) = await ClientAsync(analysis, ct);
        using (client)
        {
            analysis.GitHubCheckRunId ??= await FindOrCreateAsync(
                client,
                owner,
                repo,
                analysis,
                Details(analysis),
                ct
            );
            var selection = await PullRequestIssues.CurrentAsync(db, analysis.AnalysisId, ct);
            var annotations = selection
                .Issues
                .Select(x => new
                {
                    path = x.Path!.Replace('\\', '/').TrimStart('/'),
                    start_line = x.Line!.Value,
                    end_line = x.Line.Value,
                    annotation_level = Level(x.Severity),
                    message = x.Message[..Math.Min(65535, x.Message.Length)],
                    title = $"{x.RuleId} ({x.Severity})",
                })
                .ToList();
            var annotationsCount = await AnnotationCountAsync(
                client,
                owner,
                repo,
                analysis.GitHubCheckRunId.Value,
                ct
            );
            annotations = PendingAnnotations(annotations, annotationsCount);
            var summary =
                result.Summary
                + $"\n\n{result.ErrorCount} blocking issues\n{result.WarningCount} warnings\n{result.InfoCount} informational issues";
            if (selection.OmittedCount > 0)
                summary +=
                    "\n\n"
                    + PullRequestDetails.Truncation(selection, "annotated", Details(analysis));
            var chunks = annotations.Chunk(50).ToList();
            for (var i = 0; i < chunks.Count; i++)
            {
                var last = i == chunks.Count - 1;
                await PatchAsync(
                    client,
                    owner,
                    repo,
                    analysis.GitHubCheckRunId.Value,
                    new
                    {
                        status = last ? "completed" : "in_progress",
                        conclusion = last ? Conclusion(analysis, result) : null,
                        output = new
                        {
                            title = result.Status == "pass" ? "Quality Gate passed"
                            : result.Status == "fail" ? "Quality Gate failed"
                            : "Analysis failed",
                            summary,
                            annotations = chunks[i],
                        },
                    },
                    ct
                );
            }
            if (chunks.Count == 0)
                await PatchAsync(
                    client,
                    owner,
                    repo,
                    analysis.GitHubCheckRunId.Value,
                    new
                    {
                        status = "completed",
                        conclusion = Conclusion(analysis, result),
                        output = new
                        {
                            title = result.Status == "pass" ? "Quality Gate passed"
                            : result.Status == "fail" ? "Quality Gate failed"
                            : "Analysis failed",
                            summary,
                        },
                    },
                    ct
                );
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<(HttpClient Client, string Owner, string Repo)> ClientAsync(
        AnalysisRecord analysis,
        CancellationToken ct
    )
    {
        if (
            !Uri.TryCreate(analysis.RepositoryUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("A GitHub repository URL is required.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2)
            throw new InvalidDataException("Invalid GitHub repository URL.");
        var integration = await db.Set<ProjectIntegration>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectKey == analysis.ProjectKey, ct);
        if (
            integration is null
            || !IngestionAdo.SameRepository(integration.RepoUrl, analysis.RepositoryUrl)
        )
            throw new InvalidDataException(
                "The GitHub repository is not associated with this project."
            );
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        var client = clients.CreateClient("pr-decoration");
        client.BaseAddress = new Uri(options.Value.ApiUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeInsights/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Jwt());
        using var installation = await client.GetAsync($"repos/{parts[0]}/{repo}/installation", ct);
        await EnsureGitHubAsync(installation, ct);
        using var body = JsonDocument.Parse(await installation.Content.ReadAsStringAsync(ct));
        var id = body.RootElement.GetProperty("id").GetInt64();
        using var tokenResponse = await client.PostAsync(
            $"app/installations/{id}/access_tokens",
            null,
            ct
        );
        await EnsureGitHubAsync(tokenResponse, ct);
        using var tokenBody = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokenBody.RootElement.GetProperty("token").GetString()
        );
        return (client, parts[0], repo);
    }

    private string Jwt()
    {
        if (options.Value.AppId <= 0 || string.IsNullOrWhiteSpace(options.Value.PrivateKey))
            throw new InvalidDataException("GitHub App is not configured.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.Value.PrivateKey);
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(
                claims:
                [
                    new Claim(
                        "iat",
                        new DateTimeOffset(now.AddSeconds(-30)).ToUnixTimeSeconds().ToString(),
                        ClaimValueTypes.Integer64
                    ),
                ],
                expires: now.AddMinutes(9),
                issuer: options.Value.AppId.ToString(),
                signingCredentials: new SigningCredentials(
                    new RsaSecurityKey(rsa),
                    SecurityAlgorithms.RsaSha256
                )
            )
        );
    }

    private static async Task<long> FindOrCreateAsync(
        HttpClient client,
        string owner,
        string repo,
        AnalysisRecord analysis,
        string detailsUrl,
        CancellationToken ct
    )
    {
        using (
            var existingResponse = await client.GetAsync(
                $"repos/{owner}/{repo}/commits/{analysis.CommitSha}/check-runs?check_name=CodeInsights&per_page=100",
                ct
            )
        )
        {
            await EnsureGitHubAsync(existingResponse, ct);
            using var existingBody = JsonDocument.Parse(
                await existingResponse.Content.ReadAsStringAsync(ct)
            );
            var existing = existingBody
                .RootElement.GetProperty("check_runs")
                .EnumerateArray()
                .Where(x =>
                    x.TryGetProperty("external_id", out var external)
                    && external.GetString() == analysis.AnalysisId
                )
                .Select(x => x.GetProperty("id").GetInt64())
                .DefaultIfEmpty(0)
                .Max();
            if (existing > 0)
                return existing;
        }
        using var response = await client.PostAsJsonAsync(
            $"repos/{owner}/{repo}/check-runs",
            new
            {
                name = "CodeInsights",
                head_sha = analysis.CommitSha,
                status = "in_progress",
                external_id = analysis.AnalysisId,
                details_url = detailsUrl,
                output = new
                {
                    title = "CodeInsights",
                    summary = GateEvaluation.Summary("pending", 0),
                },
            },
            ct
        );
        await EnsureGitHubAsync(response, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task PatchAsync(
        HttpClient client,
        string owner,
        string repo,
        long id,
        object payload,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"repos/{owner}/{repo}/check-runs/{id}"
        )
        {
            Content = JsonContent.Create(payload),
        };
        using var response = await client.SendAsync(request, ct);
        await EnsureGitHubAsync(response, ct);
    }

    private static async Task<int> AnnotationCountAsync(
        HttpClient client,
        string owner,
        string repo,
        long id,
        CancellationToken ct
    )
    {
        using var response = await client.GetAsync($"repos/{owner}/{repo}/check-runs/{id}", ct);
        await EnsureGitHubAsync(response, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.TryGetProperty("annotations_count", out var count)
            ? count.GetInt32()
            : 0;
    }

    private static string Level(string severity) =>
        severity.ToLowerInvariant() is "blocker" or "critical" or "error" or "high" ? "failure"
        : severity.ToLowerInvariant() is "warning" or "medium" ? "warning"
        : "notice";

    private static string Conclusion(AnalysisRecord analysis, QualityGateResult result) =>
        analysis.Status == "failed" || result.Status == "unknown" ? "neutral"
        : result.Status == "pass" ? "success"
        : "failure";

    private string Details(AnalysisRecord analysis) =>
        PullRequestDetails.Url(ingestion.Value.PlatformUrl, analysis.AnalysisId);

    internal static List<T> PendingAnnotations<T>(List<T> annotations, int annotationsCount) =>
        annotationsCount >= annotations.Count ? [] : annotations.Skip(annotationsCount).ToList();

    private static async Task EnsureGitHubAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"GitHub HTTP {(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 200)]}",
            null,
            response.StatusCode
        );
    }
}
