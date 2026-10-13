using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

/// <summary>
/// Remediation deadlines of durable findings on their project's main branch. The deadline is
/// persisted so that lists filter by SLA status in SQL; the status itself depends on the clock
/// and the review state, and is derived at read time.
/// </summary>
public static class FindingSla
{
    public const string Pending = "pending";
    public const string Applies = "applies";

    public static readonly string[] Severities = ["blocker", "high", "medium", "low", "info"];
    public static readonly string[] Statuses =
        ["on_track", "due_soon", "breached", "suspended", "closed", "not_applicable", "not_configured", "indeterminate", "pending"];

    public const int MaxDays = 3650;

    public static string SeverityOf(string severity) =>
        string.IsNullOrWhiteSpace(severity) || severity.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? "unknown"
            : AnalysisFindings.SeverityBucket(severity);

    public static int? Days(SlaPolicy policy, string severity) => severity switch
    {
        "blocker" => policy.BlockerDays,
        "high" => policy.HighDays,
        "medium" => policy.MediumDays,
        "low" => policy.LowDays,
        "info" => policy.InfoDays,
        _ => null,
    };

    /// <summary>Recomputes the persisted deadline from the finding, its project's main branch and the effective policy.</summary>
    public static void Apply(DurableFinding finding, string? mainBranch, SlaPolicy? policy)
    {
        finding.SlaDueAt = null;
        finding.SlaWarnAt = null;
        if (string.IsNullOrEmpty(mainBranch)) { finding.SlaBasis = "unknown_main_branch"; return; }
        if (BranchRefs.Canonical(mainBranch) != BranchRefs.Canonical(finding.Branch)) { finding.SlaBasis = "not_main_branch"; return; }
        if (policy is null) { finding.SlaBasis = "no_policy"; return; }
        if (finding.Severity == "unknown") { finding.SlaBasis = "unknown_severity"; return; }
        if (Days(policy, finding.Severity) is not { } days) { finding.SlaBasis = "no_target"; return; }
        if (finding.FirstDetectedAt is not { } detected) { finding.SlaBasis = "unknown_detection"; return; }
        finding.SlaBasis = Applies;
        finding.SlaDueAt = detected.AddDays(days);
        // "Due soon" covers the last fifth of the delay, never less than one day.
        finding.SlaWarnAt = finding.SlaDueAt.Value.AddDays(-Math.Max(1d, days / 5d));
    }

    public static string Status(DurableFinding finding, DateTime now) => finding.SlaBasis switch
    {
        Pending => "pending",
        "not_main_branch" or "no_target" => "not_applicable",
        "no_policy" => "not_configured",
        Applies => FindingCollaboration.EffectiveState(finding, now) switch
        {
            "resolved" => "closed",
            "false_positive" or "accepted_risk" => "suspended",
            _ when now >= finding.SlaDueAt => "breached",
            _ when now >= finding.SlaWarnAt => "due_soon",
            _ => "on_track",
        },
        _ => "indeterminate",
    };

    public static object View(DurableFinding finding, DateTime now) => new
    {
        status = Status(finding, now),
        basis = finding.SlaBasis,
        due_at = finding.SlaDueAt,
        warn_at = finding.SlaWarnAt,
    };

    /// <summary>Same partition as <see cref="Status"/>, expressed for SQL filtering before pagination.</summary>
    public static IQueryable<DurableFinding> FilterStatus(IQueryable<DurableFinding> query, string status, DateTime now)
    {
        if (status.Length == 0 || status == "all") return query;
        if (!Statuses.Contains(status)) throw new ApiException(422, "Invalid sla_status");
        var running = query.Where(x => x.SlaBasis == Applies
            && (x.State == "open" || x.State == "confirmed" || (x.State == "false_positive" || x.State == "accepted_risk") && x.ExceptionExpiresAt <= now));
        return status switch
        {
            "breached" => running.Where(x => x.SlaDueAt <= now),
            "due_soon" => running.Where(x => x.SlaWarnAt <= now && x.SlaDueAt > now),
            "on_track" => running.Where(x => x.SlaWarnAt > now),
            "suspended" => query.Where(x => x.SlaBasis == Applies && (x.State == "false_positive" || x.State == "accepted_risk") && x.ExceptionExpiresAt > now),
            "closed" => query.Where(x => x.SlaBasis == Applies && x.State == "resolved"),
            "not_applicable" => query.Where(x => x.SlaBasis == "not_main_branch" || x.SlaBasis == "no_target"),
            "not_configured" => query.Where(x => x.SlaBasis == "no_policy"),
            "pending" => query.Where(x => x.SlaBasis == Pending),
            _ => query.Where(x => x.SlaBasis != Applies && x.SlaBasis != Pending && x.SlaBasis != "not_main_branch"
                && x.SlaBasis != "no_target" && x.SlaBasis != "no_policy"),
        };
    }

    public static string ScopeKey(string? projectKey) => projectKey is null ? "global" : "project:" + projectKey;

    /// <summary>The project override replaces the global policy entirely; no merge between the two.</summary>
    public static async Task<SlaPolicy?> EffectivePolicyAsync(AppDbContext db, string projectKey, CancellationToken ct) =>
        await db.Set<SlaPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == ScopeKey(projectKey), ct)
        ?? await db.Set<SlaPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == "global", ct);

    /// <summary>Marks findings for recomputation and wakes the worker; used after a policy or main-branch change.</summary>
    public static async Task InvalidateAsync(AppDbContext db, string? projectKey, CancellationToken ct)
    {
        var query = db.Set<DurableFinding>().Where(x => x.SlaBasis != Pending);
        if (projectKey is not null) query = query.Where(x => x.ProjectKey == projectKey);
        else query = query.Where(x => !db.Set<SlaPolicy>().Any(p => p.ProjectKey == x.ProjectKey));
        await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.SlaBasis, Pending), ct);
        await EnqueueAsync(db, ct);
    }

    public static Task<int> EnqueueAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO durable_jobs (id,kind,resource_id,state,attempts,available_at,error,created_at) VALUES ({Guid.NewGuid().ToString()},'sla','all','pending',0,{now},'',{now}) ON CONFLICT (kind,resource_id) DO UPDATE SET state = 'pending', attempts = 0, available_at = EXCLUDED.available_at, error = '' WHERE durable_jobs.state IN ('completed','failed')",
            ct);
    }

    /// <summary>Computes pending findings by project and branch under the collaboration lock, in bounded batches.</summary>
    public static async Task<int> RecomputeAsync(AppDbContext db, CancellationToken ct, int batches = 20)
    {
        // The first ingestion on main/master may have set the main branch after its findings were computed.
        await db.Set<DurableFinding>()
            .Where(x => x.SlaBasis == "unknown_main_branch" && db.Set<Project>().Any(p => p.Key == x.ProjectKey && p.MainBranch != null))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SlaBasis, Pending), ct);
        var total = 0;
        for (var round = 0; round < batches; round++)
        {
            var groups = await db.Set<DurableFinding>().AsNoTracking().Where(x => x.SlaBasis == Pending)
                .OrderBy(x => x.ProjectKey).ThenBy(x => x.Branch).ThenBy(x => x.Id).Take(500)
                .Select(x => new { x.ProjectKey, x.Branch }).Distinct().ToListAsync(ct);
            if (groups.Count == 0) break;
            foreach (var group in groups)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await FindingCollaboration.LockAsync(db, group.ProjectKey, group.Branch, ct);
                var rows = await db.Set<DurableFinding>()
                    .Where(x => x.ProjectKey == group.ProjectKey && x.Branch == group.Branch && x.SlaBasis == Pending)
                    .OrderBy(x => x.Id).Take(500).ToListAsync(ct);
                var mainBranch = await db.Set<Project>().Where(x => x.Key == group.ProjectKey).Select(x => x.MainBranch).SingleOrDefaultAsync(ct);
                var policy = await EffectivePolicyAsync(db, group.ProjectKey, ct);
                await BackfillAsync(db, rows.Where(x => x.FirstDetectedAt is null || x.Severity == "unknown").ToList(), ct);
                foreach (var row in rows) Apply(row, mainBranch, policy);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                total += rows.Count;
            }
        }
        return total;
    }

    /// <summary>
    /// Findings tracked before this feature get their detection date from the earliest analysis still holding one of
    /// their occurrences, only when no earlier analysis of the branch lost its details: otherwise the true first
    /// detection may be older and the date stays unknown.
    /// </summary>
    public static async Task BackfillAsync(AppDbContext db, IReadOnlyCollection<DurableFinding> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var ids = rows.Select(x => x.Id).ToArray();
        // A pull request build analyses a merge result: it does not prove when the branch first contained the finding.
        var seen = await db.Set<FindingOccurrence>().AsNoTracking().Where(o => o.FindingId != null && ids.Contains(o.FindingId))
            .Join(db.Set<AnalysisRecord>().Where(AnalysisKinds.Branch), o => o.AnalysisId, a => a.AnalysisId, (o, a) => new { o.Id, o.FindingId, o.Severity, a.CreatedAt })
            .ToListAsync(ct);
        var purged = new Dictionary<(string, string), List<DateTime?>>();
        foreach (var row in rows)
        {
            var occurrences = seen.Where(x => x.FindingId == row.Id).ToList();
            if (row.Severity == "unknown" && occurrences.MaxBy(x => x.Id) is { } latest) row.Severity = SeverityOf(latest.Severity);
            if (row.FirstDetectedAt is not null) continue;
            var dates = occurrences.Select(x => Parse(x.CreatedAt)).ToList();
            if (dates.Count == 0 || dates.Any(x => x is null)) continue;
            var earliest = dates.Min()!.Value;
            if (!purged.TryGetValue((row.ProjectKey, row.Branch), out var lost))
            {
                var texts = await db.Set<AnalysisRecord>().AsNoTracking()
                    .Where(a => a.ProjectKey == row.ProjectKey && a.Branch == row.Branch
                        && (a.DetailsPurged || db.Set<IngestionRecord>().Any(i => i.AnalysisId == a.AnalysisId && i.DetailsPurged)))
                    .Select(a => a.CreatedAt).ToListAsync(ct);
                purged[(row.ProjectKey, row.Branch)] = lost = texts.Select(Parse).ToList();
            }
            // An unparsable purged date could precede the finding: it is treated as a possible loss.
            if (!lost.Any(x => x is null || x < earliest)) row.FirstDetectedAt = earliest;
        }
    }

    private static DateTime? Parse(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime : null;

    /// <summary>
    /// Queues one digest per project and enabled channel for findings that entered breach since their last
    /// notification. A finding is notified once per deadline; a later deadline (policy change) may notify again.
    /// </summary>
    public static async Task<int> NotifyAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var candidates = FilterStatus(db.Set<DurableFinding>(), "breached", now)
            .Where(x => x.SlaNotifiedAt == null || x.SlaNotifiedAt < x.SlaDueAt);
        var projects = await candidates.Select(x => x.ProjectKey).Distinct().ToListAsync(ct);
        if (projects.Count == 0) return 0;
        var channels = await db.Set<NotificationChannel>().AsNoTracking().Where(x => x.Enabled && x.OnSlaBreach).Select(x => x.Id).ToListAsync(ct);
        var queued = 0;
        foreach (var project in projects)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var marked = await candidates.Where(x => x.ProjectKey == project)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SlaNotifiedAt, stamp), ct);
            // Without an enabled channel the breach is still recorded, so enabling one later does not replay history.
            foreach (var channel in channels)
                db.Add(new DurableJob { Kind = "sla-notification", ResourceId = $"{project}|{stamp.Ticks}|{channel}" });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            queued += marked > 0 ? channels.Count : 0;
        }
        return queued;
    }
}
