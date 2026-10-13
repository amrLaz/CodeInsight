using System.Text.Json.Nodes;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Services;

public sealed class IngestionWorker(
    IServiceScopeFactory scopes,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> logger
) : BackgroundService
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new(
        "CodeInsights.Ingestion",
        "1.0"
    );
    private static readonly System.Diagnostics.Metrics.Histogram<double> JobDuration =
        Meter.CreateHistogram<double>("codeinsights.job.duration", "s");

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            Enumerable
                .Range(0, Math.Clamp(options.Value.Concurrency, 1, 8))
                .Select(_ => RunAsync(stoppingToken))
        );

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ScheduleMaintenanceAsync(db, stoppingToken);
                var job = await ClaimAsync(db, options.Value, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var heartbeat = RenewAsync(job.Id, job.LeaseOwner!, work);
                try
                {
                    switch (job.Kind)
                    {
                        case "ingest":
                            await scope
                                .ServiceProvider.GetRequiredService<IngestionProcessor>()
                                .ProcessAsync(job.ResourceId, job.LeaseOwner!, work.Token);
                            break;
                        case "ado-pending":
                        case "ado":
                            var row = await db.Set<IngestionRecord>()
                                .FindAsync([job.ResourceId], work.Token);
                            if (row is not null)
                                await scope
                                    .ServiceProvider.GetRequiredService<IngestionAdo>()
                                    .SendAsync(row, work.Token);
                            break;
                        case "pr-decoration":
                            var analysisId = job.ResourceId.Split(':', 2)[0];
                            var analysis = await db.Set<AnalysisRecord>()
                                .FindAsync([analysisId], work.Token);
                            if (analysis is not null)
                            {
                                await scope
                                    .ServiceProvider.GetRequiredService<PullRequestDecoration>()
                                    .PublishCurrentAsync(analysis, work.Token);
                                analysis.PrDecorationStatus = "posted";
                                analysis.PrDecorationError = "";
                                await db.SaveChangesAsync(work.Token);
                            }
                            break;
                        case "notification":
                            var parts = job.ResourceId.Split(':', 2);
                            var ingestion = await db.Set<IngestionRecord>()
                                .FindAsync([parts[0]], work.Token);
                            var channel = await db.Set<NotificationChannel>()
                                .FindAsync([parts[1]], work.Token);
                            if (ingestion is not null && channel?.Enabled == true)
                                await scope
                                    .ServiceProvider.GetRequiredService<NotificationService>()
                                    .SendAsync(
                                        channel.ChannelType,
                                        JsonNode.Parse(channel.Config)!.AsObject(),
                                        ingestion.QualityGate,
                                        new JsonObject
                                        {
                                            ["analysis_id"] = ingestion.AnalysisId,
                                            ["project_key"] = ingestion.ProjectKey,
                                            ["gate_snapshot"] = JsonNode.Parse(
                                                ingestion.GateSnapshot
                                            ),
                                            ["event_id"] = job.Id,
                                        },
                                        work.Token
                                    );
                            break;
                        case "sla":
                            await FindingSla.RecomputeAsync(db, work.Token);
                            await FindingSla.NotifyAsync(db, work.Token);
                            await FindingWorkItems.ApplyRulesAsync(db, work.Token);
                            break;
                        case "work-item":
                            await scope
                                .ServiceProvider.GetRequiredService<FindingWorkItems>()
                                .CreateAsync(job.ResourceId, work.Token);
                            break;
                        case "work-item-comment":
                            await scope
                                .ServiceProvider.GetRequiredService<FindingWorkItems>()
                                .CommentResolvedAsync(job.ResourceId, work.Token);
                            break;
                        case "sla-notification":
                            await scope
                                .ServiceProvider.GetRequiredService<NotificationService>()
                                .SendSlaBreachAsync(job.ResourceId, work.Token);
                            break;
                        case "maintenance":
                            await FindingCollaboration.ExpireAsync(db, work.Token);
                            await FindingSla.RecomputeAsync(db, work.Token);
                            await FindingSla.NotifyAsync(db, work.Token);
                            await FindingWorkItems.ApplyRulesAsync(db, work.Token);
                            await PortfolioSnapshots.RefreshAsync(db, work.Token);
                            await scope
                                .ServiceProvider.GetRequiredService<RetentionService>()
                                .StartupAsync(work.Token);
                            await scope
                                .ServiceProvider.GetRequiredService<IngestionMaintenance>()
                                .RunAsync(work.Token);
                            break;
                    }
                    await db.Set<DurableJob>()
                        .Where(x => x.Id == job.Id && x.LeaseOwner == job.LeaseOwner)
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.State, "completed")
                                    .SetProperty(x => x.LeaseUntil, (DateTime?)null)
                                    .SetProperty(x => x.LeaseOwner, (string?)null),
                            stoppingToken
                        );
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    db.ChangeTracker.Clear();
                    var terminal =
                        ex is InvalidDataException || job.Attempts >= options.Value.MaxAttempts;
                    var safeError =
                        ex is InvalidDataException
                            ? ex.Message[..Math.Min(ex.Message.Length, 500)]
                            : ex.GetType().Name;
                    var retry = DateTime.UtcNow.AddSeconds(
                        Math.Min(120, Math.Pow(2, job.Attempts))
                    );
                    await using var failureTransaction = await db.Database.BeginTransactionAsync(
                        stoppingToken
                    );
                    var changed = await db.Set<DurableJob>()
                        .Where(x => x.Id == job.Id && x.LeaseOwner == job.LeaseOwner)
                        .ExecuteUpdateAsync(
                            s =>
                                s.SetProperty(x => x.State, terminal ? "failed" : "pending")
                                    .SetProperty(x => x.Error, safeError)
                                    .SetProperty(x => x.AvailableAt, retry)
                                    .SetProperty(x => x.LeaseOwner, (string?)null)
                                    .SetProperty(x => x.LeaseUntil, (DateTime?)null),
                            stoppingToken
                        );
                    if (terminal && changed > 0 && job.Kind == "ingest")
                        await MarkIngestionFailedAsync(
                            db,
                            job.ResourceId,
                            safeError,
                            stoppingToken
                        );
                    if (terminal && changed > 0 && job.Kind == "work-item")
                        await FindingWorkItems.FailAsync(db, job.ResourceId, safeError, stoppingToken);
                    if (terminal && changed > 0 && job.Kind == "pr-decoration")
                    {
                        var analysisId = job.ResourceId.Split(':', 2)[0];
                        await db.Set<AnalysisRecord>()
                            .Where(x => x.AnalysisId == analysisId)
                            .ExecuteUpdateAsync(
                                s =>
                                    s.SetProperty(x => x.PrDecorationStatus, "error")
                                        .SetProperty(x => x.PrDecorationError, safeError),
                                stoppingToken
                            );
                    }
                    await failureTransaction.CommitAsync(stoppingToken);
                    logger.LogWarning(
                        ex,
                        "Job {Id} {Kind}: {State} ({Error})",
                        job.Id,
                        job.Kind,
                        terminal ? "failed" : "retry",
                        safeError
                    );
                }
                finally
                {
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                    JobDuration.Record(
                        elapsed,
                        new KeyValuePair<string, object?>("kind", job.Kind)
                    );
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation(
                            "Job {Id} {Kind} resource {ResourceId} attempt {Attempt} elapsed {Seconds}s",
                            job.Id,
                            job.Kind,
                            job.ResourceId,
                            job.Attempts,
                            elapsed
                        );
                    await work.CancelAsync();
                    try
                    {
                        await heartbeat;
                    }
                    catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Worker unavailable: {Type}", ex.GetType().Name);
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    public static async Task<DurableJob?> ClaimAsync(
        AppDbContext db,
        IngestionOptions options,
        CancellationToken ct
    )
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var job = await db.Set<DurableJob>()
            .FromSqlInterpolated(
                $"SELECT * FROM durable_jobs WHERE (state = 'pending' AND available_at <= {now}) OR (state = 'running' AND lease_until < {now}) ORDER BY available_at, id FOR UPDATE SKIP LOCKED LIMIT 1"
            )
            .FirstOrDefaultAsync(ct);
        if (job is null)
            return null;
        if (job.Attempts >= options.MaxAttempts)
        {
            job.State = "failed";
            job.Error = "Retry budget exhausted after lease expiry";
            job.LeaseOwner = null;
            job.LeaseUntil = null;
            if (job.Kind == "ingest")
                await MarkIngestionFailedAsync(db, job.ResourceId, "Retry budget exhausted", ct);
            if (job.Kind == "work-item")
                await FindingWorkItems.FailAsync(db, job.ResourceId, "Retry budget exhausted", ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }
        job.Attempts++;
        job.State = "running";
        job.LeaseOwner = Guid.NewGuid().ToString();
        job.LeaseUntil = now.AddSeconds(Math.Max(30, options.LeaseSeconds));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.Entry(job).State = EntityState.Detached;
        return job;
    }

    /// <summary>Persists a terminal receipt and its PR-decoration outbox entry inside the caller's transaction.</summary>
    public static async Task MarkIngestionFailedAsync(
        AppDbContext db,
        string id,
        string safeError,
        CancellationToken ct,
        string? expectedState = null
    )
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Terminal failure requires a transaction.");
        var now = DateTime.UtcNow;
        var changed = await db.Set<IngestionRecord>()
            .Where(x =>
                x.Id == id
                && x.State != "completed"
                && x.State != "failed"
                && (expectedState == null || x.State == expectedState)
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.State, "failed")
                        .SetProperty(x => x.TechnicalState, "failed")
                        .SetProperty(x => x.QualityGate, "unknown")
                        .SetProperty(
                            x => x.Comparison,
                            "{\"state\":\"unavailable\",\"reason\":\"ingestion_failed\"}"
                        )
                        .SetProperty(x => x.Error, safeError)
                        .SetProperty(x => x.CompletedAt, now),
                ct
            );
        if (changed == 0)
            return;
        var analysisId = await db.Set<IngestionRecord>()
            .Where(x => x.Id == id)
            .Select(x => x.AnalysisId)
            .SingleAsync(ct);
        var analysis = await db.Set<AnalysisRecord>().FindAsync([analysisId], ct);
        if (analysis is not null && analysis.Status == "running")
        {
            analysis.Status = "failed";
            analysis.QualityGate = "unknown";
            analysis.QualityGateSummary = GateEvaluation.Summary("unknown", 0);
            analysis.FailureCode = safeError[..Math.Min(100, safeError.Length)];
            analysis.CompletedAt = now;
            analysis.DecorationRevision++;
            analysis.PrDecorationStatus = "queued";
            db.Add(
                new DurableJob
                {
                    Kind = "pr-decoration",
                    ResourceId = $"{analysis.AnalysisId}:{analysis.DecorationRevision}",
                }
            );
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task RenewAsync(string id, string owner, CancellationTokenSource work)
    {
        while (!work.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(10, options.Value.LeaseSeconds / 3)),
                work.Token
            );
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var until = DateTime.UtcNow.AddSeconds(Math.Max(30, options.Value.LeaseSeconds));
            if (
                await db.Set<DurableJob>()
                    .Where(x => x.Id == id && x.State == "running" && x.LeaseOwner == owner)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, until), work.Token)
                != 1
            )
            {
                await work.CancelAsync();
                break;
            }
        }
    }

    private static Task<int> ScheduleMaintenanceAsync(AppDbContext db, CancellationToken ct)
    {
        var resource = DateTime.UtcNow.ToString("yyyy-MM-dd-HH");
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO durable_jobs (id,kind,resource_id,state,attempts,available_at,error,created_at) VALUES ({id},'maintenance',{resource},'pending',0,{now},'',{now}) ON CONFLICT (kind,resource_id) DO NOTHING",
            ct
        );
    }
}

public sealed class IngestionMaintenance(
    AppDbContext db,
    ReportStorage storage,
    IOptions<IngestionOptions> options
)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var abandoned = DateTime.UtcNow.AddHours(-24);
        var abandonedIds = await db.Set<IngestionRecord>()
            .Where(x => x.State == "receiving" && x.CreatedAt < abandoned)
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .Select(x => x.Id)
            .ToListAsync(ct);
        foreach (var id in abandonedIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await IngestionWorker.MarkIngestionFailedAsync(
                db,
                id,
                "Publication abandoned before finalization",
                ct,
                "receiving"
            );
            await transaction.CommitAsync(ct);
        }
        var abandonedAnalyses = await db.Set<AnalysisRecord>()
            .Where(x => x.Status == "running" && x.StartedAt < abandoned)
            .OrderBy(x => x.StartedAt)
            .Take(100)
            .ToListAsync(ct);
        foreach (var analysis in abandonedAnalyses)
            await AnalysisLifecycle.FailAsync(db, analysis, "analysis_abandoned", ct);
        if (
            string.IsNullOrEmpty(options.Value.BlobConnectionString)
            && string.IsNullOrEmpty(options.Value.BlobServiceUri)
        )
            return;
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var rows = await db.Set<IngestionRecord>()
            .Where(x =>
                !x.DetailsPurged
                && x.CreatedAt < cutoff
                && (x.State == "completed" || x.State == "failed")
            )
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var reports = await db.Set<IngestionReport>()
                .Where(x => x.IngestionId == row.Id && x.BlobName != "")
                .ToListAsync(ct);
            foreach (var report in reports)
            {
                if (
                    !await db.Set<IngestionReport>()
                        .AnyAsync(x => x.IngestionId != row.Id && x.BlobName == report.BlobName, ct)
                )
                    await storage.DeleteAsync(report.BlobName, ct);
                report.BlobName = "";
            }
            await db.Set<FindingOccurrence>()
                .Where(x => x.IngestionId == row.Id)
                .ExecuteDeleteAsync(ct);
            await db.Set<CoverageLine>().Where(x => x.IngestionId == row.Id).ExecuteDeleteAsync(ct);
            await db.Set<SbomComponent>()
                .Where(x => x.IngestionId == row.Id)
                .ExecuteDeleteAsync(ct);
            PurgeCodeDetails(row);
            row.DetailsPurged = true;
            await db.SaveChangesAsync(ct);
        }
        var analyses = await db.Set<AnalysisRecord>()
            .Where(x => !x.DetailsPurged && x.CompletedAt < cutoff)
            .OrderBy(x => x.CompletedAt)
            .Take(100)
            .ToListAsync(ct);
        foreach (var analysis in analyses)
        {
            var sources = await db.Set<AnalysisSource>()
                .Where(x => x.AnalysisId == analysis.AnalysisId)
                .ToListAsync(ct);
            foreach (var source in sources)
                if (source.BlobName.Length > 0)
                    await storage.DeleteAsync(source.BlobName, ct);
            db.RemoveRange(sources);
            analysis.DetailsPurged = true;
            await db.SaveChangesAsync(ct);
        }
        await foreach (var name in storage.AbandonedAsync(abandoned, ct))
            if (
                !await db.Set<IngestionReport>().AnyAsync(x => x.BlobName == name, ct)
                && !await db.Set<AnalysisSource>().AnyAsync(x => x.BlobName == name, ct)
            )
                await storage.DeleteAsync(name, ct);
    }

    public static void PurgeCodeDetails(IngestionRecord row)
    {
        row.CodeData = "{}";
        var manifest = JsonNode.Parse(row.Manifest)!.AsObject();
        manifest["repository_files"] = new JsonArray();
        row.Manifest = manifest.ToJsonString(ApiJson.Options);
    }
}
