using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

public static class AnalysisLifecycle
{
    public static async Task<AnalysisRecord> StartAsync(AppDbContext db, AnalysisStartRequest request, CancellationToken ct)
    {
        ValidateStart(request);
        request.RepositoryUrl = RepositoryUrls.Normalize(request.CiProvider, request.RepositoryUrl);
        if (!await db.Set<Project>().AnyAsync(x => x.Key == request.ProjectKey, ct))
            throw new ApiException(404, "Project must be registered before publishing an analysis");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // ponytail: PostgreSQL advisory locking is process-independent; a theoretical 64-bit hash collision only serializes unrelated starts.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.ProjectKey + ":" + request.ProviderRunId}, 0))", ct);
        var existing = await db.Set<AnalysisRecord>().SingleOrDefaultAsync(x => x.ProjectKey == request.ProjectKey && x.ProviderRunId == request.ProviderRunId, ct);
        if (existing is not null)
        {
            SameContext(existing, request);
            await transaction.CommitAsync(ct);
            return existing;
        }

        var now = DateTime.UtcNow;
        var record = new AnalysisRecord
        {
            AnalysisId = Guid.NewGuid().ToString(),
            ProjectKey = request.ProjectKey,
            CommitSha = request.CommitSha,
            Branch = request.Branch,
            CiProvider = request.CiProvider,
            ProviderRunId = request.ProviderRunId,
            RepositoryUrl = request.RepositoryUrl ?? "",
            Status = "running",
            QualityGate = "pending",
            QualityGateSummary = GateEvaluation.Summary("pending", 0),
            StartedAt = now,
            CreatedAt = ApiJson.Iso(now),
            PrId = request.PrId ?? "",
            PrRepoId = request.PrRepoId ?? "",
            PrAdoProject = request.PrAdoProject ?? "",
            PrAdoOrgUrl = request.PrAdoOrgUrl ?? "",
            PrTargetBranch = request.PrTargetBranch ?? "",
            PrSourceSha = request.PrSourceSha ?? request.CommitSha,
            PrTargetSha = request.PrTargetSha ?? "",
            PrDecorationStatus = "queued",
            DecorationRevision = 1,
        };
        db.Add(record);
        QueueDecoration(db, record);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    public static async Task<AnalysisRecord> CompleteAsync(AppDbContext db, AnalysisRecord record, AnalysisRequest request, CancellationToken ct)
    {
        GateEvaluation.Validate(request);
        if (request.ProjectKey != record.ProjectKey || request.CommitSha != record.CommitSha || request.Branch != record.Branch || request.CiProvider != record.CiProvider)
            throw new ApiException(409, "Analysis context cannot change during completion");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ApiJson.Options)));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({record.AnalysisId}, 0))", ct);
        await db.Entry(record).ReloadAsync(ct);
        if (record.Status != "running")
        {
            if (record.Status == "completed" && record.ResultHash == hash) { await transaction.CommitAsync(ct); return record; }
            throw new ApiException(409, "A terminal analysis cannot accept a different result");
        }

        var project = await db.Set<Project>().SingleAsync(x => x.Key == record.ProjectKey, ct);
        var assignment = await db.Set<ProjectGateAssignment>().FindAsync([record.ProjectKey], ct);
        var gate = assignment is null ? null : await db.Set<QualityGateConfig>().FindAsync([assignment.GateId], ct);
        gate ??= await db.Set<QualityGateConfig>().FirstOrDefaultAsync(x => x.IsDefault, ct) ?? await db.Set<QualityGateConfig>().FindAsync(["default"], ct)
            ?? new QualityGateConfig { Id = "default", Name = "Default Gate", IsDefault = true };
        if (db.Entry(gate).State == EntityState.Detached) db.Add(gate);

        var metrics = GateEvaluation.BuildMetrics(request);
        var completeness = GateEvaluation.Completeness(request, project);
        var (verdict, failures, snapshot) = GateEvaluation.Evaluate(metrics, gate, request.Measurements, request.ProfileId, request.ProfileVersion, request.FormulaVersion);
        var technical = GateEvaluation.TechnicalState(completeness, request.ToolExecutions, request.Measurements);
        if (completeness != "complete" && verdict != "fail") verdict = "unknown";
        snapshot["completeness"] = completeness;
        snapshot["technical_state"] = technical;

        Apply(record, request, metrics, completeness, verdict, failures, snapshot, hash);
        var occurrences = AnalysisOccurrences.From(record).ToList();
        record.ErrorCount = occurrences.Count(x => x.Severity.ToLowerInvariant() is "blocker" or "critical" or "error" or "high");
        record.WarningCount = occurrences.Count(x => x.Severity.ToLowerInvariant() is "warning" or "medium");
        record.InfoCount = occurrences.Count - record.ErrorCount - record.WarningCount;
        record.QualityGateSummary = GateEvaluation.Summary(record.QualityGate, record.ErrorCount);

        db.AddRange(occurrences);
        await FindingCollaboration.ObserveAsync(db, record, occurrences, ct);
        record.DecorationRevision++;
        record.PrDecorationStatus = "queued";
        QueueDecoration(db, record);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    public static async Task<AnalysisRecord> FailAsync(AppDbContext db, AnalysisRecord record, string errorCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 100 || errorCode.Any(char.IsControl))
            throw new ApiException(422, "error_code must be a safe identifier of at most 100 characters");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({record.AnalysisId}, 0))", ct);
        await db.Entry(record).ReloadAsync(ct);
        if (record.Status != "running") { await transaction.CommitAsync(ct); return record; }
        record.Status = "failed";
        record.QualityGate = "unknown";
        record.QualityGateSummary = GateEvaluation.Summary("unknown", 0);
        record.FailureCode = errorCode;
        record.CompletedAt = DateTime.UtcNow;
        record.DecorationRevision++;
        record.PrDecorationStatus = "queued";
        QueueDecoration(db, record);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return record;
    }

    private static void Apply(AnalysisRecord record, AnalysisRequest request, AnalysisMetrics metrics, string completeness, string verdict, List<string> failures, Dictionary<string, object?> snapshot, string hash)
    {
        record.Status = "completed"; record.QualityGate = verdict; record.CompletedAt = DateTime.UtcNow; record.ResultHash = hash;
        record.MaintainabilityScore = metrics.MaintainabilityScore; record.SecurityScore = metrics.SecurityScore; record.ReliabilityScore = metrics.ReliabilityScore;
        record.CodeSmells = metrics.CodeSmells; record.Vulnerabilities = metrics.Vulnerabilities; record.SecurityHotspots = metrics.SecurityHotspots; record.Bugs = metrics.Bugs;
        record.Coverage = metrics.Coverage; record.Duplication = metrics.Duplication; record.DependencyVulnerabilities = metrics.DependencyVulnerabilities;
        record.Measurements = JsonSerializer.Serialize(request.Measurements, ApiJson.Options); record.ToolExecutions = JsonSerializer.Serialize(request.ToolExecutions, ApiJson.Options);
        record.Completeness = completeness; record.ProfileId = request.ProfileId; record.ProfileVersion = request.ProfileVersion; record.FormulaVersion = request.FormulaVersion;
        record.GateSnapshot = JsonSerializer.Serialize(snapshot, ApiJson.Options); record.AffectedFiles = JsonSerializer.Serialize(metrics.AffectedFiles ?? [], ApiJson.Options);
        record.GateFailures = JsonSerializer.Serialize(failures, ApiJson.Options);
    }

    private static void QueueDecoration(AppDbContext db, AnalysisRecord record) => db.Add(new DurableJob { Kind = "pr-decoration", ResourceId = $"{record.AnalysisId}:{record.DecorationRevision}" });

    public static string SafePath(string path)
    {
        var value = path.Replace('\\', '/').Trim();
        if (value.Length is 0 or > 1024 || value.StartsWith('/') || value.Contains(':') || value.Any(char.IsControl) || value.Split('/').Any(x => x is "" or "." or "..") || Path.IsPathRooted(value))
            throw new ApiException(422, "Source path must be a safe repository-relative path");
        return value;
    }

    private static void ValidateStart(AnalysisStartRequest request)
    {
        if (request.ProjectKey.Length < 2 || request.ProjectKey.Length > 255 || request.CommitSha.Length < 6 || request.CommitSha.Length > 255 || request.Branch.Length is 0 or > 255)
            throw new ApiException(422, "Invalid analysis identity");
        if (request.CiProvider is not ("azure-devops" or "github-actions" or "local")) throw new ApiException(422, "Invalid CI provider");
        if (string.IsNullOrWhiteSpace(request.ProviderRunId) || request.ProviderRunId.Length > 255) throw new ApiException(422, "provider_run_id is required");
    }

    private static void SameContext(AnalysisRecord record, AnalysisStartRequest request)
    {
        if (record.CommitSha != request.CommitSha || record.Branch != request.Branch || record.CiProvider != request.CiProvider || record.RepositoryUrl != (request.RepositoryUrl ?? "")
            || record.PrId != (request.PrId ?? "") || record.PrRepoId != (request.PrRepoId ?? "") || record.PrAdoProject != (request.PrAdoProject ?? "")
            || record.PrAdoOrgUrl != (request.PrAdoOrgUrl ?? "") || record.PrSourceSha != (request.PrSourceSha ?? request.CommitSha) || record.PrTargetSha != (request.PrTargetSha ?? ""))
            throw new ApiException(409, "provider_run_id already identifies another analysis context");
    }
}
