using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeInsights.Api.Data;

public sealed class IngestionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AnalysisId { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string BuildKey { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string Manifest { get; set; } = "{}";
    public string ProfileSnapshot { get; set; } = "{}";
    public string ExceptionSnapshot { get; set; } = "[]";
    public int Revision { get; set; }
    public string State { get; set; } = "receiving";
    public string TechnicalState { get; set; } = "pending";
    public string QualityGate { get; set; } = "unknown";
    public string Error { get; set; } = "";
    public string GateSnapshot { get; set; } = "{}";
    public string Comparison { get; set; } = "{\"state\":\"unavailable\",\"reason\":\"baseline_absent\"}";
    public string CodeData { get; set; } = "{}";
    public string CodeScopeSnapshot { get; set; } = "{}";
    public string NewCodeSnapshot { get; set; } = "{}";
    public string SecurityReviewSnapshot { get; set; } = "[]";
    /// <summary>Licence lists frozen at creation; null for ingestions created before the organization policy.</summary>
    public string? LicensePolicySnapshot { get; set; }
    public bool DetailsPurged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class IngestionReport
{
    public string IngestionId { get; set; } = "";
    public string ReportId { get; set; } = "";
    public string BlobName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string State { get; set; } = "expected";
    public string Error { get; set; } = "";
}

public sealed class AnalysisSource
{
    public string SourceId { get; set; } = Guid.NewGuid().ToString();
    public string AnalysisId { get; set; } = "";
    public string Path { get; set; } = "";
    public string BlobName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string State { get; set; } = "expected";
}

public sealed class DurableJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = "ingest";
    public string ResourceId { get; set; } = "";
    public string State { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseOwner { get; set; }
    public string Error { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FindingOccurrence
{
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? ComparisonState { get; set; }
    public long Id { get; set; }
    public string? FindingId { get; set; }
    public string IngestionId { get; set; } = "";
    public string AnalysisId { get; set; } = "";
    public string ReportId { get; set; } = "";
    public string Tool { get; set; } = "";
    public string OriginalTool { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string OriginalSeverity { get; set; } = "";
    public string Category { get; set; } = "unknown";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string? Fingerprint { get; set; }
    public bool Excepted { get; set; }
    public string Evidence { get; set; } = "{}";
}

public sealed class CoverageLine
{
    public string IngestionId { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public bool Covered { get; set; }
}

public sealed class SbomComponent
{
    public long Id { get; set; }
    public string IngestionId { get; set; } = "";
    public string AnalysisId { get; set; } = "";
    public string ReportId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Purl { get; set; }
    public string? Ecosystem { get; set; }
    public string Licenses { get; set; } = "[]";
    public string Relationship { get; set; } = "unknown";
    public string LicenseDecision { get; set; } = "review";
    public string Vulnerabilities { get; set; } = "[]";
    public string Dependencies { get; set; } = "[]";
    public string Source { get; set; } = "";
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ProfileVersion
{
    public string ProfileId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Definition { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class IngestionProjectPolicy
{
    public string ProjectKey { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ProfileVersion { get; set; } = "";
    public string Enforcement { get; set; } = "informative";
}

public sealed class ProfileException
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectKey { get; set; } = "";
    public string Tool { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string? Path { get; set; }
    public string Reason { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class IngestionModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<IngestionRecord>().ToTable("ingestions").HasKey(x => x.Id);
        model.Entity<IngestionRecord>().HasIndex(x => new { x.BuildKey, x.ManifestHash }).IsUnique();
        model.Entity<IngestionRecord>().HasIndex(x => new { x.AnalysisId, x.Revision }).IsUnique();
        model.Entity<IngestionRecord>().HasIndex(x => new { x.ProjectKey, x.CreatedAt });
        model.Entity<IngestionReport>().ToTable("ingestion_reports").HasKey(x => new { x.IngestionId, x.ReportId });
        model.Entity<IngestionReport>().HasOne<IngestionRecord>().WithMany().HasForeignKey(x => x.IngestionId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AnalysisSource>().ToTable("analysis_sources").HasKey(x => x.SourceId);
        model.Entity<AnalysisSource>().HasIndex(x => new { x.AnalysisId, x.Path }).IsUnique();
        model.Entity<AnalysisSource>().HasOne<AnalysisRecord>().WithMany().HasForeignKey(x => x.AnalysisId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DurableJob>().ToTable("durable_jobs").HasKey(x => x.Id);
        model.Entity<DurableJob>().HasIndex(x => new { x.Kind, x.ResourceId }).IsUnique();
        model.Entity<DurableJob>().HasIndex(x => new { x.State, x.AvailableAt });
        model.Entity<FindingOccurrence>().ToTable("finding_occurrences").HasKey(x => x.Id);
        model.Entity<FindingOccurrence>().HasIndex(x => new { x.IngestionId, x.Id });
        model.Entity<FindingOccurrence>().HasIndex(x => new { x.AnalysisId, x.Tool, x.RuleId });
        model.Entity<CoverageLine>().ToTable("coverage_lines").HasKey(x => new { x.IngestionId, x.Path, x.Line });
        model.Entity<SbomComponent>().ToTable("sbom_components").HasKey(x => x.Id);
        model.Entity<SbomComponent>().HasIndex(x => new { x.IngestionId, x.Id });
        model.Entity<ProfileVersion>().ToTable("profile_versions").HasKey(x => new { x.ProfileId, x.Version });
        model.Entity<IngestionProjectPolicy>().ToTable("ingestion_project_policies").HasKey(x => x.ProjectKey);
        model.Entity<ProfileException>().ToTable("profile_exceptions").HasKey(x => x.Id);
        model.Entity<ProfileException>().HasIndex(x => new { x.ProjectKey, x.ExpiresAt });
        Type[] types = [typeof(IngestionRecord), typeof(IngestionReport), typeof(AnalysisSource), typeof(DurableJob), typeof(FindingOccurrence), typeof(CoverageLine), typeof(SbomComponent), typeof(ProfileVersion), typeof(IngestionProjectPolicy), typeof(ProfileException)];
        foreach (var type in types)
        foreach (var property in model.Entity(type).Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
