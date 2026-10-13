using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

public sealed class CollaborationTeam
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OwnerUserId { get; set; }
}

public sealed class ProjectOwnership
{
    public string ProjectKey { get; set; } = "";
    public string? TeamId { get; set; }
    public string? OwnerUserId { get; set; }
}

public sealed class DurableFinding
{
    public string Id { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Tool { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "unknown";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
    public int? Line { get; set; }
    public string IdentityKind { get; set; } = "exact_location";
    public string State { get; set; } = "open";
    public string HotspotReview { get; set; } = "not_reviewed";
    public string? AssigneeUserId { get; set; }
    public string? TeamId { get; set; }
    public string? ExceptionReason { get; set; }
    public DateTime? ExceptionExpiresAt { get; set; }
    public string LastAnalysisId { get; set; } = "";
    public DateTime LastSeenAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
    /// <summary>Normalized bucket of the latest occurrence: blocker, high, medium, low, info or unknown.</summary>
    public string Severity { get; set; } = "unknown";
    /// <summary>Creation date of the earliest analysis proven to contain the finding; null when retention hid it.</summary>
    public DateTime? FirstDetectedAt { get; set; }
    /// <summary>Why a deadline applies or not; <see cref="Services.FindingSla.Pending"/> until the maintenance computes it.</summary>
    public string SlaBasis { get; set; } = Services.FindingSla.Pending;
    public DateTime? SlaDueAt { get; set; }
    public DateTime? SlaWarnAt { get; set; }
    /// <summary>Run stamp of the breach notification that included this finding.</summary>
    public DateTime? SlaNotifiedAt { get; set; }
}

/// <summary>Remediation delays in days per severity; null means that severity has no deadline.</summary>
public sealed class SlaPolicy
{
    public string ScopeKey { get; set; } = "global";
    public string? ProjectKey { get; set; }
    public int? BlockerDays { get; set; }
    public int? HighDays { get; set; }
    public int? MediumDays { get; set; }
    public int? LowDays { get; set; }
    public int? InfoDays { get; set; }
    public int Version { get; set; } = 1;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FindingDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FindingId { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string ActorUserId { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Snapshot { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class CollaborationModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CollaborationTeam>().ToTable("collaboration_teams").HasKey(x => x.Id);
        model.Entity<CollaborationTeam>().HasIndex(x => new { x.ProjectKey, x.Name }).IsUnique();
        model.Entity<ProjectOwnership>().ToTable("project_ownerships").HasKey(x => x.ProjectKey);
        model.Entity<DurableFinding>().ToTable("durable_findings").HasKey(x => x.Id);
        model.Entity<DurableFinding>().HasIndex(x => new { x.ProjectKey, x.Branch, x.Id });
        model.Entity<DurableFinding>().HasIndex(x => new { x.AssigneeUserId, x.TeamId });
        model.Entity<DurableFinding>().HasIndex(x => new { x.SlaBasis, x.SlaDueAt });
        model.Entity<DurableFinding>().Property(x => x.Severity).HasDefaultValue("unknown");
        model.Entity<DurableFinding>().Property(x => x.SlaBasis).HasDefaultValue(Services.FindingSla.Pending);
        model.Entity<FindingDecision>().ToTable("finding_decisions").HasKey(x => x.Id);
        model.Entity<FindingDecision>().HasIndex(x => new { x.FindingId, x.CreatedAt, x.Id });
        model.Entity<SlaPolicy>().ToTable("sla_policies").HasKey(x => x.ScopeKey);
        model.Entity<SlaPolicy>().HasIndex(x => x.ProjectKey).IsUnique();
        model.Entity<SlaPolicy>().HasData(new SlaPolicy
        {
            ScopeKey = "global", BlockerDays = 7, HighDays = 30, MediumDays = 90, LowDays = 180,
            UpdatedBy = "migration", UpdatedAt = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        // Decisions deliberately have no cascade to occurrences or analyses: retention cannot erase a review.
        foreach (var type in new[] { typeof(CollaborationTeam), typeof(ProjectOwnership), typeof(DurableFinding), typeof(FindingDecision), typeof(SlaPolicy) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
