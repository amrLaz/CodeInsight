using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

/// <summary>Preserves human decisions independently of scanner occurrences and report retention.</summary>
public static class FindingCollaboration
{
    public static string Identity(string project, string branch, FindingOccurrence finding, bool fingerprintIsUnique = true) =>
        AuthService.Sha256(JsonSerializer.Serialize(new object?[] { "finding/1", project, branch, finding.Tool, finding.RuleId,
            fingerprintIsUnique && !string.IsNullOrEmpty(finding.Fingerprint) ? new object?[] { "fingerprint", finding.Fingerprint }
                : new object?[] { "exact_location", finding.Path, finding.Line, finding.Message } }));

    public static Task<int> LockAsync(AppDbContext db, string project, string branch, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"collaboration:" + JsonSerializer.Serialize(new[] { project, branch })}, 0))", ct);

    /// <summary>
    /// Caller owns the analysis transaction. Missing findings never imply resolution.
    /// A pull request build only links its occurrences to the findings of its source branch: it never creates,
    /// reopens or updates one, so its merge result cannot rewrite the branch's triage. A person triaging one of
    /// its occurrences (<paramref name="adoptOccurrenceId"/>) creates that finding on the source branch.
    /// </summary>
    public static async Task ObserveAsync(AppDbContext db, AnalysisRecord analysis, IReadOnlyCollection<FindingOccurrence> occurrences, CancellationToken ct, GitDiff? diff = null,
        long? adoptOccurrenceId = null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Observation requires a transaction");
        await LockAsync(db, analysis.ProjectKey, analysis.Branch, ct);
        var pullRequest = !string.IsNullOrEmpty(analysis.PrId);
        var fingerprints = occurrences.Where(x => !string.IsNullOrEmpty(x.Fingerprint)).GroupBy(x => (x.Tool, x.RuleId, x.Fingerprint)).ToDictionary(g => g.Key, g => g.Count());
        var continuity = await FindingIdentityContinuity.ResolveAsync(db, analysis, occurrences, diff, ct, readOnly: pullRequest);
        var mappedIds = continuity.Values.ToHashSet();
        var identified = occurrences.Select(x =>
        {
            var identity = Identity(analysis.ProjectKey, analysis.Branch, x, string.IsNullOrEmpty(x.Fingerprint) || fingerprints[(x.Tool, x.RuleId, x.Fingerprint)] == 1);
            return (Occurrence: x, Id: continuity.GetValueOrDefault(identity, identity));
        }).ToArray();
        var ownership = await db.Set<ProjectOwnership>().FindAsync([analysis.ProjectKey], ct);
        var mainBranch = await db.Set<Project>().Where(x => x.Key == analysis.ProjectKey).Select(x => x.MainBranch).SingleOrDefaultAsync(ct);
        var slaPolicy = await FindingSla.EffectivePolicyAsync(db, analysis.ProjectKey, ct);
        var slaPending = false;
        var created = false;
        var seenAt = DateTimeOffset.TryParse(analysis.CreatedAt, out var time) ? time.UtcDateTime : DateTime.UtcNow;
        // ponytail: chunks bound SQL parameters and loaded tracking records; bulk upsert is the upgrade path for measured ingestion bottlenecks.
        foreach (var chunk in identified.GroupBy(x => x.Id).Chunk(500))
        {
            var ids = chunk.Select(x => x.Key).ToArray();
            var existing = await db.Set<DurableFinding>().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            // The proof is the one of the finding's last observation, never a linked pull request occurrence.
            var lastAnalyses = existing.Values.Select(x => x.LastAnalysisId).Distinct().ToArray();
            var previousProof = await db.Set<FindingOccurrence>().AsNoTracking().Where(x => x.FindingId != null && ids.Contains(x.FindingId) && lastAnalyses.Contains(x.AnalysisId))
                .GroupBy(x => x.FindingId).Select(g => g.OrderByDescending(x => x.Id).First()).ToListAsync(ct);
            foreach (var group in chunk)
            {
                var occurrence = group.First().Occurrence;
                if (pullRequest && existing.TryGetValue(group.Key, out var linked))
                {
                    foreach (var item in group) item.Occurrence.FindingId = linked.Id;
                    continue;
                }
                if (pullRequest && !group.Any(x => x.Occurrence.Id == adoptOccurrenceId)) continue;
                if (!existing.TryGetValue(group.Key, out var finding))
                {
                    finding = new DurableFinding
                    {
                        Id = group.Key,
                        ProjectKey = analysis.ProjectKey,
                        Branch = analysis.Branch,
                        Tool = occurrence.Tool,
                        RuleId = occurrence.RuleId,
                        Category = occurrence.Category,
                        Message = occurrence.Message,
                        IdentityKind = !string.IsNullOrEmpty(occurrence.Fingerprint) && fingerprints[(occurrence.Tool, occurrence.RuleId, occurrence.Fingerprint)] == 1 ? "fingerprint" : "exact_location",
                        AssigneeUserId = ownership?.OwnerUserId,
                        TeamId = ownership?.TeamId,
                        Severity = FindingSla.SeverityOf(occurrence.Severity),
                        FirstDetectedAt = seenAt
                    };
                    db.Add(finding);
                    created = true;
                }
                else if (finding.FirstDetectedAt > seenAt) finding.FirstDetectedAt = seenAt;
                if (seenAt >= finding.LastSeenAt)
                {
                    if (finding.IdentityKind == "exact_location" && mappedIds.Contains(finding.Id)) finding.IdentityKind = "git_line_map";
                    var reopen = finding.State == "resolved" && finding.LastAnalysisId != analysis.AnalysisId && seenAt > finding.LastSeenAt;
                    finding.LastAnalysisId = analysis.AnalysisId; finding.LastSeenAt = seenAt; finding.Path = occurrence.Path; finding.Line = occurrence.Line;
                    finding.Severity = FindingSla.SeverityOf(occurrence.Severity);
                    if (reopen)
                    {
                        finding.State = "open"; finding.HotspotReview = "not_reviewed"; finding.Version++;
                        await DecisionAsync(db, finding, new Principal("system"), "reopened_by_scan", "Finding observed in a later analysis", ct);
                    }
                    var prior = previousProof.SingleOrDefault(p => p.FindingId == finding.Id);
                    if (finding.Category != occurrence.Category || finding.Message != occurrence.Message
                        || prior is not null && SecurityReviewEvaluation.EvidenceHash(prior) != SecurityReviewEvaluation.EvidenceHash(occurrence)
                        || string.IsNullOrEmpty(occurrence.Fingerprint) && diff?.State == "available" && occurrence.Line is { } currentLine
                            && diff.Files.Any(f => f.Path == occurrence.Path && f.AddedLines.Contains(currentLine)))
                    {
                        finding.Category = occurrence.Category; finding.Message = occurrence.Message;
                        finding.HotspotReview = "not_reviewed"; finding.Version++;
                        if (finding.State is "false_positive" or "accepted_risk")
                        {
                            finding.State = "open"; finding.ExceptionReason = null; finding.ExceptionExpiresAt = null;
                        }
                        await DecisionAsync(db, finding, new Principal("system"), "evidence_changed", "Scanner evidence changed; hotspot review and exception reset", ct);
                    }
                }
                foreach (var item in group) item.Occurrence.FindingId = finding.Id;
                // Findings tracked before deadlines existed wait for the worker's backfill of their detection date;
                // once it proved the date unknowable, the deadline stays indeterminate without another backfill.
                if (finding.FirstDetectedAt is null && finding.SlaBasis == FindingSla.Pending) slaPending = true;
                else FindingSla.Apply(finding, mainBranch, slaPolicy);
            }
        }
        // New findings may match an automatic work item rule; the sla job evaluates them without waiting for maintenance.
        if (slaPending || created) await FindingSla.EnqueueAsync(db, ct);
    }

    public static string EffectiveState(DurableFinding finding, DateTime now) =>
        finding.State is "false_positive" or "accepted_risk" && finding.ExceptionExpiresAt <= now ? "open" : finding.State;

    /// <summary>Expired reviews are immediately ineffective; maintenance records their reopening durably.</summary>
    public static async Task ExpireAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Set<DurableFinding>().AsNoTracking().Where(x => (x.State == "false_positive" || x.State == "accepted_risk") && x.ExceptionExpiresAt <= now)
            .OrderBy(x => x.ExceptionExpiresAt).Take(100).Select(x => new { x.Id, x.ProjectKey, x.Branch }).ToListAsync(ct);
        foreach (var item in rows)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, item.ProjectKey, item.Branch, ct);
            var row = await db.Set<DurableFinding>().SingleAsync(x => x.Id == item.Id, ct);
            if (row.State is "false_positive" or "accepted_risk" && row.ExceptionExpiresAt <= now)
            {
                row.State = "open"; row.HotspotReview = "not_reviewed"; row.ExceptionReason = null; row.ExceptionExpiresAt = null; row.Version++;
                await DecisionAsync(db, row, new Principal("system"), "exception_expired", "Review expiry reached; finding reopened", ct);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public static async Task DecisionAsync(AppDbContext db, DurableFinding finding, Principal actor, string action, string reason, CancellationToken ct)
    {
        var snapshot = JsonSerializer.SerializeToNode(View(finding), ApiJson.Options)!;
        if (action == "review")
        {
            var proof = await db.Set<FindingOccurrence>().AsNoTracking().Where(o => o.FindingId == finding.Id && o.AnalysisId == finding.LastAnalysisId)
                .OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
            var commit = await db.Set<AnalysisRecord>().Where(a => a.AnalysisId == finding.LastAnalysisId).Select(a => a.CommitSha).FirstOrDefaultAsync(ct);
            if (proof is not null && commit is not null)
                snapshot["review_evidence"] = JsonSerializer.SerializeToNode(new SecurityReviewProof(SecurityReviewEvaluation.EvidenceHash(proof), commit, proof.Path, proof.Line, proof.Fingerprint), ApiJson.Options);
        }
        var decision = new FindingDecision
        {
            FindingId = finding.Id,
            ProjectKey = finding.ProjectKey,
            ActorUserId = actor.Sub,
            ActorEmail = actor.Email,
            Action = action,
            Reason = reason,
            Snapshot = snapshot.ToJsonString(ApiJson.Options)
        };
        db.Add(decision);
    }

    public static object View(DurableFinding x) => new
    {
        x.Id,
        x.ProjectKey,
        x.Branch,
        x.Tool,
        x.RuleId,
        x.Category,
        x.Message,
        x.Path,
        x.Line,
        x.IdentityKind,
        x.State,
        effective_state = EffectiveState(x, DateTime.UtcNow),
        x.HotspotReview,
        x.AssigneeUserId,
        x.TeamId,
        x.ExceptionReason,
        x.ExceptionExpiresAt,
        x.LastAnalysisId,
        x.LastSeenAt,
        x.UpdatedAt,
        x.Version,
        x.Severity,
        x.FirstDetectedAt,
        sla = FindingSla.View(x, DateTime.UtcNow)
    };
}
