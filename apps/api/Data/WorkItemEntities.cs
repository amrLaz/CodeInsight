using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

/// <summary>
/// The Azure Boards work item created for one durable finding. Unlike pull-request projections, a
/// work item is a durable external object that people edit: its identity must be kept to avoid a
/// duplicate and to comment on it later.
/// </summary>
public sealed class FindingWorkItem
{
    public string FindingId { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string OrganizationUrl { get; set; } = "";
    public string AdoProject { get; set; } = "";
    public int? WorkItemId { get; set; }
    public string? Url { get; set; }
    /// <summary>pending, created or failed.</summary>
    public string State { get; set; } = "pending";
    /// <summary>manual, severity_rule or sla_rule.</summary>
    public string Origin { get; set; } = "manual";
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CreatedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Per-project work item fields and automatic creation rules; defaults apply without a row.</summary>
public sealed class WorkItemSettings
{
    public const string DefaultType = "Bug";
    public const int DefaultMaxPerRun = 10;

    public string ProjectKey { get; set; } = "";
    public string WorkItemType { get; set; } = DefaultType;
    public string? AreaPath { get; set; }
    public string? IterationPath { get; set; }
    /// <summary>Lowest severity that creates a work item automatically; null disables the rule.</summary>
    public string? AutoMinSeverity { get; set; }
    public bool AutoOnSlaBreach { get; set; }
    public int AutoMaxPerRun { get; set; } = DefaultMaxPerRun;
    /// <summary>Rules only consider findings detected, or breaches due, after they were enabled.</summary>
    public DateTime? AutoSince { get; set; }
    public int Version { get; set; } = 1;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class WorkItemModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<FindingWorkItem>().ToTable("finding_work_items").HasKey(x => x.FindingId);
        model.Entity<FindingWorkItem>().HasIndex(x => new { x.ProjectKey, x.State });
        model.Entity<WorkItemSettings>().ToTable("work_item_settings").HasKey(x => x.ProjectKey);
        foreach (var type in new[] { typeof(FindingWorkItem), typeof(WorkItemSettings) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
