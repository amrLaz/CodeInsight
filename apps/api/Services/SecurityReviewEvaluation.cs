using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

/// <summary>Server-owned review state captured when a publication is created, never read live during a retry.</summary>
public sealed record SecurityReviewDecision(string FindingId, string DecisionId, int Version, string ProjectKey, string Branch,
    string Tool, string RuleId, string Category, string Message, string? Path, int? Line, string? Fingerprint,
    string EvidenceHash, string State, string HotspotReview, string? Reason, DateTime? ExpiresAt, string CommitSha = "");

public sealed record SecurityReviewProof(string EvidenceHash, string CommitSha, string? Path, int? Line, string? Fingerprint);

public sealed record SecurityReviewResult(string Scope, string State, string Result, double? Value, int Numerator, int Denominator,
    double? Threshold, string? Reason, string[] AppliedExceptionDecisionIds, string[] ReviewedDecisionIds, DateTime CapturedAt, SecurityReviewDecision[] Decisions);

public static class SecurityReviewEvaluation
{
    /// <summary>Captures only reviews that have server-side decision history and retained evidence in this branch.</summary>
    public static async Task<string> CaptureAsync(AppDbContext db, string project, string branch, DateTime createdAt, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Review snapshot requires a transaction");
        await FindingCollaboration.LockAsync(db, project, branch, ct);
        var rows = await db.Set<DurableFinding>().AsNoTracking().Where(f => f.ProjectKey == project && f.Branch == branch
            && (f.HotspotReview != "not_reviewed" || (f.State == "false_positive" || f.State == "accepted_risk") && f.ExceptionExpiresAt > createdAt))
            .OrderBy(f => f.Id).Take(250001).ToListAsync(ct);
        if (rows.Count > 250000) throw new ApiException(413, "Review snapshot exceeds 250000 findings");
        var snapshot = new List<SecurityReviewDecision>();
        foreach (var chunk in rows.Chunk(500))
        {
            var ids = chunk.Select(f => f.Id).ToArray();
            var analysisIds = chunk.Select(f => f.LastAnalysisId).Distinct().ToArray();
            // The proof is the one of the finding's last observation, never a linked pull request occurrence.
            var evidence = await db.Set<FindingOccurrence>().AsNoTracking().Where(o => o.FindingId != null && ids.Contains(o.FindingId) && analysisIds.Contains(o.AnalysisId))
                .GroupBy(o => o.FindingId).Select(g => g.OrderByDescending(o => o.Id).First()).ToListAsync(ct);
            var decisions = await db.Set<FindingDecision>().AsNoTracking().Where(d => ids.Contains(d.FindingId) && d.Action == "review" && d.CreatedAt <= createdAt)
                .GroupBy(d => d.FindingId).Select(g => g.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).First()).ToListAsync(ct);
            var commits = await db.Set<AnalysisRecord>().AsNoTracking().Where(a => analysisIds.Contains(a.AnalysisId)).ToDictionaryAsync(a => a.AnalysisId, a => a.CommitSha, ct);
            var matches = await db.Set<FindingIdentityMatch>().AsNoTracking().Where(m => m.FindingId != null && ids.Contains(m.FindingId)).OrderBy(m => m.CreatedAt).Take(250001).ToListAsync(ct);
            if (matches.Count > 250000) throw new ApiException(413, "Review correspondence history exceeds 250000 entries");
            foreach (var finding in chunk)
            {
                var proof = evidence.SingleOrDefault(e => e.FindingId == finding.Id && e.AnalysisId == finding.LastAnalysisId);
                var decision = decisions.SingleOrDefault(d => d.FindingId == finding.Id);
                if (proof is null || decision is null) continue;
                using var decisionSnapshot = JsonDocument.Parse(decision.Snapshot);
                // A scan cannot retrospectively attach an old approval to its own proof or commit.
                if (!decisionSnapshot.RootElement.TryGetProperty("review_evidence", out var capturedProof)) continue;
                var reviewedProof = capturedProof.Deserialize<SecurityReviewProof>(ApiJson.Options);
                if (reviewedProof is null || reviewedProof.EvidenceHash != EvidenceHash(proof)) continue;
                reviewedProof = FollowProof(reviewedProof, commits.GetValueOrDefault(finding.LastAnalysisId, ""), proof.Path, proof.Line,
                    matches.Where(m => m.FindingId == finding.Id).ToArray());
                snapshot.Add(new(finding.Id, decision.Id, decisionSnapshot.RootElement.GetProperty("version").GetInt32(), project, branch, finding.Tool, finding.RuleId, finding.Category,
                    finding.Message, reviewedProof.Path, reviewedProof.Line, reviewedProof.Fingerprint, reviewedProof.EvidenceHash, finding.State, finding.HotspotReview,
                    finding.ExceptionReason, finding.ExceptionExpiresAt, reviewedProof.CommitSha));
            }
        }
        return JsonSerializer.Serialize(snapshot, ApiJson.Options);
    }

    /// <summary>Only a complete chain of confirmed, unchanged proof can advance the commit attached to an approval.</summary>
    public static SecurityReviewProof FollowProof(SecurityReviewProof proof, string commit, string? path, int? line, IReadOnlyCollection<FindingIdentityMatch> matches)
    {
        var edges = matches.Where(m => m.FindingId != null && m.Method is "git_line_map" or "fingerprint" && m.EvidenceHash == proof.EvidenceHash)
            .ToLookup(m => (m.BaseSha, m.OldPath, m.OldLine));
        var reached = new HashSet<(string Commit, string? Path, int? Line)> { (proof.CommitSha, proof.Path, proof.Line) };
        var pending = new Queue<(string Commit, string? Path, int? Line)>(); pending.Enqueue((proof.CommitSha, proof.Path, proof.Line));
        while (pending.TryDequeue(out var current))
            foreach (var match in edges[current])
                if (reached.Add((match.HeadSha, match.Path, match.Line))) pending.Enqueue((match.HeadSha, match.Path, match.Line));
        return reached.Contains((commit, path, line)) ? proof with { CommitSha = commit, Path = path, Line = line } : proof;
    }

    /// <summary>Locations can move without changing the observed defect; secondary and flow evidence still invalidate prior reviews.</summary>
    public static string EvidenceHash(FindingOccurrence finding)
    {
        using var evidence = JsonDocument.Parse(string.IsNullOrWhiteSpace(finding.Evidence) ? "null" : finding.Evidence);
        var value = evidence.RootElement;
        var contextual = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
            ? locations.EnumerateArray().Where(x => !x.TryGetProperty("kind", out var kind) || kind.GetString() != "primary").Select(x => x.Clone()).ToArray() : [];
        return AuthService.Sha256(JsonSerializer.Serialize(new { finding.Category, finding.Message, finding.Severity, contextual }, ApiJson.Options));
    }

    /// <summary>Evaluates frozen decisions. Caller supplies the exact new-code subset, not the bounded comparison detail list.</summary>
    public static SecurityReviewResult Apply(SecurityReviewPolicy policy, string snapshot, IngestionManifest manifest, DateTime capturedAt,
        IReadOnlyCollection<FindingOccurrence> findings, bool measured, IReadOnlyCollection<FindingOccurrence>? newCodeFindings = null, bool newCodeAvailable = false)
    {
        var decisions = JsonSerializer.Deserialize<SecurityReviewDecision[]>(snapshot, ApiJson.Options) ?? [];
        var usedExceptions = new HashSet<string>();
        var reviewed = new Dictionary<FindingOccurrence, string>();
        var byRule = decisions.Where(d => d.ProjectKey == manifest.ProjectKey && d.Branch == manifest.Branch).ToLookup(d => (d.Tool, d.RuleId));
        var fingerprints = findings.Where(f => !string.IsNullOrEmpty(f.Fingerprint)).GroupBy(f => (f.Tool, f.RuleId, f.Fingerprint)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var finding in findings)
        {
            var matches = byRule[(finding.Tool, finding.RuleId)].Where(d => Matches(d, finding, manifest.GitDiff, manifest.CommitSha)
                && (string.IsNullOrEmpty(finding.Fingerprint) || fingerprints[(finding.Tool, finding.RuleId, finding.Fingerprint)] == 1)).ToArray();
            if (matches.Length != 1 || matches[0].EvidenceHash != EvidenceHash(finding) || matches[0].State == "resolved") continue;
            var decision = matches[0];
            var expired = decision.State is "false_positive" or "accepted_risk" && (decision.ExpiresAt is null || decision.ExpiresAt <= capturedAt);
            if (!expired && decision.HotspotReview is "safe" or "to_fix") reviewed[finding] = decision.DecisionId;
            if (policy.ApplyApprovedExceptions && !expired && decision.State is "false_positive" or "accepted_risk" && !string.IsNullOrWhiteSpace(decision.Reason))
            {
                finding.Excepted = true;
                usedExceptions.Add(decision.DecisionId);
            }
        }
        var scoped = policy.Scope == "new_code" ? newCodeFindings ?? [] : findings;
        var hotspots = scoped.Where(f => f.Category == "security_hotspot").ToArray();
        var numerator = hotspots.Count(reviewed.ContainsKey);
        var available = measured && (policy.Scope == "global" || newCodeAvailable);
        var value = available && hotspots.Length > 0 ? 100.0 * numerator / hotspots.Length : (double?)null;
        var state = !available ? "not_measured" : hotspots.Length == 0 ? "not_applicable" : "measured";
        var result = !available ? "unknown" : hotspots.Length == 0 ? "not_applicable"
            : policy.MinimumHotspotReviewPercent is not { } threshold || value >= threshold ? "pass" : "fail";
        var reviewedIds = hotspots.Where(reviewed.ContainsKey).Select(f => reviewed[f]).ToHashSet();
        return new(policy.Scope, state, result, value, numerator, hotspots.Length, policy.MinimumHotspotReviewPercent,
            !available ? policy.Scope == "new_code" && !newCodeAvailable ? "new_code_unavailable" : "hotspots_not_measured" : hotspots.Length == 0 ? "no_hotspots_in_scope" : null,
            usedExceptions.Order().ToArray(), reviewedIds.Order().ToArray(), capturedAt,
            decisions.Where(d => usedExceptions.Contains(d.DecisionId) || reviewedIds.Contains(d.DecisionId)).ToArray());
    }

    public static bool Matches(SecurityReviewDecision decision, FindingOccurrence finding, GitDiff diff, string? currentCommit = null)
    {
        if (decision.Tool != finding.Tool || decision.RuleId != finding.RuleId || decision.Category != finding.Category || decision.Message != finding.Message) return false;
        if (!string.IsNullOrEmpty(decision.Fingerprint) && !string.IsNullOrEmpty(finding.Fingerprint)) return decision.Fingerprint == finding.Fingerprint;
        if (!string.IsNullOrEmpty(currentCommit) && decision.CommitSha == currentCommit && decision.Path == finding.Path && decision.Line == finding.Line) return true;
        if (diff.State != "available" || string.IsNullOrEmpty(decision.CommitSha) || diff.BaseSha != decision.CommitSha || decision.Line is not { } line) return false;
        if (!string.IsNullOrEmpty(currentCommit) && diff.HeadSha != currentCommit) return false;
        var file = diff.Files.SingleOrDefault(f => f.Path == finding.Path);
        if (file is null) return decision.Path == finding.Path && decision.Line == finding.Line;
        if ((file.OldPath ?? file.Path) != decision.Path || finding.Line is { } currentLine && file.AddedLines.Contains(currentLine)) return false;
        return file is not null && file.LineMap.TryGetValue(line.ToString(System.Globalization.CultureInfo.InvariantCulture), out var mapped) && mapped == finding.Line;
    }
}
