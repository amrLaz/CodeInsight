using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

/// <summary>
/// Daily state of each project's main branch, for portfolio trends. The current day is recomputed at every
/// maintenance run; missing past days are rebuilt from stored analyses, only with what those still prove.
/// </summary>
public static class PortfolioSnapshots
{
    public const int MaxDays = 400;
    public const int RebuildsPerRun = 50;

    private sealed record Analysis(string AnalysisId, DateTime CreatedAt, bool DetailsPurged, string? Security, string? Reliability,
        string? Maintainability, string? SecurityReview, int? Coverage, int? Duplication);

    private sealed record Counts(int Blocker, int High, int Medium, int Low, int Info, int Unknown);

    /// <summary>Refreshes today for every project and rebuilds missing days for a bounded number of projects.</summary>
    public static async Task<int> RefreshAsync(AppDbContext db, CancellationToken ct, DateTime? now = null, int rebuildsPerRun = RebuildsPerRun)
    {
        var clock = now ?? DateTime.UtcNow;
        var today = DateOnly.FromDateTime(clock);
        var projects = await db.Set<Project>().AsNoTracking().Where(x => x.MainBranch != null && !x.Archived)
            .OrderBy(x => x.Key).Select(x => new { x.Key, x.MainBranch }).ToListAsync(ct);
        var written = 0;
        var rebuilds = 0;
        foreach (var project in projects)
        {
            var branch = BranchRefs.Canonical(project.MainBranch);
            var analyses = (await db.Set<AnalysisRecord>().AsNoTracking()
                    .Where(x => x.ProjectKey == project.Key && x.Branch == branch && x.Status == "completed")
                    .Where(AnalysisKinds.Branch)
                    .Select(x => new { x.AnalysisId, x.CreatedAt, x.DetailsPurged, x.SecurityRating, x.ReliabilityRating, x.MaintainabilityRating, x.SecurityReviewRating, x.Coverage, x.Duplication })
                    .ToListAsync(ct))
                .Select(x => Parse(x.CreatedAt) is { } at
                    ? new Analysis(x.AnalysisId, at, x.DetailsPurged, x.SecurityRating, x.ReliabilityRating, x.MaintainabilityRating, x.SecurityReviewRating, x.Coverage, x.Duplication)
                    : null)
                .OfType<Analysis>().OrderBy(x => x.CreatedAt).ThenBy(x => x.AnalysisId).ToList();
            if (analyses.Count == 0) continue;
            var first = DateOnly.FromDateTime(analyses[0].CreatedAt);
            var start = first > today.AddDays(-MaxDays) ? first : today.AddDays(-MaxDays);
            var existing = (await db.Set<PortfolioSnapshot>().AsNoTracking()
                .Where(x => x.ProjectKey == project.Key && x.Day >= start).Select(x => x.Day).ToListAsync(ct)).ToHashSet();
            var days = new List<DateOnly>();
            if (rebuilds < rebuildsPerRun)
            {
                for (var day = start; day < today; day = day.AddDays(1))
                    if (!existing.Contains(day)) days.Add(day);
                if (days.Count > 0) rebuilds++;
            }
            days.Add(today);

            var counts = new Dictionary<string, Counts?>();
            var sla = await SlaCountsAsync(db, project.Key, branch, clock, ct);
            foreach (var day in days)
            {
                var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var analysis = analyses.LastOrDefault(x => x.CreatedAt < end);
                if (analysis is null) continue;
                if (!counts.TryGetValue(analysis.AnalysisId, out var severity))
                    counts[analysis.AnalysisId] = severity = await CountsAsync(db, analysis, ct);
                var live = day == today;
                var row = await db.Set<PortfolioSnapshot>().FindAsync([project.Key, day], ct);
                if (row is null)
                {
                    row = new PortfolioSnapshot { ProjectKey = project.Key, Day = day };
                    db.Add(row);
                }
                row.AnalysisId = analysis.AnalysisId;
                row.SecurityRating = analysis.Security;
                row.ReliabilityRating = analysis.Reliability;
                row.MaintainabilityRating = analysis.Maintainability;
                row.SecurityReviewRating = analysis.SecurityReview;
                row.Coverage = analysis.Coverage;
                row.Duplication = analysis.Duplication;
                row.BlockerFindings = severity?.Blocker;
                row.HighFindings = severity?.High;
                row.MediumFindings = severity?.Medium;
                row.LowFindings = severity?.Low;
                row.InfoFindings = severity?.Info;
                row.UnknownSeverityFindings = severity?.Unknown;
                row.SlaBreached = live ? sla.Breached : null;
                row.SlaDueSoon = live ? sla.DueSoon : null;
                row.Source = live ? "live" : "reconstructed";
                row.ComputedAt = clock;
                written++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return written;
    }

    /// <summary>Occurrences of the analysis' latest completed revision, as Issues lists them; null once purged.</summary>
    private static async Task<Counts?> CountsAsync(AppDbContext db, Analysis analysis, CancellationToken ct)
    {
        if (analysis.DetailsPurged) return null;
        var ingestion = await db.Set<IngestionRecord>().AsNoTracking().Where(x => x.AnalysisId == analysis.AnalysisId && x.CompletedAt != null)
            .OrderByDescending(x => x.Revision).Select(x => new { x.Id, x.DetailsPurged }).FirstOrDefaultAsync(ct);
        if (ingestion?.DetailsPurged == true) return null;
        var severities = await db.Set<FindingOccurrence>().AsNoTracking()
            .Where(x => x.AnalysisId == analysis.AnalysisId && !x.Excepted && (ingestion == null || x.IngestionId == ingestion.Id))
            .GroupBy(x => x.Severity).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var buckets = severities.GroupBy(x => FindingSla.SeverityOf(x.Key)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        int Get(string key) => buckets.GetValueOrDefault(key);
        return new Counts(Get("blocker"), Get("high"), Get("medium"), Get("low"), Get("info"), Get("unknown"));
    }

    private static async Task<(int Breached, int DueSoon)> SlaCountsAsync(AppDbContext db, string project, string branch, DateTime now, CancellationToken ct)
    {
        var findings = db.Set<DurableFinding>().AsNoTracking().Where(x => x.ProjectKey == project && x.Branch == branch);
        return (await FindingSla.FilterStatus(findings, "breached", now).CountAsync(ct),
            await FindingSla.FilterStatus(findings, "due_soon", now).CountAsync(ct));
    }

    private static DateTime? Parse(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime : null;
}
