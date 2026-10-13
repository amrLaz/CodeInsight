using System.Net;
using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Services;

/// <summary>Delivers durable ingestion results to the verified Azure DevOps PR iteration.</summary>
public sealed class IngestionAdo(AppDbContext db, IHttpClientFactory clients, IOptions<IngestionOptions> options, AzureDevOpsAuthentication authentication)
{
    private const string Marker = "<!-- codeinsights:quality-gate -->";

    public async Task SendAsync(IngestionRecord row, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize<IngestionManifest>(row.Manifest, ApiJson.Options) ?? throw new InvalidDataException("Invalid ingestion manifest.");
        if (manifest.CiProvider != "azure-devops" || string.IsNullOrEmpty(manifest.PrId)) return;
        var org = manifest.PrAdoOrgUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(manifest.PrAdoProject) || string.IsNullOrEmpty(manifest.PrRepoId))
            throw new InvalidDataException("Incomplete Azure DevOps PR identity.");
        var links = await db.Set<ProjectIntegration>().AsNoTracking().Where(x => x.ProjectKey == row.ProjectKey).ToListAsync(ct);
        if (!links.Any(x => SameRepository(x.RepoUrl, manifest.RepositoryUrl)))
            throw new InvalidDataException("The repository is not associated with this project.");
        var config = await db.Set<AzureDevOpsOrganizationConfig>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationUrl == org && x.QualityGateEnabled, ct);
        if (config is null) throw new InvalidDataException("No enabled Azure DevOps integration for this organization.");
        if (!Uri.TryCreate(org, UriKind.Absolute, out var orgUri) || orgUri.Scheme != "https" || !Uri.TryCreate(manifest.RepositoryUrl, UriKind.Absolute, out var repository)
            || !new Uri(org + "/").IsBaseOf(repository))
            throw new InvalidDataException("Repository is outside the configured organization.");
        var policy = await db.Set<IngestionProjectPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ProjectKey == row.ProjectKey, ct);
        using var client = clients.CreateClient("pr-decoration");
        client.Timeout = TimeSpan.FromSeconds(30);
        await authentication.AuthorizeAsync(client, org, ct);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");
        var baseUrl = $"{org}/{Uri.EscapeDataString(manifest.PrAdoProject)}/_apis/git/repositories/{Uri.EscapeDataString(manifest.PrRepoId)}/pullRequests/{Uri.EscapeDataString(manifest.PrId)}";
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // ponytail: serialize only one PR's deliveries across workers; revisit long transactions if ADO latency dominates.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({baseUrl}, 0))", ct);
        if (await db.Set<IngestionRecord>().AnyAsync(x => x.AnalysisId == row.AnalysisId && x.Revision > row.Revision, ct)) return;
        await DeliverAsync(client, baseUrl, manifest, row, policy?.Enforcement ?? "informative", options.Value.PlatformUrl, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Compares canonical repository URLs without accepting host, query, or path-prefix aliases.</summary>
    public static bool SameRepository(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var a) && Uri.TryCreate(right, UriKind.Absolute, out var b)
        && a.Scheme == "https" && b.Scheme == "https" && a.UserInfo.Length == 0 && b.UserInfo.Length == 0
        && a.Query.Length == 0 && b.Query.Length == 0 && a.Fragment.Length == 0 && b.Fragment.Length == 0
        && a.GetLeftPart(UriPartial.Authority).Equals(b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
        && Uri.UnescapeDataString(a.AbsolutePath).TrimEnd('/').Equals(Uri.UnescapeDataString(b.AbsolutePath).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP delivery seam; returns false when the analyzed PR iteration is obsolete.</summary>
    public static async Task<bool> DeliverAsync(HttpClient client, string baseUrl, IngestionManifest manifest, IngestionRecord row, string enforcement, string platformUrl, CancellationToken ct)
    {
        using var pr = await GetAsync(client, baseUrl + "?api-version=7.1", ct);
        if (!Current(pr.RootElement, manifest)) return false;
        var adoRepositoryUrl = RepositoryUrls.Normalize(
            "azure-devops",
            Text(Property(pr.RootElement, "repository"), "remoteUrl")
        );
        if (!SameRepository(adoRepositoryUrl, manifest.RepositoryUrl)
            || !string.Equals(Text(Property(pr.RootElement, "repository"), "id"), manifest.PrRepoId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Azure DevOps PR repository does not match the publication.");
        var iterations = await ValuesAsync(client, baseUrl + "/iterations?api-version=7.1", ct);
        var iteration = iterations.Where(x => Commit(x, "sourceRefCommit") == manifest.SourceSha && Commit(x, "targetRefCommit") == manifest.TargetSha)
            .Select(x => x.GetProperty("id").GetInt32()).DefaultIfEmpty(0).Max();
        if (iteration <= 0) return false;
        var informative = enforcement != "blocking";
        var technical = row.TechnicalState is "complete" or "success";
        var state = row.State is "receiving" or "queued" ? "pending" : !technical || row.QualityGate == "unknown" ? "error" : informative || row.QualityGate == "pass" ? "succeeded" : "failed";
        var description = $"Quality Gate ({enforcement}): {row.QualityGate.ToUpperInvariant()} | technical: {row.TechnicalState}";
        var targetUrl = platformUrl.Length > 0 ? $"{platformUrl.TrimEnd('/')}/?analysis_id={Uri.EscapeDataString(row.AnalysisId)}" : "";
        var payload = new { state, description, iterationId = iteration, context = new { name = "platform-quality-gate", genre = "static-analysis" }, targetUrl };
        var statuses = await ValuesAsync(client, baseUrl + "/statuses?api-version=7.1", ct);
        var existing = statuses.Where(x => Text(Property(x, "context"), "name") == "platform-quality-gate" && Text(Property(x, "context"), "genre") == "static-analysis"
            && Property(x, "iterationId").ValueKind == JsonValueKind.Number && x.GetProperty("iterationId").GetInt32() == iteration)
            .OrderByDescending(x => x.GetProperty("id").GetInt32()).FirstOrDefault();
        // Check again after remote reads; iteration-scoped status cannot approve a later source revision.
        using (var latest = await GetAsync(client, baseUrl + "?api-version=7.1", ct)) if (!Current(latest.RootElement, manifest)) return false;
        // ADO status PATCH only removes entries. POST replaces the effective context status; identical retries need no write.
        if (existing.ValueKind == JsonValueKind.Undefined || Text(existing, "state") != state || Text(existing, "description") != description || Text(existing, "targetUrl") != targetUrl)
            await WriteAsync(client, HttpMethod.Post, baseUrl + "/statuses?api-version=7.1", payload, ct);

        var content = $"{Marker}\nQuality Gate **{row.QualityGate.ToUpperInvariant()}** ({enforcement})\n\nTechnical state: {row.TechnicalState}\nAnalyzed commit: `{manifest.CommitSha}`\nPR source: `{manifest.SourceSha}`\nTarget: `{manifest.TargetSha}`\nRevision: {row.Revision}";
        if (row.Error.Length > 0) content += "\nTechnical error: " + Escape(row.Error);
        using (var snapshot = JsonDocument.Parse(row.GateSnapshot))
        {
            var conditions = Property(snapshot.RootElement, "conditions");
            if (conditions.ValueKind == JsonValueKind.Array)
            {
                var failing = conditions.EnumerateArray().Where(x => Text(x, "result") is "fail" or "unknown" || Text(x, "state") is "fail" or "unknown").ToList();
                if (failing.Count > 0) content += "\n\nGate conditions requiring attention:";
                foreach (var condition in failing.Take(10))
                    content += $"\n- {Escape(Text(condition, "metric") ?? "unknown")}: {Escape(Scalar(Property(condition, "value")))} {Escape(Text(condition, "operator") ?? Text(condition, "condition") ?? "unknown")} {Escape(Scalar(Property(condition, "threshold")))} ({Escape(Text(condition, "result") ?? Text(condition, "state") ?? "unknown")})";
                if (failing.Count > 10) content += $"\n- {failing.Count - 10} additional conditions in the analysis.";
            }
        }
        using (var comparison = JsonDocument.Parse(row.Comparison))
        {
            var detail = comparison.RootElement;
            if (Text(detail, "state") == "available")
            {
                content += $"\n\nNew Code: new {Scalar(Property(detail, "new_count"))}, persistent {Scalar(Property(detail, "persistent_count"))}, resolved {Scalar(Property(detail, "resolved_count"))}, indeterminate {Scalar(Property(detail, "indeterminate_count"))}.";
                content += $"\nChanged executable-line coverage: {Scalar(Property(detail, "coverage"))}";
                var findings = Property(detail, "findings");
                if (findings.ValueKind == JsonValueKind.Array)
                    foreach (var finding in findings.EnumerateArray().Take(10))
                        content += $"\n- {Escape(Text(finding, "state") ?? "indeterminate")}: {Escape(Text(finding, "tool") ?? "unknown")}/{Escape(Text(finding, "rule_id") ?? "unknown")} — {Escape(Text(finding, "path") ?? "location unavailable")}:{Scalar(Property(finding, "line"))}";
                if (Property(detail, "findings_truncated").ValueKind == JsonValueKind.True || findings.ValueKind == JsonValueKind.Array && findings.GetArrayLength() > 10)
                    content += "\n- Additional findings in the analysis.";
            }
            else content += "\n\nNew Code unavailable: " + Escape(Text(detail, "reason") ?? "not_computed");
        }
        if (targetUrl.Length > 0) content += $"\n\n[Analysis details]({targetUrl})";
        var threads = await ValuesAsync(client, baseUrl + "/threads?api-version=7.1", ct);
        var matching = threads.SelectMany(t => Property(t, "comments").ValueKind == JsonValueKind.Array
            ? t.GetProperty("comments").EnumerateArray().Where(c => Text(c, "content")?.StartsWith(Marker, StringComparison.Ordinal) == true).Select(c => (Thread: t.GetProperty("id").GetInt32(), Comment: c.GetProperty("id").GetInt32()))
            : []).OrderBy(x => x.Thread).ThenBy(x => x.Comment).FirstOrDefault();
        using (var latest = await GetAsync(client, baseUrl + "?api-version=7.1", ct)) if (!Current(latest.RootElement, manifest)) return false;
        if (matching.Thread > 0)
            await WriteAsync(client, HttpMethod.Patch, $"{baseUrl}/threads/{matching.Thread}/comments/{matching.Comment}?api-version=7.1", new { content }, ct);
        else
            await WriteAsync(client, HttpMethod.Post, baseUrl + "/threads?api-version=7.1", new { comments = new[] { new { parentCommentId = 0, content, commentType = 1 } }, status = "active" }, ct);
        return true;
    }

    private static bool Current(JsonElement pr, IngestionManifest manifest) =>
        Text(pr, "status") == "active" && manifest.SourceSha.Length > 0 && manifest.TargetSha.Length > 0
        && Commit(pr, "lastMergeSourceCommit") == manifest.SourceSha && Commit(pr, "lastMergeTargetCommit") == manifest.TargetSha
        && (manifest.CommitSha == manifest.SourceSha || Commit(pr, "lastMergeCommit") == manifest.CommitSha);
    private static string? Commit(JsonElement value, string property) => Text(Property(value, property), "commitId");
    private static JsonElement Property(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) ? child : default;
    private static string? Text(JsonElement value, string property) => Property(value, property) is { ValueKind: JsonValueKind.String } child ? child.GetString() : null;
    private static string Scalar(JsonElement value) => value.ValueKind switch { JsonValueKind.Number => value.GetRawText(), JsonValueKind.String => value.GetString() ?? "not measured", _ => "not measured" };
    private static string Escape(string value)
    {
        var safe = WebUtility.HtmlEncode(value.Length > 250 ? value[..250] + "…" : value).Replace('\r', ' ').Replace('\n', ' ');
        foreach (var character in new[] { "\\", "`", "*", "_", "[", "]", "(", ")", "#", "!", "|", "~" }) safe = safe.Replace(character, "\\" + character, StringComparison.Ordinal);
        return safe;
    }
    private static async Task<JsonDocument> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        await EnsureAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static async Task<List<JsonElement>> ValuesAsync(HttpClient client, string url, CancellationToken ct)
    {
        List<JsonElement> values = [];
        string? continuation = null;
        for (var page = 0; page < 100; page++)
        {
            using var response = await client.GetAsync(url + (continuation is null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation)), ct);
            await EnsureAsync(response, ct);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            values.AddRange(body.RootElement.GetProperty("value").EnumerateArray().Select(x => x.Clone()));
            continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens) ? tokens.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(continuation)) return values;
        }
        throw new InvalidDataException("Azure DevOps pagination limit exceeded.");
    }
    private static async Task WriteAsync(HttpClient client, HttpMethod method, string url, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(payload) };
        using var response = await client.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
            if (retry > TimeSpan.Zero) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry.TotalSeconds)), ct);
        }
        throw new HttpRequestException($"ADO HTTP {(int)response.StatusCode}", null, response.StatusCode);
    }
}
