using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Services;

/// <summary>
/// Creates one Azure Boards work item per durable finding and keeps its link. The scan stays the source
/// of truth: closing the work item never changes the finding, resolving the finding only adds a comment.
/// </summary>
public sealed class FindingWorkItems(
    AppDbContext db,
    IHttpClientFactory clients,
    AzureDevOpsAuthentication authentication,
    IOptions<IngestionOptions> options
)
{
    public const string ApiVersion = "7.1";
    public const string CommentsApiVersion = "7.1-preview.4";
    public static readonly string[] SeverityOrder = ["blocker", "high", "medium", "low", "info"];

    public sealed record Target(string OrganizationUrl, string Project);

    public static string Tag(string findingId) => "codeinsights-finding-" + findingId;

    /// <summary>Azure DevOps organization and project of a canonical dev.azure.com repository URL.</summary>
    public static Target? AdoProject(string? repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 && segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase)
            ? new Target("https://dev.azure.com/" + segments[0].ToLowerInvariant(), segments[1])
            : null;
    }

    /// <summary>Where the project's work items go, or why it cannot receive any.</summary>
    public static async Task<(Target? Target, string? Reason)> ResolveAsync(AppDbContext db, string projectKey, CancellationToken ct)
    {
        var repository = await db.Set<ProjectIntegration>().AsNoTracking().Where(x => x.ProjectKey == projectKey)
            .Select(x => x.RepoUrl).FirstOrDefaultAsync(ct);
        if (AdoProject(repository) is not { } target)
            return (null, "The project has no Azure DevOps repository link");
        var enabled = await db.Set<AzureDevOpsOrganizationConfig>().AnyAsync(x => x.OrganizationUrl == target.OrganizationUrl
            && x.WorkItemsEnabled && x.BotIdentityId != null && x.BotIdentityId != "", ct);
        return enabled ? (target, null) : (null, "Work items are not enabled for this project's tested Azure DevOps organization");
    }

    /// <summary>Records the request and queues its creation; an existing pending or created link is kept.</summary>
    public static async Task<FindingWorkItem> RequestAsync(AppDbContext db, DurableFinding finding, Target target, string origin, string requestedBy, CancellationToken ct)
    {
        var row = await db.Set<FindingWorkItem>().FindAsync([finding.Id], ct);
        if (row is { State: "pending" or "created" }) return row;
        if (row is null)
        {
            row = new FindingWorkItem { FindingId = finding.Id, ProjectKey = finding.ProjectKey };
            db.Add(row);
        }
        row.OrganizationUrl = target.OrganizationUrl;
        row.AdoProject = target.Project;
        row.State = "pending";
        row.Origin = origin;
        row.RequestedBy = requestedBy;
        row.RequestedAt = DateTime.UtcNow;
        row.LastError = null;
        await EnqueueAsync(db, "work-item", finding.Id, ct);
        return row;
    }

    public static Task<int> EnqueueAsync(AppDbContext db, string kind, string resource, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO durable_jobs (id,kind,resource_id,state,attempts,available_at,error,created_at) VALUES ({Guid.NewGuid().ToString()},{kind},{resource},'pending',0,{now},'',{now}) ON CONFLICT (kind,resource_id) DO UPDATE SET state = 'pending', attempts = 0, available_at = EXCLUDED.available_at, error = '' WHERE durable_jobs.state IN ('completed','failed')",
            ct);
    }

    public static object View(FindingWorkItem row) => new
    {
        row.FindingId,
        row.State,
        row.WorkItemId,
        row.Url,
        row.Origin,
        row.RequestedBy,
        row.RequestedAt,
        row.CreatedAt,
        row.LastError,
    };

    /// <summary>Job <c>work-item</c>: creates the work item, or adopts the one a lost response already created.</summary>
    public async Task CreateAsync(string findingId, CancellationToken ct)
    {
        var row = await db.Set<FindingWorkItem>().FindAsync([findingId], ct);
        if (row is not { State: "pending" }) return;
        var finding = await db.Set<DurableFinding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == findingId, ct)
            ?? throw new InvalidDataException("The finding no longer exists");
        var (target, reason) = await ResolveAsync(db, finding.ProjectKey, ct);
        if (target is null) throw new InvalidDataException(reason!);
        var settings = await db.Set<WorkItemSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.ProjectKey == finding.ProjectKey, ct)
            ?? new WorkItemSettings { ProjectKey = finding.ProjectKey };
        using var client = await ClientAsync(target.OrganizationUrl, ct);
        var projectUrl = $"{target.OrganizationUrl}/{Uri.EscapeDataString(target.Project)}";
        // Creation is not idempotent in Azure DevOps: a retry first looks for the item tagged with this finding.
        var id = await FindTaggedAsync(client, projectUrl, findingId, ct)
            ?? await CreateItemAsync(client, projectUrl, settings, finding, HubUrl(finding), ct);
        row.State = "created";
        row.WorkItemId = id;
        row.Url = $"{projectUrl}/_workitems/edit/{id}";
        row.OrganizationUrl = target.OrganizationUrl;
        row.AdoProject = target.Project;
        row.CreatedAt = DateTime.UtcNow;
        row.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Job <c>work-item-comment</c> (<c>finding|version</c>): tells the work item the finding was resolved.</summary>
    public async Task CommentResolvedAsync(string resource, CancellationToken ct)
    {
        var findingId = resource.Split('|')[0];
        var row = await db.Set<FindingWorkItem>().AsNoTracking().SingleOrDefaultAsync(x => x.FindingId == findingId, ct);
        if (row is null or { State: "failed" }) return;
        if (row.State == "pending" || row.WorkItemId is null)
            throw new HttpRequestException("The work item is not created yet");
        var finding = await db.Set<DurableFinding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == findingId, ct);
        if (finding is null) return;
        var decision = await db.Set<FindingDecision>().AsNoTracking().Where(x => x.FindingId == findingId && x.Action == "review")
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        using var client = await ClientAsync(row.OrganizationUrl, ct);
        var projectUrl = $"{row.OrganizationUrl}/{Uri.EscapeDataString(row.AdoProject)}";
        using var response = await client.PostAsJsonAsync(
            $"{projectUrl}/_apis/wit/workItems/{row.WorkItemId}/comments?api-version={CommentsApiVersion}",
            new { text = ResolutionComment(finding, decision, HubUrl(finding)) }, ct);
        await EnsureAsync(response, "comment on the work item", ct);
    }

    /// <summary>Marks the link failed once its job has exhausted its retries.</summary>
    public static Task<int> FailAsync(AppDbContext db, string findingId, string error, CancellationToken ct) =>
        db.Set<FindingWorkItem>().Where(x => x.FindingId == findingId && x.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "failed").SetProperty(x => x.LastError, error), ct);

    /// <summary>
    /// Automatic rules on the main branch: SLA breaches first, then severities from the highest, bounded per
    /// project and per run. Only findings detected, or deadlines due, after the rules were enabled qualify.
    /// </summary>
    public static async Task<int> ApplyRulesAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var created = 0;
        var projects = await db.Set<WorkItemSettings>().AsNoTracking()
            .Where(x => x.AutoSince != null && (x.AutoMinSeverity != null || x.AutoOnSlaBreach)).ToListAsync(ct);
        foreach (var settings in projects)
        {
            var (target, _) = await ResolveAsync(db, settings.ProjectKey, ct);
            var mainBranch = await db.Set<Project>().Where(x => x.Key == settings.ProjectKey).Select(x => x.MainBranch).SingleOrDefaultAsync(ct);
            if (target is null || string.IsNullOrEmpty(mainBranch)) continue;
            var branch = BranchRefs.Canonical(mainBranch);
            var since = settings.AutoSince!.Value;
            var open = db.Set<DurableFinding>().Where(x => x.ProjectKey == settings.ProjectKey && x.Branch == branch
                && (x.State == "open" || x.State == "confirmed" || (x.State == "false_positive" || x.State == "accepted_risk") && x.ExceptionExpiresAt <= now)
                && !db.Set<FindingWorkItem>().Any(w => w.FindingId == x.Id));
            var picked = new List<(string Id, string Origin)>();
            if (settings.AutoOnSlaBreach)
                picked.AddRange((await FindingSla.FilterStatus(open, "breached", now).Where(x => x.SlaDueAt >= since)
                    .OrderBy(x => x.SlaDueAt).ThenBy(x => x.Id).Select(x => x.Id).Take(settings.AutoMaxPerRun).ToListAsync(ct))
                    .Select(id => (id, "sla_rule")));
            if (settings.AutoMinSeverity is { } minimum)
                foreach (var severity in AtLeast(minimum))
                {
                    var room = settings.AutoMaxPerRun - picked.Count;
                    if (room <= 0) break;
                    var taken = picked.Select(x => x.Id).ToArray();
                    picked.AddRange((await open.Where(x => x.Severity == severity && x.FirstDetectedAt >= since && !taken.Contains(x.Id))
                        .OrderBy(x => x.FirstDetectedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(room).ToListAsync(ct))
                        .Select(id => (id, "severity_rule")));
                }
            if (picked.Count == 0) continue;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var (id, origin) in picked.Take(settings.AutoMaxPerRun))
            {
                var finding = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
                await RequestAsync(db, finding, target, origin, "rule", ct);
                created++;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
        return created;
    }

    public static string[] AtLeast(string minimum) =>
        SeverityOrder.Contains(minimum) ? SeverityOrder[..(Array.IndexOf(SeverityOrder, minimum) + 1)] : [];

    public static string Title(DurableFinding finding)
    {
        var title = $"[CodeInsights] {finding.Severity} · {finding.Tool}/{finding.RuleId}" + (finding.Path is null ? "" : $" · {finding.Path}");
        return title.Length <= 255 ? title : title[..254] + "…";
    }

    public static string Description(DurableFinding finding, string? hubUrl)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var sla = FindingSla.Status(finding, DateTime.UtcNow);
        var builder = new StringBuilder()
            .Append($"<p>{E(finding.Message)}</p><ul>")
            .Append($"<li><b>Rule</b>: {E(finding.Tool)}/{E(finding.RuleId)}</li>")
            .Append($"<li><b>Severity</b>: {E(finding.Severity)} · <b>Category</b>: {E(finding.Category)}</li>")
            .Append($"<li><b>Location</b>: {E(finding.Path ?? "no file")}{(finding.Line is null ? "" : ":" + finding.Line)} on {E(finding.Branch)}</li>")
            .Append($"<li><b>SLA</b>: {E(sla)}{(finding.SlaDueAt is { } due ? ", due " + due.ToString("yyyy-MM-dd") : "")}</li></ul>");
        if (hubUrl is not null) builder.Append($"<p><a href=\"{E(hubUrl)}\">Open the finding in CodeInsights</a></p>");
        return builder.Append($"<p>CodeInsights finding {E(finding.Id)}. Closing this work item does not change the finding.</p>").ToString();
    }

    public static IReadOnlyList<object> Fields(WorkItemSettings settings, DurableFinding finding, string? hubUrl)
    {
        var fields = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = Title(finding) },
            new { op = "add", path = "/fields/System.Description", value = Description(finding, hubUrl) },
            new { op = "add", path = "/fields/System.Tags", value = "CodeInsights; " + Tag(finding.Id) },
        };
        if (!string.IsNullOrWhiteSpace(settings.AreaPath)) fields.Add(new { op = "add", path = "/fields/System.AreaPath", value = settings.AreaPath });
        if (!string.IsNullOrWhiteSpace(settings.IterationPath)) fields.Add(new { op = "add", path = "/fields/System.IterationPath", value = settings.IterationPath });
        if (hubUrl is not null)
            fields.Add(new { op = "add", path = "/relations/-", value = new { rel = "Hyperlink", url = hubUrl, attributes = new { comment = "CodeInsights finding" } } });
        return fields;
    }

    public static string ResolutionComment(DurableFinding finding, FindingDecision? decision, string? hubUrl)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var who = decision is null ? "" : $" by {E(decision.ActorEmail)} on {decision.CreatedAt:yyyy-MM-dd}";
        var why = string.IsNullOrWhiteSpace(decision?.Reason) ? "" : $": {E(decision!.Reason)}";
        var link = hubUrl is null ? "" : $" <a href=\"{E(hubUrl)}\">Open in CodeInsights</a>";
        return $"<p>The finding was resolved in CodeInsights{who}{why}.{link}</p><p>This comment does not close the work item.</p>";
    }

    private string? HubUrl(DurableFinding finding) =>
        options.Value.PlatformUrl.Length == 0 || finding.LastAnalysisId.Length == 0
            ? null
            : PullRequestDetails.Url(options.Value.PlatformUrl, finding.LastAnalysisId);

    private async Task<HttpClient> ClientAsync(string organizationUrl, CancellationToken ct)
    {
        var client = clients.CreateClient("work-items");
        client.Timeout = TimeSpan.FromSeconds(30);
        await authentication.AuthorizeAsync(client, organizationUrl, ct);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");
        return client;
    }

    private static async Task<int?> FindTaggedAsync(HttpClient client, string projectUrl, string findingId, CancellationToken ct)
    {
        var query = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Tags] CONTAINS '{Tag(findingId).Replace("'", "''")}'";
        using var response = await client.PostAsJsonAsync($"{projectUrl}/_apis/wit/wiql?api-version={ApiVersion}", new { query }, ct);
        await EnsureAsync(response, "search work items", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("workItems", out var items)
            ? items.EnumerateArray().Select(x => (int?)x.GetProperty("id").GetInt32()).Min()
            : null;
    }

    private static async Task<int> CreateItemAsync(HttpClient client, string projectUrl, WorkItemSettings settings, DurableFinding finding, string? hubUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{projectUrl}/_apis/wit/workitems/${Uri.EscapeDataString(settings.WorkItemType)}?api-version={ApiVersion}")
        {
            Content = new StringContent(JsonSerializer.Serialize(Fields(settings, finding, hubUrl)), Encoding.UTF8, "application/json-patch+json"),
        };
        using var response = await client.SendAsync(request, ct);
        await EnsureAsync(response, $"create a {settings.WorkItemType} work item", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Refusals are terminal with Azure DevOps' own message; throttling and server errors are retried.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        if (code is 400 or 401 or 403 or 404)
        {
            var detail = await MessageAsync(response, ct);
            var hint = code is 401 or 403 ? " The CodeInsights managed identity needs Work Items (Read & Write) on this project." : "";
            throw new InvalidDataException($"Azure DevOps refused to {action} (HTTP {code}){(detail is null ? "." : ": " + detail)}{hint}");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta is { } retry)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retry.TotalSeconds)), ct);
        throw new HttpRequestException($"ADO HTTP {code}", null, response.StatusCode);
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(message) ? null : message.Length <= 300 ? message : message[..300];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
