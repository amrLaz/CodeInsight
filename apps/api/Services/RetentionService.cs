using System.Globalization;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

public sealed class RetentionService(AppDbContext db, ILogger<RetentionService> logger)
{
    public async Task StartupAsync(CancellationToken ct = default)
    {
        try
        {
            if ((await GetSettingAsync("retention_auto_cleanup", "true", ct)).ToLowerInvariant() is not ("1" or "true" or "yes")) return;
            var value = await GetSettingAsync("last_cleanup_at", "", ct);
            if (DateTimeOffset.TryParse(value, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return;
            var result = await RunAsync(ct);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Startup retention purged details for {Count} analyses", result.DeletedAnalyses);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested) { logger.LogWarning(exception, "Startup retention failed"); }
    }
    public async Task<string> GetSettingAsync(
        string key,
        string fallback = "",
        CancellationToken ct = default
    ) =>
        await db.Set<PlatformSetting>()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
        ?? fallback;

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        var row = await db.Set<PlatformSetting>().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
            db.Add(
                new PlatformSetting
                {
                    Id = Guid.NewGuid().ToString(),
                    Key = key,
                    Value = value,
                }
            );
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task<CleanupResult> RunAsync(CancellationToken ct = default)
    {
        var keep = Math.Max(
            1,
            int.Parse(
                await GetSettingAsync("retention_keep_days", "90", ct),
                CultureInfo.InvariantCulture
            )
        );
        var failed = Math.Max(
            1,
            int.Parse(
                await GetSettingAsync("retention_keep_failed_days", "30", ct),
                CultureInfo.InvariantCulture
            )
        );
        var maximum = Math.Max(
            1,
            int.Parse(
                await GetSettingAsync("retention_max_per_branch", "50", ct),
                CultureInfo.InvariantCulture
            )
        );
        var auditKeep = Math.Max(
            30,
            int.Parse(
                await GetSettingAsync("audit_log_retention_days", "365", ct),
                CultureInfo.InvariantCulture
            )
        );
        var branches = (
            await GetSettingAsync("retention_protected_branches", "master,main,develop", ct)
        ).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var now = DateTime.UtcNow;
        var cutoff = new DateTimeOffset(now.AddDays(-keep)).ToString("o");
        var failedCutoff = new DateTimeOffset(now.AddDays(-failed)).ToString("o");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // ponytail: apply retention rules in order; SQL ranks runs without materializing the catalog.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"CREATE TEMP TABLE cleanup_analysis_ids ON COMMIT DROP AS SELECT analysis_id FROM analysis_records WHERE NOT details_purged AND ((status = 'failed' AND created_at < {failedCutoff}) OR (NOT (branch = ANY({branches})) AND created_at < {cutoff}))",
            ct
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO cleanup_analysis_ids SELECT analysis_id FROM (SELECT analysis_id, ROW_NUMBER() OVER (PARTITION BY project_key, branch ORDER BY created_at DESC, analysis_id DESC) AS rank FROM analysis_records WHERE NOT details_purged AND analysis_id NOT IN (SELECT analysis_id FROM cleanup_analysis_ids)) ranked WHERE rank > {maximum}",
            ct
        );
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM finding_occurrences WHERE analysis_id IN (SELECT analysis_id FROM cleanup_analysis_ids) AND analysis_id NOT IN (SELECT analysis_id FROM ingestions)",
            ct
        );
        // ponytail: the existing record is the durable summary; keep its stable ID and measured versions.
        var deleted = await db.Database.ExecuteSqlRawAsync(
            "UPDATE analysis_records SET affected_files = '{{}}', details_purged = TRUE WHERE analysis_id IN (SELECT analysis_id FROM cleanup_analysis_ids) AND analysis_id NOT IN (SELECT analysis_id FROM ingestions)",
            ct
        );
        var auditCutoff = now.AddDays(-auditKeep);
        var deletedAudit = await db.Set<AuditLog>()
            .Where(x => x.CreatedAt < auditCutoff)
            .ExecuteDeleteAsync(ct);
        var timestamp = new DateTimeOffset(now).ToString("o");
        await SetSettingAsync("last_cleanup_at", timestamp, ct);
        await SetSettingAsync(
            "last_cleanup_deleted",
            deleted.ToString(CultureInfo.InvariantCulture),
            ct
        );
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(deleted, deletedAudit, timestamp, new(keep, failed, maximum, auditKeep));
    }
}

public sealed record CleanupResult(
    int DeletedAnalyses,
    int DeletedAuditLogs,
    string CleanupAt,
    CleanupSettings SettingsApplied
);

public sealed record CleanupSettings(
    int KeepDays,
    int KeepFailedDays,
    int MaxPerBranch,
    int AuditLogRetentionDays
);
