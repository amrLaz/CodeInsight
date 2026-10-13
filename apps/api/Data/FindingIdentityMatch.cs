using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

/// <summary>Durable, branch-scoped evidence for a finding correspondence; retained independently of occurrences.</summary>
public sealed class FindingIdentityMatch
{
    public string Id { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string Branch { get; set; } = "";
    public string AnalysisId { get; set; } = "";
    public string IngestionId { get; set; } = "";
    public string OccurrenceIdentity { get; set; } = "";
    public string? FindingId { get; set; }
    public string Method { get; set; } = "indeterminate";
    public string BaseSha { get; set; } = "";
    public string HeadSha { get; set; } = "";
    public string? OldPath { get; set; }
    public int? OldLine { get; set; }
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string EvidenceHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class FindingIdentityModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<FindingIdentityMatch>().ToTable("finding_identity_matches").HasKey(x => x.Id);
        model.Entity<FindingIdentityMatch>().HasIndex(x => new { x.ProjectKey, x.Branch, x.OccurrenceIdentity, x.CreatedAt });
        model.Entity<FindingIdentityMatch>().HasIndex(x => new { x.FindingId, x.CreatedAt });
        foreach (var property in model.Entity<FindingIdentityMatch>().Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
