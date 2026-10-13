using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

/// <summary>Pull request builds describe their pull request and never stand in for their source branch.</summary>
public sealed class PullRequestAnalysisTests
{
    private const string Repository = "https://dev.azure.com/org/project/_git/repo";
    private const string Develop = "refs/heads/develop";
    private const string Master = "refs/heads/master";
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Branch_reads_ignore_pull_request_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async db =>
        {
            db.AddRange(
                Analysis("develop-build", Now.AddDays(-2), Develop),
                Analysis("pr-old", Now.AddDays(-1), Develop, pr: "51382", target: Master),
                Analysis("pr-new", Now.AddHours(-1), Develop, pr: "51382", target: Master),
                Analysis("pr-feature", Now.AddHours(-2), "refs/heads/feature/x", pr: "9", target: Develop),
                Analysis("master-build", Now.AddDays(-3), Master),
                Analysis("pr-only-project", Now, "refs/heads/feature/y", project: "pr-only", pr: "7", target: Master));
            await db.SaveChangesAsync(ct);
            var analyses = db.Set<AnalysisRecord>().AsNoTracking().Where(x => x.ProjectKey == "app");

            var branches = Json(await InsightEndpoints.BranchRowsAsync(analyses, [Develop, "refs/heads/feature/x"], ct));
            var develop = branches[0];
            Assert.Equal((Develop, 1, 2, "develop-build"),
                (develop.GetProperty("name").GetString(), develop.GetProperty("count").GetInt32(),
                    develop.GetProperty("pull_request_count").GetInt32(), develop.GetProperty("latest").GetProperty("analysis_id").GetString()));
            // A branch built only through pull requests stays listed, with no branch verdict to show.
            var feature = branches[1];
            Assert.Equal(("refs/heads/feature/x", 0, 1), (feature.GetProperty("name").GetString(),
                feature.GetProperty("count").GetInt32(), feature.GetProperty("pull_request_count").GetInt32()));
            Assert.Equal(JsonValueKind.Null, feature.GetProperty("latest").ValueKind);

            var (rows, next) = await InsightEndpoints.PullRequestRowsAsync(analyses, null, 1, ct);
            var pullRequest = Json(rows).Single();
            Assert.Equal(("51382", Develop, Master, 2, "pr-new"),
                (pullRequest.GetProperty("pr_id").GetString(), pullRequest.GetProperty("source_branch").GetString(),
                    pullRequest.GetProperty("target_branch").GetString(), pullRequest.GetProperty("count").GetInt32(),
                    pullRequest.GetProperty("latest").GetProperty("analysis_id").GetString()));
            Assert.Equal(Master, pullRequest.GetProperty("latest").GetProperty("pr_target_branch").GetString());
            // Pull requests are numbered upwards: 51382 precedes 9 although "9" sorts after "5" as text.
            var (remaining, end) = await InsightEndpoints.PullRequestRowsAsync(analyses, ParseCursor(next), 1, ct);
            Assert.Equal("9", Json(remaining).Single().GetProperty("pr_id").GetString());
            Assert.Null(end);

            var latest = await InsightEndpoints.ProjectLatestAsync(db.Set<AnalysisRecord>().AsNoTracking(), ["app", "pr-only"], ct);
            Assert.Equal("develop-build", latest.Single(x => x.ProjectKey == "app").AnalysisId);
            // A project analysed only through pull requests remains reachable through its latest one.
            Assert.Equal("pr-only-project", latest.Single(x => x.ProjectKey == "pr-only").AnalysisId);
        }, ct);
    }

    [Fact]
    public async Task Branch_search_reaches_a_branch_beyond_the_first_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async db =>
        {
            db.AddRange(
                Analysis("master-build", Now, Master),
                // Source branches of pull requests sort before master and fill the first page.
                Analysis("pr-a", Now, "refs/heads/feat/a", pr: "1", target: Master),
                Analysis("pr-b", Now, "refs/heads/fix/master-gate", pr: "2", target: Master),
                Analysis("pr-c", Now, "refs/heads/fix/a_b", pr: "3", target: Master),
                Analysis("pr-d", Now, "refs/heads/fix/axb", pr: "4", target: Master));
            await db.SaveChangesAsync(ct);
            var analyses = db.Set<AnalysisRecord>().AsNoTracking().Where(x => x.ProjectKey == "app");
            Task<List<string>> Names(string prefix, string search) =>
                InsightEndpoints.MatchingBranches(analyses, prefix, search).Select(x => x.Branch).Distinct().OrderBy(x => x).ToListAsync(ct);

            Assert.DoesNotContain(Master, (await Names("", "")).Take(4));
            // Any part of the name, whatever its case: the picker filters its loaded page the same way.
            Assert.Equal(["refs/heads/fix/master-gate", Master], await Names("", " MASTER "));
            // `_` is a literal character, not a LIKE wildcard.
            Assert.Equal(["refs/heads/fix/a_b"], await Names("", "a_b"));
            Assert.Equal(["refs/heads/fix/a_b", "refs/heads/fix/axb", "refs/heads/fix/master-gate"], await Names("fix/", ""));
        }, ct);
    }

    [Fact]
    public async Task Pull_request_builds_are_never_a_new_code_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async db =>
        {
            db.Add(new NewCodeDefinition { ScopeKey = "global", Level = "global", Mode = "number_of_days", NumberOfDays = 1 });
            AddCompleted(db, Analysis("develop-build", Now.AddDays(-3), Develop));
            AddCompleted(db, Analysis("develop-pr", Now.AddDays(-2), Develop, pr: "51382", target: Master));
            await db.SaveChangesAsync(ct);

            var resolution = await NewCode.ResolveAsync(db, "app", Repository, Develop, null, null, null, Now, ct);

            Assert.Null(resolution.Reason);
            Assert.Equal("develop-build", resolution.BaselineAnalysisId);
        }, ct);
    }

    [Fact]
    public async Task Migration_restores_the_recorded_target_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async db =>
        {
            db.AddRange(
                Analysis("pr-cleared", Now, Develop, pr: "51382", target: ""),
                Analysis("branch-build", Now, Develop));
            db.AddRange(
                Ingestion("pr-cleared", 1, Develop, "refs/heads/release"),
                Ingestion("pr-cleared", 2, Develop, Master),
                Ingestion("branch-build", 1, Develop, Master));
            await db.SaveChangesAsync(ct);

            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: ct);

            var targets = await db.Set<AnalysisRecord>().AsNoTracking().ToDictionaryAsync(x => x.AnalysisId, x => x.PrTargetBranch, ct);
            Assert.Equal(Master, targets["pr-cleared"]);
            Assert.Equal("", targets["branch-build"]);
        }, ct, migrateTo: "20260929221952_AddLicensePolicy");
    }

    [Fact]
    public async Task Pull_request_builds_link_branch_findings_without_rewriting_their_triage()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDatabaseAsync(async db =>
        {
            var pullRequest = Analysis("develop-pr", Now, Develop, pr: "51382", target: Master);
            db.Add(pullRequest);
            var known = Occurrence("develop-pr", "known", "src/A.cs", 4);
            var unknown = Occurrence("develop-pr", "new-in-merge", "src/B.cs", 9);
            db.AddRange(known, unknown);
            // A resolved finding of the source branch, with an evidence the merge result does not repeat.
            var resolved = new DurableFinding
            {
                Id = FindingCollaboration.Identity("app", Develop, known),
                ProjectKey = "app",
                Branch = Develop,
                Tool = known.Tool,
                RuleId = known.RuleId,
                Category = known.Category,
                Message = "older message",
                State = "resolved",
                LastAnalysisId = "develop-build",
                LastSeenAt = Now.AddDays(-1),
                FirstDetectedAt = Now.AddDays(-10),
            };
            db.Add(resolved);
            await db.SaveChangesAsync(ct);

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await FindingCollaboration.ObserveAsync(db, pullRequest, [known, unknown], ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            var finding = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(ct);
            Assert.Equal(("resolved", "develop-build", "older message", 1), (finding.State, finding.LastAnalysisId, finding.Message, finding.Version));
            Assert.Empty(await db.Set<FindingDecision>().ToListAsync(ct));
            Assert.Empty(await db.Set<FindingIdentityMatch>().ToListAsync(ct));
            // The branch decision still applies to the pull request occurrence; the merge-only one stays untracked.
            Assert.Equal(finding.Id, known.FindingId);
            Assert.Null(unknown.FindingId);

            // Triaging that occurrence on the pull request records the decision on its source branch.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await FindingCollaboration.ObserveAsync(db, pullRequest, [known, unknown], ct, adoptOccurrenceId: unknown.Id);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            var adopted = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == unknown.FindingId, ct);
            Assert.Equal((Develop, "open", "develop-pr"), (adopted.Branch, adopted.State, adopted.LastAnalysisId));
            Assert.Equal("resolved", (await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == finding.Id, ct)).State);
        }, ct);
    }

    private static FindingOccurrence Occurrence(string analysisId, string fingerprint, string path, int line) => new()
    {
        AnalysisId = analysisId,
        Tool = "semgrep",
        RuleId = "csharp.sql-injection",
        Category = "vulnerability",
        Severity = "high",
        Message = "Possible SQL injection",
        Path = path,
        Line = line,
        Fingerprint = fingerprint,
    };

    private static AnalysisRecord Analysis(string id, DateTime at, string branch, string project = "app", string? pr = null, string? target = null) => new()
    {
        AnalysisId = id,
        ProjectKey = project,
        Branch = branch,
        CommitSha = "abcdef1",
        CreatedAt = ApiJson.Iso(at),
        CompletedAt = at,
        Status = "completed",
        QualityGate = "pass",
        PrId = pr ?? "",
        PrTargetBranch = target ?? "",
    };

    private static void AddCompleted(AppDbContext db, AnalysisRecord analysis)
    {
        db.Add(analysis);
        db.Add(Ingestion(analysis.AnalysisId, 1, analysis.Branch, analysis.PrTargetBranch, analysis.CompletedAt));
    }

    private static IngestionRecord Ingestion(string analysisId, int revision, string branch, string? target, DateTime? completedAt = null) => new()
    {
        AnalysisId = analysisId,
        ProjectKey = "app",
        BuildKey = analysisId + "-" + revision,
        Revision = revision,
        State = "completed",
        CompletedAt = completedAt ?? Now,
        Manifest = JsonSerializer.Serialize(new { repository_url = Repository, branch, pr_target_branch = target }),
    };

    private static string? ParseCursor(string? cursor) =>
        cursor is null ? null : JsonSerializer.Deserialize<string[]>(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(cursor))![0];

    private static List<JsonElement> Json(IReadOnlyList<object> rows) =>
        JsonSerializer.SerializeToElement(rows, ApiJson.Options).EnumerateArray().ToList();

    private static async Task WithDatabaseAsync(Func<AppDbContext, Task> test, CancellationToken ct, string? migrateTo = null)
    {
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_DB to run pull request PostgreSQL tests.");
        var isolated = new NpgsqlConnectionStringBuilder(connection) { Database = "codeinsights_pr_" + Guid.NewGuid().ToString("N")[..12], Pooling = false };
        await ExecuteAsync(connection, $"CREATE DATABASE \"{isolated.Database}\"", ct);
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await db.GetService<IMigrator>().MigrateAsync(migrateTo, ct);
            await test(db);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)", ct);
        }
    }

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }
}
