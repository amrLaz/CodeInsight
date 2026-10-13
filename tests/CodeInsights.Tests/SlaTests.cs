using System.Text.Json.Nodes;
using CodeInsights.Api;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class SlaTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SlaPolicy Defaults = new() { BlockerDays = 7, HighDays = 30, MediumDays = 90, LowDays = 180 };

    private static DurableFinding Finding(string severity = "high", string branch = "refs/heads/main", DateTime? detected = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProjectKey = "p",
        Branch = branch,
        Severity = severity,
        FirstDetectedAt = detected ?? Now.AddDays(-1),
    };

    [Theory]
    [InlineData("critical", "blocker")]
    [InlineData("blocker", "blocker")]
    [InlineData("high", "high")]
    [InlineData("warning", "medium")]
    [InlineData("low", "low")]
    [InlineData("info", "info")]
    [InlineData("unknown", "unknown")]
    [InlineData("", "unknown")]
    public void Severity_is_bucketed_without_turning_unknown_into_info(string raw, string expected) =>
        Assert.Equal(expected, FindingSla.SeverityOf(raw));

    [Fact]
    public void Deadline_applies_only_when_every_input_is_known()
    {
        var finding = Finding();
        FindingSla.Apply(finding, null, Defaults);
        Assert.Equal("unknown_main_branch", finding.SlaBasis);
        FindingSla.Apply(finding, "develop", Defaults);
        Assert.Equal("not_main_branch", finding.SlaBasis);
        FindingSla.Apply(finding, "main", null);
        Assert.Equal("no_policy", finding.SlaBasis);
        var unknown = Finding("unknown");
        FindingSla.Apply(unknown, "main", Defaults);
        Assert.Equal("unknown_severity", unknown.SlaBasis);
        var info = Finding("info");
        FindingSla.Apply(info, "main", Defaults);
        Assert.Equal("no_target", info.SlaBasis);
        var undated = Finding();
        undated.FirstDetectedAt = null;
        FindingSla.Apply(undated, "main", Defaults);
        Assert.Equal("unknown_detection", undated.SlaBasis);
        Assert.Null(undated.SlaDueAt);

        FindingSla.Apply(finding, "main", Defaults);
        Assert.Equal(FindingSla.Applies, finding.SlaBasis);
        Assert.Equal(Now.AddDays(29), finding.SlaDueAt);
        Assert.Equal(Now.AddDays(23), finding.SlaWarnAt);
        var blocker = Finding("blocker");
        FindingSla.Apply(blocker, "refs/heads/main", new SlaPolicy { BlockerDays = 3 });
        Assert.Equal(blocker.SlaDueAt!.Value.AddDays(-1), blocker.SlaWarnAt);
    }

    [Fact]
    public void Status_follows_review_state_and_clock()
    {
        DurableFinding Applied(DateTime detected, string state = "open", DateTime? expiry = null)
        {
            var finding = Finding(detected: detected);
            finding.State = state;
            finding.ExceptionExpiresAt = expiry;
            FindingSla.Apply(finding, "main", Defaults);
            return finding;
        }
        Assert.Equal("on_track", FindingSla.Status(Applied(Now.AddDays(-1)), Now));
        Assert.Equal("due_soon", FindingSla.Status(Applied(Now.AddDays(-25)), Now));
        Assert.Equal("breached", FindingSla.Status(Applied(Now.AddDays(-31), "confirmed"), Now));
        Assert.Equal("closed", FindingSla.Status(Applied(Now.AddDays(-31), "resolved"), Now));
        Assert.Equal("suspended", FindingSla.Status(Applied(Now.AddDays(-31), "accepted_risk", Now.AddDays(1)), Now));
        Assert.Equal("breached", FindingSla.Status(Applied(Now.AddDays(-31), "false_positive", Now.AddDays(-1)), Now));
        Assert.Equal("pending", FindingSla.Status(new DurableFinding(), Now));
    }

    [Fact]
    public void Sql_filter_partitions_findings_exactly_like_the_read_status()
    {
        var findings = new List<DurableFinding>();
        foreach (var detected in new[] { Now.AddDays(-1), Now.AddDays(-25), Now.AddDays(-31), Now.AddDays(-400) })
        foreach (var severity in new[] { "blocker", "high", "info", "unknown" })
        foreach (var branch in new[] { "refs/heads/main", "refs/heads/feature" })
        foreach (var (state, expiry) in new (string, DateTime?)[] { ("open", null), ("confirmed", null), ("resolved", null), ("accepted_risk", Now.AddDays(3)), ("false_positive", Now.AddDays(-3)) })
        {
            var finding = Finding(severity, branch, detected);
            finding.State = state;
            finding.ExceptionExpiresAt = expiry;
            FindingSla.Apply(finding, "main", Defaults);
            findings.Add(finding);
        }
        findings.Add(new DurableFinding { Id = "pending" });
        var noPolicy = Finding();
        FindingSla.Apply(noPolicy, "main", null);
        findings.Add(noPolicy);
        foreach (var finding in findings)
        {
            var matches = FindingSla.Statuses.Where(status => FindingSla.FilterStatus(new[] { finding }.AsQueryable(), status, Now).Any()).ToList();
            Assert.Equal([FindingSla.Status(finding, Now)], matches);
        }
        Assert.Throws<ApiException>(() => FindingSla.FilterStatus(findings.AsQueryable(), "late", Now));
    }

    [Fact]
    public void Breach_digest_reports_truncation_instead_of_hiding_it()
    {
        var context = new JsonObject
        {
            ["project_key"] = "p",
            ["branch"] = "refs/heads/main",
            ["breached_count"] = 25,
            ["findings"] = new JsonArray(new JsonObject { ["severity"] = "high", ["tool"] = "semgrep", ["rule_id"] = "R1", ["path"] = "a.cs", ["line"] = 3, ["sla_due_at"] = "2026-09-01" }),
        };
        var email = NotificationService.BuildSlaEmailBody(context);
        Assert.Contains("25 finding(s)", email);
        Assert.Contains("24 more", email);
        Assert.Contains("semgrep/R1", email);
        var teams = NotificationService.BuildSlaTeamsPayload(context).ToJsonString();
        Assert.Contains("24 more", teams);
    }

    [Fact]
    public async Task Persistence_backfill_and_notification()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_DB to run SLA PostgreSQL tests.");
        // The API under test runs a worker on the shared queue: notification jobs are checked on a private database.
        var isolated = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = "codeinsights_sla_" + Guid.NewGuid().ToString("N")[..12],
            Pooling = false,
        };
        await ExecuteAsync(connection, ct, $"CREATE DATABASE \"{isolated.Database}\"");
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await db.Database.MigrateAsync(ct);
            var global = await db.Set<SlaPolicy>().SingleAsync(x => x.ScopeKey == "global", ct);
            Assert.Equal((7, 30, 90, 180, (int?)null), (global.BlockerDays, global.HighDays, global.MediumDays, global.LowDays, global.InfoDays));

            db.Add(new Project { Id = Guid.NewGuid().ToString(), Key = "sla", Name = "sla", MainBranch = "refs/heads/main" });
            await db.SaveChangesAsync(ct);
            var observed = await Observe(db, ct, "refs/heads/main", Now.AddDays(-40), "high");
            var feature = await Observe(db, ct, "refs/heads/feature", Now.AddDays(-40), "high");
            var row = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == observed, ct);
            Assert.Equal((FindingSla.Applies, "high", Now.AddDays(-40)), (row.SlaBasis, row.Severity, row.FirstDetectedAt!.Value));
            Assert.Equal("breached", FindingSla.Status(row, DateTime.UtcNow));
            Assert.Equal("not_main_branch", (await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == feature, ct)).SlaBasis);

            // A project override replaces the global policy after recomputation.
            db.Add(new SlaPolicy { ScopeKey = FindingSla.ScopeKey("sla"), ProjectKey = "sla", HighDays = 365 });
            await db.SaveChangesAsync(ct);
            await FindingSla.InvalidateAsync(db, "sla", ct);
            Assert.Equal(FindingSla.Pending, (await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == observed, ct)).SlaBasis);
            await FindingSla.RecomputeAsync(db, ct);
            row = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == observed, ct);
            Assert.Equal(Now.AddDays(325), row.SlaDueAt);

            // Backfill: a finding tracked before deadlines existed gets its date only when no earlier analysis was purged.
            var dated = await Legacy(db, ct, purgedBefore: false);
            var hidden = await Legacy(db, ct, purgedBefore: true);
            await FindingSla.RecomputeAsync(db, ct);
            var datedRow = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == dated, ct);
            Assert.Equal(Now.AddDays(-20), datedRow.FirstDetectedAt);
            Assert.Equal("medium", datedRow.Severity);
            var hiddenRow = await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == hidden, ct);
            Assert.Null(hiddenRow.FirstDetectedAt);
            Assert.Equal(("unknown_detection", "indeterminate"), (hiddenRow.SlaBasis, FindingSla.Status(hiddenRow, DateTime.UtcNow)));

            // Notification: one digest per project and channel, a finding is never notified twice for the same deadline.
            await db.Set<SlaPolicy>().Where(x => x.ProjectKey == "sla").ExecuteDeleteAsync(ct);
            await FindingSla.InvalidateAsync(db, "sla", ct);
            await FindingSla.RecomputeAsync(db, ct);
            db.Add(new NotificationChannel { Id = "sla-channel", Name = "sla", ChannelType = "webhook", Config = "{}", Enabled = true, OnFail = false, OnSlaBreach = true });
            db.Add(new NotificationChannel { Id = "gate-channel", Name = "gate", ChannelType = "webhook", Config = "{}", Enabled = true, OnFail = true });
            await db.SaveChangesAsync(ct);
            await FindingSla.NotifyAsync(db, ct);
            var jobs = await db.Set<DurableJob>().AsNoTracking().Where(x => x.Kind == "sla-notification").ToListAsync(ct);
            Assert.Single(jobs);
            Assert.StartsWith("sla|", jobs[0].ResourceId);
            Assert.EndsWith("|sla-channel", jobs[0].ResourceId);
            Assert.NotNull((await db.Set<DurableFinding>().AsNoTracking().SingleAsync(x => x.Id == observed, ct)).SlaNotifiedAt);
            await FindingSla.NotifyAsync(db, ct);
            Assert.Single(await db.Set<DurableJob>().AsNoTracking().Where(x => x.Kind == "sla-notification").ToListAsync(ct));
        }
        finally
        {
            await ExecuteAsync(connection, ct, $"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)");
        }
    }

    private static async Task<string> Observe(AppDbContext db, CancellationToken ct, string branch, DateTime at, string severity)
    {
        var analysis = new AnalysisRecord
        {
            AnalysisId = Guid.NewGuid().ToString(),
            ProjectKey = "sla",
            Branch = branch,
            CommitSha = "abcdef123",
            CreatedAt = ApiJson.Iso(at),
            Status = "completed",
        };
        var occurrence = new FindingOccurrence
        {
            AnalysisId = analysis.AnalysisId,
            Tool = "scanner",
            RuleId = "R-" + branch,
            Fingerprint = "stable",
            Path = "a.cs",
            Line = 1,
            Message = "problem",
            Category = "vulnerability",
            Severity = severity,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Add(analysis);
        db.Add(occurrence);
        await FindingCollaboration.ObserveAsync(db, analysis, [occurrence], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return occurrence.FindingId!;
    }

    /// <summary>A finding as stored before this feature: no severity, no detection date, occurrences in two analyses.</summary>
    private static async Task<string> Legacy(AppDbContext db, CancellationToken ct, bool purgedBefore)
    {
        var project = "legacy-" + Guid.NewGuid().ToString("N")[..6];
        const string branch = "refs/heads/main";
        var id = Guid.NewGuid().ToString();
        db.Add(new Project { Id = Guid.NewGuid().ToString(), Key = project, Name = project, MainBranch = branch });
        if (purgedBefore)
            db.Add(new AnalysisRecord { AnalysisId = Guid.NewGuid().ToString(), ProjectKey = project, Branch = branch, CommitSha = "000000a", CreatedAt = ApiJson.Iso(Now.AddDays(-60)), Status = "completed", DetailsPurged = true });
        foreach (var (days, severity) in new[] { (-20, "low"), (-10, "medium") })
        {
            var analysis = new AnalysisRecord { AnalysisId = Guid.NewGuid().ToString(), ProjectKey = project, Branch = branch, CommitSha = "000000b", CreatedAt = ApiJson.Iso(Now.AddDays(days)), Status = "completed" };
            db.Add(analysis);
            db.Add(new FindingOccurrence { AnalysisId = analysis.AnalysisId, FindingId = id, Tool = "scanner", RuleId = "L", Message = "legacy", Category = "bug", Severity = severity });
        }
        db.Add(new DurableFinding { Id = id, ProjectKey = project, Branch = branch, Tool = "scanner", RuleId = "L", Message = "legacy", Severity = "unknown", FirstDetectedAt = null, SlaBasis = FindingSla.Pending });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return id;
    }

    private static async Task ExecuteAsync(string connection, CancellationToken ct, string sql)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }
}
