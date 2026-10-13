using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

/// <summary>
/// State of one project's main branch at the end of one UTC day. A null value means "not measured that
/// day": aggregates skip it instead of reading it as zero.
/// </summary>
public sealed class PortfolioSnapshot
{
    public string ProjectKey { get; set; } = "";
    public DateOnly Day { get; set; }
    /// <summary>Latest completed main-branch analysis created on or before the end of the day.</summary>
    public string AnalysisId { get; set; } = "";
    public string? SecurityRating { get; set; }
    public string? ReliabilityRating { get; set; }
    public string? MaintainabilityRating { get; set; }
    public string? SecurityReviewRating { get; set; }
    public int? Coverage { get; set; }
    public int? Duplication { get; set; }
    /// <summary>Findings of that analysis by severity; all null once its details were purged.</summary>
    public int? BlockerFindings { get; set; }
    public int? HighFindings { get; set; }
    public int? MediumFindings { get; set; }
    public int? LowFindings { get; set; }
    public int? InfoFindings { get; set; }
    public int? UnknownSeverityFindings { get; set; }
    /// <summary>Only known for days observed live: past SLA states were never recorded.</summary>
    public int? SlaBreached { get; set; }
    public int? SlaDueSoon { get; set; }
    /// <summary>live when computed on the day itself, reconstructed when rebuilt from stored analyses.</summary>
    public string Source { get; set; } = "live";
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

public static class PortfolioModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<PortfolioSnapshot>().ToTable("portfolio_snapshots").HasKey(x => new { x.ProjectKey, x.Day });
        model.Entity<PortfolioSnapshot>().HasIndex(x => x.Day);
        foreach (var property in model.Entity<PortfolioSnapshot>().Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
