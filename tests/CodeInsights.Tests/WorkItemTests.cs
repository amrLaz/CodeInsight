using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using CodeInsights.Api;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class WorkItemTests
{
    private static DurableFinding Finding(string message = "problem") => new()
    {
        Id = new string('a', 64),
        ProjectKey = "boards",
        Branch = "refs/heads/main",
        Tool = "semgrep",
        RuleId = "csharp.sqli",
        Category = "vulnerability",
        Message = message,
        Path = "src/Api.cs",
        Line = 12,
        Severity = "high",
        LastAnalysisId = "analysis-1",
    };

    [Theory]
    [InlineData("https://dev.azure.com/Up-Group/Up.France.ODI/_git/Repo", "https://dev.azure.com/up-group", "Up.France.ODI")]
    [InlineData("https://dev.azure.com/up-group/My%20Project/_git/Repo", "https://dev.azure.com/up-group", "My Project")]
    public void Azure_DevOps_project_comes_from_the_repository_link(string url, string organization, string project) =>
        Assert.Equal(new FindingWorkItems.Target(organization, project), FindingWorkItems.AdoProject(url));

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com/org/repo")]
    [InlineData("https://dev.azure.com/up-group/project")]
    [InlineData("http://dev.azure.com/up-group/project/_git/repo")]
    public void Anything_but_an_Azure_Repos_link_has_no_target(string? url) => Assert.Null(FindingWorkItems.AdoProject(url));

    [Fact]
    public void Fields_escape_scanner_text_and_only_send_configured_paths()
    {
        var finding = Finding("<script>alert(1)</script>");
        var fields = JsonSerializer.Serialize(FindingWorkItems.Fields(new WorkItemSettings(), finding, "https://hub.local/?analysis_id=analysis-1"));
        var description = FindingWorkItems.Description(finding, null);
        Assert.DoesNotContain("<script>", description);
        Assert.Contains("&lt;script&gt;", description);
        Assert.Contains("codeinsights-finding-" + finding.Id, fields);
        Assert.Contains("Hyperlink", fields);
        Assert.DoesNotContain("System.AreaPath", fields);
        var configured = JsonSerializer.Serialize(FindingWorkItems.Fields(new WorkItemSettings { AreaPath = "Team\\Quality", IterationPath = "Sprint 1" }, finding, null));
        Assert.Contains("System.AreaPath", configured);
        Assert.Contains("System.IterationPath", configured);
        Assert.DoesNotContain("Hyperlink", configured);
        var longPath = Finding();
        longPath.Path = new string('p', 400);
        Assert.True(FindingWorkItems.Title(longPath).Length <= 255);
    }

    [Fact]
    public void Severity_rules_include_every_level_above_the_minimum()
    {
        Assert.Equal(["blocker", "high", "medium"], FindingWorkItems.AtLeast("medium"));
        Assert.Equal(["blocker"], FindingWorkItems.AtLeast("blocker"));
        Assert.Empty(FindingWorkItems.AtLeast("critical"));
    }

    [Fact]
    public async Task Creation_adoption_refusal_and_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_DB to run work item PostgreSQL tests.");
        // The API under test runs a worker on the shared queue: queued jobs are checked on a private database.
        var isolated = new NpgsqlConnectionStringBuilder(connection) { Database = "codeinsights_boards_" + Guid.NewGuid().ToString("N")[..12], Pooling = false };
        await ExecuteAsync(connection, $"CREATE DATABASE \"{isolated.Database}\"", ct);
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await db.Database.MigrateAsync(ct);
            db.AddRange(
                new Project { Id = Guid.NewGuid().ToString(), Key = "boards", Name = "boards", MainBranch = "refs/heads/main" },
                new ProjectIntegration { Id = Guid.NewGuid().ToString(), ProjectKey = "boards", RepoProvider = "azure-devops", RepoUrl = "https://dev.azure.com/up-group/Quality/_git/app", PipelineProvider = "azure-devops" },
                new AzureDevOpsOrganizationConfig { ConfigId = "org", ConfigName = "org", OrganizationUrl = "https://dev.azure.com/up-group", WorkItemsEnabled = false, BotIdentityId = "bot" });
            var finding = Finding();
            db.Add(finding);
            await db.SaveChangesAsync(ct);

            var (target, reason) = await FindingWorkItems.ResolveAsync(db, "boards", ct);
            Assert.Null(target);
            Assert.Contains("not enabled", reason);
            await db.Set<AzureDevOpsOrganizationConfig>().ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkItemsEnabled, true), ct);
            (target, _) = await FindingWorkItems.ResolveAsync(db, "boards", ct);
            Assert.Equal(new FindingWorkItems.Target("https://dev.azure.com/up-group", "Quality"), target);

            // First creation: nothing tagged yet, so the item is created with a JSON Patch document.
            var ado = new AzureDevOpsStub();
            ado.Respond("/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[]}""");
            ado.Respond("/_apis/wit/workitems/$Bug", HttpStatusCode.OK, """{"id":42}""");
            await FindingWorkItems.RequestAsync(db, finding, target!, "manual", "dev@up.coop", ct);
            await db.SaveChangesAsync(ct);
            Assert.Single(await db.Set<DurableJob>().Where(x => x.Kind == "work-item" && x.ResourceId == finding.Id).ToListAsync(ct));
            db.ChangeTracker.Clear();
            await Service(db, ado).CreateAsync(finding.Id, ct);
            var row = await db.Set<FindingWorkItem>().AsNoTracking().SingleAsync(x => x.FindingId == finding.Id, ct);
            Assert.Equal(("created", 42, "https://dev.azure.com/up-group/Quality/_workitems/edit/42"), (row.State, row.WorkItemId, row.Url));
            var create = ado.Requests.Single(x => x.Url.Contains("/workitems/"));
            Assert.Equal("application/json-patch+json", create.ContentType);
            Assert.Contains("codeinsights-finding-" + finding.Id, create.Body);
            Assert.Contains("https://hub.local/?analysis_id=analysis-1", create.Body);

            // A second request keeps the existing link instead of creating a duplicate.
            await FindingWorkItems.RequestAsync(db, finding, target!, "manual", "dev@up.coop", ct);
            Assert.Equal(42, (await db.Set<FindingWorkItem>().AsNoTracking().SingleAsync(x => x.FindingId == finding.Id, ct)).WorkItemId);

            // Retry after a lost response: the tagged item is adopted, nothing is posted again.
            await db.Set<FindingWorkItem>().ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "pending").SetProperty(x => x.WorkItemId, (int?)null), ct);
            var retry = new AzureDevOpsStub();
            retry.Respond("/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[{"id":7}]}""");
            db.ChangeTracker.Clear();
            await Service(db, retry).CreateAsync(finding.Id, ct);
            Assert.Equal(7, (await db.Set<FindingWorkItem>().AsNoTracking().SingleAsync(x => x.FindingId == finding.Id, ct)).WorkItemId);
            Assert.DoesNotContain(retry.Requests, x => x.Url.Contains("/workitems/"));

            // Resolution comment goes to the linked item.
            var comment = new AzureDevOpsStub();
            comment.Respond("/_apis/wit/workItems/7/comments", HttpStatusCode.OK, "{}");
            db.ChangeTracker.Clear();
            await Service(db, comment).CommentResolvedAsync(finding.Id + "|3", ct);
            Assert.Contains("resolved in CodeInsights", comment.Requests.Single().Body);

            // A refusal is terminal and explains the missing permission.
            await db.Set<FindingWorkItem>().ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "pending"), ct);
            var refused = new AzureDevOpsStub();
            refused.Respond("/_apis/wit/wiql", HttpStatusCode.Forbidden, """{"message":"TF401027: permission denied"}""");
            db.ChangeTracker.Clear();
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(db, refused).CreateAsync(finding.Id, ct));
            Assert.Contains("TF401027", error.Message);
            Assert.Contains("Work Items (Read & Write)", error.Message);
            await FindingWorkItems.FailAsync(db, finding.Id, error.Message, ct);
            Assert.Equal("failed", (await db.Set<FindingWorkItem>().AsNoTracking().SingleAsync(x => x.FindingId == finding.Id, ct)).State);

            // Rules: only main-branch findings detected after enabling, highest severity first, bounded per run.
            var since = DateTime.UtcNow.AddDays(-1);
            db.Add(new WorkItemSettings { ProjectKey = "boards", AutoMinSeverity = "high", AutoMaxPerRun = 2, AutoSince = since });
            string Add(string severity, DateTime detected, string branch = "refs/heads/main", string state = "open")
            {
                var id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                db.Add(new DurableFinding { Id = id, ProjectKey = "boards", Branch = branch, Tool = "t", RuleId = "r", Message = "m", Severity = severity, State = state, FirstDetectedAt = detected, SlaBasis = FindingSla.Pending });
                return id;
            }
            var blocker = Add("blocker", DateTime.UtcNow.AddHours(-2));
            var high = Add("high", DateTime.UtcNow.AddHours(-3));
            var secondHigh = Add("high", DateTime.UtcNow.AddHours(-1));
            var old = Add("blocker", since.AddDays(-5));
            var medium = Add("medium", DateTime.UtcNow);
            var feature = Add("blocker", DateTime.UtcNow, "refs/heads/feature");
            var resolved = Add("blocker", DateTime.UtcNow, state: "resolved");
            await db.SaveChangesAsync(ct);
            Assert.Equal(2, await FindingWorkItems.ApplyRulesAsync(db, ct));
            var queued = await db.Set<FindingWorkItem>().AsNoTracking().Where(x => x.Origin == "severity_rule").Select(x => x.FindingId).ToListAsync(ct);
            Assert.Equal(new[] { blocker, high }.Order(), queued.Order());
            Assert.Equal(1, await FindingWorkItems.ApplyRulesAsync(db, ct));
            Assert.Contains(secondHigh, await db.Set<FindingWorkItem>().Select(x => x.FindingId).ToListAsync(ct));
            Assert.Equal(0, await FindingWorkItems.ApplyRulesAsync(db, ct));
            var ignored = await db.Set<FindingWorkItem>().Where(x => new[] { old, medium, feature, resolved }.Contains(x.FindingId)).CountAsync(ct);
            Assert.Equal(0, ignored);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)", ct);
        }
    }

    private static FindingWorkItems Service(AppDbContext db, AzureDevOpsStub stub) =>
        new(db, stub, new AzureDevOpsAuthentication(new StaticCredential()), Options.Create(new IngestionOptions { PlatformUrl = "https://hub.local" }));

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class StaticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Answers by URL fragment and records every request; an unexpected call fails the test.</summary>
    private sealed class AzureDevOpsStub : HttpMessageHandler, IHttpClientFactory
    {
        private readonly List<(string Fragment, HttpStatusCode Status, string Body)> responses = [];
        public List<(string Url, string? ContentType, string Body)> Requests { get; } = [];

        public void Respond(string fragment, HttpStatusCode status, string body) => responses.Add((fragment, status, body));

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Content?.Headers.ContentType?.MediaType, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var match = responses.FirstOrDefault(x => url.Contains(x.Fragment, StringComparison.Ordinal));
            if (match.Fragment is null) throw new InvalidOperationException("Unexpected Azure DevOps call " + url);
            return new HttpResponseMessage(match.Status) { Content = new StringContent(match.Body, Encoding.UTF8, "application/json") };
        }
    }
}
