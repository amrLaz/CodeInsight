using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class SecurityReviewTests
{
    private const string ScannerTool = "scanner";

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        var finding = new FindingOccurrence
        {
            Tool = ScannerTool,
            RuleId = "R1",
            Category = "security_hotspot",
            Message = "Review operation",
            Severity = "warning",
            Path = "old.cs",
            Line = 1,
            Fingerprint = "unique",
        };
        var decision = new SecurityReviewDecision(
            "id",
            "decision",
            2,
            "project",
            "main",
            finding.Tool,
            finding.RuleId,
            finding.Category,
            finding.Message,
            finding.Path,
            finding.Line,
            finding.Fingerprint,
            SecurityReviewEvaluation.EvidenceHash(finding),
            "accepted_risk",
            "safe",
            "Administrator approval",
            now.AddHours(1),
            "aaaaaa"
        );
        var manifest = new IngestionManifest { ProjectKey = "project", Branch = "main" };
        var policy = new SecurityReviewPolicy(true, 100);
        SecurityReviewResult Apply(
            SecurityReviewDecision review,
            SecurityReviewPolicy? selected = null,
            bool measured = true
        ) =>
            SecurityReviewEvaluation.Apply(
                selected ?? policy,
                JsonSerializer.Serialize(new[] { review }, ApiJson.Options),
                manifest,
                now,
                [finding],
                measured
            );
        var approved = Apply(decision);
        Assert.True(
            finding.Excepted
                && approved.Numerator == 1
                && approved.Denominator == 1
                && approved.Value == 100
                && approved.AppliedExceptionDecisionIds.SequenceEqual(["decision"]),
            "approved same-branch exception and review are snapshotted"
        );
        finding.Excepted = false;
        Assert.True(
            Apply(decision with { ExpiresAt = now }).Result == "fail" && !finding.Excepted,
            "expired approval neither exempts nor counts as reviewed"
        );
        Assert.True(
            Apply(decision with { Branch = "feature" }).Numerator == 0 && !finding.Excepted,
            "other branch review cannot transfer"
        );
        Assert.True(
            Apply(decision with { State = "resolved" }).Numerator == 0,
            "reappearance requires a fresh review"
        );
        finding.Message = "Changed evidence";
        Assert.True(
            Apply(decision).Numerator == 0 && !finding.Excepted,
            "changed evidence invalidates approval"
        );
        finding.Message = decision.Message;
        Assert.True(
            Apply(decision, new(false, 100)).Result == "pass" && !finding.Excepted,
            "informative exceptions never affect findings"
        );
        Assert.True(
            Apply(decision, measured: false).Result == "unknown",
            "missing hotspot scanner cannot satisfy review gate"
        );
        Assert.True(
            Apply(decision, new(true, 100, "new_code")).Result == "unknown",
            "missing comparison cannot satisfy scoped review gate"
        );
        var empty = SecurityReviewEvaluation.Apply(policy, "[]", manifest, now, [], true);
        Assert.True(
            empty.State == "not_applicable" && empty.Value is null && empty.Denominator == 0,
            "empty hotspot scope is not invented 100 percent"
        );
        var locationOnlyDecision = decision with { Fingerprint = null };
        var locationOnlyFinding = new FindingOccurrence
        {
            Tool = finding.Tool,
            RuleId = finding.RuleId,
            Category = finding.Category,
            Message = finding.Message,
            Path = finding.Path,
            Line = finding.Line,
        };
        var replaced = new GitDiff
        {
            State = "available",
            BaseSha = "aaaaaa",
            HeadSha = "bbbbbb",
            Files = [new("old.cs", null, [1], [])],
        };
        Assert.True(
            !SecurityReviewEvaluation.Matches(
                locationOnlyDecision,
                locationOnlyFinding,
                replaced,
                "bbbbbb"
            ),
            "replacement at the same location cannot inherit an approval without fingerprint"
        );
        Assert.True(
            !SecurityReviewEvaluation.Matches(
                locationOnlyDecision,
                locationOnlyFinding,
                new(),
                "bbbbbb"
            ),
            "missing Git history cannot prove location-only approval continuity"
        );
        Assert.True(
            SecurityReviewEvaluation.Matches(
                locationOnlyDecision,
                locationOnlyFinding,
                new(),
                "aaaaaa"
            ),
            "same exact commit retains a location-only approval"
        );
        try
        {
            CodeInsights.Api.Endpoints.ProfileEndpoints.Validate(
                new ProfileDefinition
                {
                    ProfileId = "review",
                    Version = "1",
                    RequiredTools = [ScannerTool],
                    SecurityReview = new(true, -1),
                }
            );
            throw new InvalidOperationException("Expected invalid review threshold rejection");
        }
        catch (ApiException ex) when (ex.StatusCode == 422) { }
        var prior = new FindingOccurrence
        {
            FindingId = "durable",
            Tool = ScannerTool,
            RuleId = "R1",
            Category = "bug",
            Message = "problem",
            Path = "old.cs",
            Line = 1,
        };
        var moved = new FindingOccurrence
        {
            Tool = prior.Tool,
            RuleId = prior.RuleId,
            Category = prior.Category,
            Message = prior.Message,
            Path = "new.cs",
            Line = 8,
        };
        var diff = new GitDiff
        {
            State = "available",
            BaseSha = "aaaaaa",
            HeadSha = "bbbbbb",
            Files = [new("new.cs", "old.cs", [], new() { ["1"] = 8 })],
        };
        var reviewedProof = new SecurityReviewProof("proof", "aaaaaa", "old.cs", 1, null);
        var firstEdge = new FindingIdentityMatch
        {
            FindingId = "durable",
            Method = "git_line_map",
            EvidenceHash = "proof",
            BaseSha = "aaaaaa",
            HeadSha = "bbbbbb",
            OldPath = "old.cs",
            OldLine = 1,
            Path = "new.cs",
            Line = 8,
        };
        var secondEdge = new FindingIdentityMatch
        {
            FindingId = "durable",
            Method = "git_line_map",
            EvidenceHash = "proof",
            BaseSha = "bbbbbb",
            HeadSha = "cccccc",
            OldPath = "new.cs",
            OldLine = 8,
            Path = "new.cs",
            Line = 9,
        };
        Assert.True(
            SecurityReviewEvaluation
                .FollowProof(reviewedProof, "cccccc", "new.cs", 9, [firstEdge, secondEdge])
                .CommitSha == "cccccc",
            "approved proof advances only along a complete immutable correspondence chain"
        );
        Assert.True(
            SecurityReviewEvaluation
                .FollowProof(reviewedProof, "cccccc", "new.cs", 9, [secondEdge])
                .CommitSha == "aaaaaa",
            "missing correspondence cannot rebind approval after replacement"
        );
        Assert.True(
            FindingIdentityContinuity.Candidates(moved, [prior], diff).Single() == prior,
            "explicit rename and line map preserve candidate identity"
        );
        Assert.True(
            FindingIdentityContinuity.Candidates(moved, [prior, prior], diff).Length == 2,
            "ambiguous movement is never forced to one candidate"
        );
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Console.WriteLine(
                "Security review/identity checks passed; persistence checks require isolated PostgreSQL."
            );
            return;
        }
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(
            info.Host is "localhost" or "127.0.0.1"
                && info.Database
                    is "codeinsights_checks"
                        or "codeinsights_test"
                        or "codeinsights_p1",
            "isolated local PostgreSQL required"
        );
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options
        );
        var project = "p3-review-" + Guid.NewGuid().ToString("N");
        db.Add(
            new Project
            {
                Id = Guid.NewGuid().ToString(),
                Key = project,
                Name = project,
            }
        );
        await db.SaveChangesAsync();
        var first = await Observe("main", "aaaaaa", "old.cs", 1, null);
        var durable = await db.Set<DurableFinding>().SingleAsync(f => f.Id == first.FindingId);
        durable.AssigneeUserId = "retained-owner";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var next = await Observe("main", "bbbbbb", "new.cs", 8, diff);
        Assert.True(next.FindingId == first.FindingId, "Git movement preserves durable identity");
        durable = await db.Set<DurableFinding>().SingleAsync(f => f.Id == next.FindingId);
        Assert.True(durable.AssigneeUserId == "retained-owner", "Git movement retains assignment");
        Assert.True(
            await db.Set<FindingIdentityMatch>()
                .AnyAsync(m =>
                    m.FindingId == first.FindingId
                    && m.Method == "git_line_map"
                    && m.BaseSha == "aaaaaa"
                    && m.HeadSha == "bbbbbb"
                ),
            "match history stores frozen Git evidence"
        );
        var feature = await Observe("feature", "bbbbbb", "new.cs", 8, diff);
        Assert.True(
            feature.FindingId != first.FindingId,
            "same diff cannot transfer identity to another branch"
        );
        var repeated = await Observe("main", "cccccc", "new.cs", 8, null);
        Assert.True(
            repeated.FindingId == first.FindingId,
            "durable correspondence survives subsequent scans without a diff"
        );
        var occurrenceIdentity = FindingCollaboration.Identity(project, "main", repeated);
        db.Add(
            new FindingIdentityMatch
            {
                Id = Guid.NewGuid().ToString(),
                ProjectKey = project,
                Branch = "main",
                AnalysisId = repeated.AnalysisId,
                OccurrenceIdentity = occurrenceIdentity,
                FindingId = null,
                Method = "indeterminate",
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            }
        );
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using (var ambiguousTransaction = await db.Database.BeginTransactionAsync())
        {
            await FindingCollaboration.LockAsync(db, project, "main", default);
            var aliases = await FindingIdentityContinuity.ResolveAsync(
                db,
                new AnalysisRecord
                {
                    ProjectKey = project,
                    Branch = "main",
                    CommitSha = "dddddd",
                },
                [repeated],
                null,
                default
            );
            Assert.True(
                !aliases.ContainsKey(occurrenceIdentity),
                "a newer ambiguous correspondence supersedes an older successful alias"
            );
            await ambiguousTransaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        durable = await db.Set<DurableFinding>().SingleAsync(f => f.Id == first.FindingId);
        durable.State = "accepted_risk";
        durable.HotspotReview = "safe";
        durable.ExceptionReason = "expired";
        durable.ExceptionExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await FindingCollaboration.ExpireAsync(db, default);
        db.ChangeTracker.Clear();
        durable = await db.Set<DurableFinding>().SingleAsync(f => f.Id == first.FindingId);
        Assert.True(
            durable.HotspotReview == "not_reviewed" && durable.ExceptionExpiresAt is null,
            "maintenance cannot resurrect an expired safe review"
        );
        Console.WriteLine("Security review and Git identity persistence checks passed.");

        async Task<FindingOccurrence> Observe(
            string branch,
            string commit,
            string path,
            int line,
            GitDiff? gitDiff
        )
        {
            var analysis = new AnalysisRecord
            {
                AnalysisId = Guid.NewGuid().ToString(),
                ProjectKey = project,
                Branch = branch,
                CommitSha = commit,
                Status = "completed",
                CreatedAt = ApiJson.Iso(DateTime.UtcNow),
            };
            var occurrence = new FindingOccurrence
            {
                AnalysisId = analysis.AnalysisId,
                Tool = ScannerTool,
                RuleId = "R1",
                Category = "bug",
                Message = "problem",
                Path = path,
                Line = line,
            };
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Add(analysis);
            db.Add(occurrence);
            await FindingCollaboration.ObserveAsync(db, analysis, [occurrence], default, gitDiff);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            db.ChangeTracker.Clear();
            return occurrence;
        }
    }

}
