using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

public static class FindingIdentityContinuity
{
    /// <summary>Finds a unique old occurrence using producer fingerprints or an explicit unchanged-line Git mapping.</summary>
    public static FindingOccurrence[] Candidates(FindingOccurrence current, IReadOnlyCollection<FindingOccurrence> baseline, GitDiff diff)
    {
        var sameRule = baseline.Where(o => o.FindingId != null && o.Tool == current.Tool && o.RuleId == current.RuleId);
        if (!string.IsNullOrEmpty(current.Fingerprint))
        {
            var matches = sameRule.Where(o => o.Fingerprint == current.Fingerprint).ToArray();
            if (matches.Length > 0) return matches;
        }
        var file = diff.Files.SingleOrDefault(f => f.Path == current.Path);
        if (file is null) return [];
        return sameRule.Where(o => o.Path == (file.OldPath ?? file.Path) && o.Line is { } oldLine
            && file.LineMap.TryGetValue(oldLine.ToString(System.Globalization.CultureInfo.InvariantCulture), out var line) && line == current.Line
            && o.Message == current.Message && o.Category == current.Category
            && (string.IsNullOrEmpty(o.Fingerprint) || string.IsNullOrEmpty(current.Fingerprint) || o.Fingerprint == current.Fingerprint)).ToArray();
    }

    /// <summary>
    /// Caller holds the project/branch transaction lock. Cross-branch baselines never transfer an identity.
    /// <paramref name="readOnly"/> only reuses recorded correspondences: a pull request build records none, so
    /// its merge result never shadows the continuity of its source branch.
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveAsync(AppDbContext db, AnalysisRecord analysis,
        IReadOnlyCollection<FindingOccurrence> occurrences, GitDiff? diff, CancellationToken ct, bool readOnly = false)
    {
        var uniqueFingerprints = occurrences.Where(o => !string.IsNullOrEmpty(o.Fingerprint)).GroupBy(o => (o.Tool, o.RuleId, o.Fingerprint)).ToDictionary(g => g.Key, g => g.Count() == 1);
        string Identity(FindingOccurrence o) => FindingCollaboration.Identity(analysis.ProjectKey, analysis.Branch, o,
            string.IsNullOrEmpty(o.Fingerprint) || uniqueFingerprints[(o.Tool, o.RuleId, o.Fingerprint)]);
        var result = new Dictionary<string, string>();
        var byIdentity = occurrences.GroupBy(Identity).ToDictionary(g => g.Key, g => g.First());
        var identities = byIdentity.Keys.ToArray();
        foreach (var chunk in identities.Chunk(500))
        {
            var matches = await db.Set<FindingIdentityMatch>().AsNoTracking().Where(m => m.ProjectKey == analysis.ProjectKey && m.Branch == analysis.Branch
                && chunk.Contains(m.OccurrenceIdentity)).GroupBy(m => m.OccurrenceIdentity)
                .Select(g => g.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).First()).ToListAsync(ct);
            foreach (var match in matches)
                if (match.FindingId is not null) result[match.OccurrenceIdentity] = match.FindingId;
        }
        if (readOnly || diff is null || diff.State != "available" || diff.HeadSha != analysis.CommitSha) return result;
        var baselineAnalysisId = await db.Set<AnalysisRecord>().AsNoTracking().Where(a => a.ProjectKey == analysis.ProjectKey && a.Branch == analysis.Branch
            && a.CommitSha == diff.BaseSha && !a.DetailsPurged && a.Status == "completed").OrderByDescending(a => a.CreatedAt).Select(a => a.AnalysisId).FirstOrDefaultAsync(ct);
        if (baselineAnalysisId is null) return result;
        var baselineIngestionId = await db.Set<IngestionRecord>().AsNoTracking().Where(i => i.AnalysisId == baselineAnalysisId && i.State == "completed" && !i.DetailsPurged)
            .OrderByDescending(i => i.Revision).Select(i => i.Id).FirstOrDefaultAsync(ct) ?? "";
        var baseline = await db.Set<FindingOccurrence>().AsNoTracking().Where(o => o.AnalysisId == baselineAnalysisId && o.IngestionId == baselineIngestionId && o.FindingId != null)
            .Take(250001).ToListAsync(ct);
        if (baseline.Count > 250000) throw new InvalidDataException("Identity baseline exceeds 250000 occurrences");
        var byFingerprint = baseline.Where(o => !string.IsNullOrEmpty(o.Fingerprint)).ToLookup(o => (o.Tool, o.RuleId, o.Fingerprint));
        var byPath = baseline.ToLookup(o => (o.Tool, o.RuleId, o.Path));
        var files = diff.Files.ToDictionary(f => f.Path);
        var proposed = occurrences.Select(o =>
        {
            var fingerprints = byFingerprint[(o.Tool, o.RuleId, o.Fingerprint)].ToArray();
            var oldPath = o.Path is not null ? files.GetValueOrDefault(o.Path)?.OldPath ?? o.Path : null;
            return (Current: o, Identity: Identity(o), Candidates: fingerprints.Length > 0 ? fingerprints : Candidates(o, byPath[(o.Tool, o.RuleId, oldPath)].ToArray(), diff));
        }).ToArray();
        var used = proposed.Where(p => p.Candidates.Length == 1).GroupBy(p => p.Candidates[0].FindingId).ToDictionary(g => g.Key!, g => g.Count());
        var ingestionId = occurrences.FirstOrDefault()?.IngestionId ?? "";
        var existingIds = (await db.Set<FindingIdentityMatch>().Where(m => m.AnalysisId == analysis.AnalysisId && m.IngestionId == ingestionId).Select(m => m.Id).ToListAsync(ct)).ToHashSet();
        foreach (var item in proposed)
        {
            if (item.Candidates.Length == 0) continue;
            var matched = item.Candidates.Length == 1 && used[item.Candidates[0].FindingId!] == 1;
            var old = matched ? item.Candidates[0] : null;
            if (old is not null) result[item.Identity] = old.FindingId!;
            else result.Remove(item.Identity);
            var id = AuthService.Sha256(JsonSerializer.Serialize(new[] { analysis.AnalysisId, ingestionId, item.Identity, diff.BaseSha, diff.HeadSha }));
            if (!existingIds.Add(id)) continue;
            db.Add(new FindingIdentityMatch { Id = id, ProjectKey = analysis.ProjectKey, Branch = analysis.Branch, AnalysisId = analysis.AnalysisId,
                IngestionId = ingestionId, OccurrenceIdentity = item.Identity, FindingId = old?.FindingId, Method = old is null ? "indeterminate"
                    : !string.IsNullOrEmpty(item.Current.Fingerprint) && old.Fingerprint == item.Current.Fingerprint ? "fingerprint" : "git_line_map",
                BaseSha = diff.BaseSha, HeadSha = diff.HeadSha, OldPath = old?.Path, OldLine = old?.Line, Path = item.Current.Path, Line = item.Current.Line,
                EvidenceHash = SecurityReviewEvaluation.EvidenceHash(item.Current) });
        }
        return result;
    }
}
