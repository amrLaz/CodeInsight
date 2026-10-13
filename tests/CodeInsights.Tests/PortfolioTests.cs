using CodeInsights.Api;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class PortfolioTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    [Fact]
    public async Task Snapshots_keep_only_what_stored_analyses_prove()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_DB to run portfolio PostgreSQL tests.");
        var isolated = new NpgsqlConnectionStringBuilder(connection) { Database = "codeinsights_portfolio_" + Guid.NewGuid().ToString("N")[..12], Pooling = false };
        await ExecuteAsync(connection, $"CREATE DATABASE \"{isolated.Database}\"", ct);
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await db.Database.MigrateAsync(ct);
            db.AddRange(
                new Project { Id = "p1", Key = "trend", Name = "trend", MainBranch = "refs/heads/main" },
                new Project { Id = "p2", Key = "unset", Name = "unset" });
            var older = Analysis("a-old", Now.AddDays(-3), rating: "A", coverage: 80);
            var recent = Analysis("a-new", Now.AddDays(-1), rating: "B", coverage: 70);
            recent.DetailsPurged = true;
            db.AddRange(older, recent,
                Analysis("a-feature", Now.AddDays(-1), branch: "refs/heads/feature"),
                Analysis("a-failed", Now.AddHours(-1), status: "failed"),
                Analysis("a-unset", Now.AddDays(-1), project: "unset"));
            foreach (var severity in new[] { "high", "error", "low", "unknown" })
                db.Add(new FindingOccurrence { AnalysisId = "a-old", Tool = "t", RuleId = "r", Message = "m", Severity = severity });
            db.Add(new FindingOccurrence { AnalysisId = "a-old", Tool = "t", RuleId = "r", Message = "m", Severity = "high", Excepted = true });
            var breached = new DurableFinding { Id = "f1", ProjectKey = "trend", Branch = "refs/heads/main", Tool = "t", RuleId = "r", Message = "m", Severity = "high", State = "open", FirstDetectedAt = Now.AddDays(-40) };
            FindingSla.Apply(breached, "main", new SlaPolicy { HighDays = 30 });
            db.Add(breached);
            await db.SaveChangesAsync(ct);

            Assert.Equal(4, await PortfolioSnapshots.RefreshAsync(db, ct, Now));
            var rows = await db.Set<PortfolioSnapshot>().AsNoTracking().OrderBy(x => x.Day).ToListAsync(ct);
            Assert.All(rows, row => Assert.Equal("trend", row.ProjectKey));
            Assert.Equal(new[] { Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1), Today }, rows.Select(x => x.Day));

            var first = rows[0];
            Assert.Equal(("a-old", "A", 80, "reconstructed"), (first.AnalysisId, first.SecurityRating, first.Coverage, first.Source));
            // error is bucketed as blocker; the excepted occurrence is left out; unknown stays unknown.
            Assert.Equal((1, 1, 0, 1, 0, 1), (first.BlockerFindings, first.HighFindings, first.MediumFindings, first.LowFindings, first.InfoFindings, first.UnknownSeverityFindings));
            Assert.Null(first.SlaBreached);
            Assert.Equal("a-old", rows[1].AnalysisId);

            // Purged details: ratings and coverage survive, finding counts are unknown rather than zero.
            Assert.Equal(("a-new", "B", 70), (rows[2].AnalysisId, rows[2].SecurityRating, rows[2].Coverage));
            Assert.Null(rows[2].BlockerFindings);
            Assert.Null(rows[2].SlaBreached);

            var today = rows[3];
            Assert.Equal(("a-new", "live", 1, 0), (today.AnalysisId, today.Source, today.SlaBreached, today.SlaDueSoon));

            // A second run only recomputes today; without a rebuild budget, a missing day waits.
            await db.Set<PortfolioSnapshot>().Where(x => x.Day == Today.AddDays(-2)).ExecuteDeleteAsync(ct);
            Assert.Equal(1, await PortfolioSnapshots.RefreshAsync(db, ct, Now, rebuildsPerRun: 0));
            Assert.Equal(3, await db.Set<PortfolioSnapshot>().CountAsync(ct));
            Assert.Equal(2, await PortfolioSnapshots.RefreshAsync(db, ct, Now));
            Assert.Equal(4, await db.Set<PortfolioSnapshot>().CountAsync(ct));

            // Aggregation: only authorized projects, sums carry their measured-project count, no sum over nothing.
            db.Add(new PortfolioSnapshot { ProjectKey = "hidden", Day = Today, AnalysisId = "h", SecurityRating = "E", BlockerFindings = 99 });
            await db.SaveChangesAsync(ct);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(
                await CodeInsights.Api.Endpoints.PortfolioEndpoints.BuildAsync(db, ["trend", "unset"], Today.AddDays(-6), Today, ct), ApiJson.Options);
            var aggregated = json.GetProperty("days").EnumerateArray().ToList();
            Assert.Equal(4, aggregated.Count);
            var last = aggregated[^1];
            Assert.Equal(1, last.GetProperty("projects").GetInt32());
            Assert.Equal(0, last.GetProperty("ratings").GetProperty("security").GetProperty("e").GetInt32());
            Assert.Equal(1, last.GetProperty("ratings").GetProperty("security").GetProperty("b").GetInt32());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, last.GetProperty("findings").ValueKind);
            Assert.Equal(1, last.GetProperty("sla").GetProperty("breached").GetInt32());
            var firstDay = aggregated[0];
            Assert.Equal(1, firstDay.GetProperty("findings").GetProperty("blocker").GetInt32());
            Assert.Equal(1, firstDay.GetProperty("findings").GetProperty("projects").GetInt32());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, firstDay.GetProperty("sla").ValueKind);
            var projectRow = json.GetProperty("projects").EnumerateArray().Single();
            Assert.Equal(("trend", 70, 80), (projectRow.GetProperty("project_key").GetString(), projectRow.GetProperty("coverage").GetInt32(), projectRow.GetProperty("coverage_start").GetInt32()));
            Assert.Equal(1, json.GetProperty("projects_without_main_branch").GetInt32());
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)", ct);
        }
    }

    private static AnalysisRecord Analysis(string id, DateTime at, string project = "trend", string branch = "refs/heads/main", string status = "completed", string? rating = null, int? coverage = null) => new()
    {
        AnalysisId = id,
        ProjectKey = project,
        Branch = branch,
        CommitSha = "abcdef1",
        CreatedAt = ApiJson.Iso(at),
        Status = status,
        SecurityRating = rating,
        Coverage = coverage,
    };

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }
}
