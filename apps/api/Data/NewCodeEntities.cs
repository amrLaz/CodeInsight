using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

public sealed class NewCodeDefinition
{
    public string ScopeKey { get; set; } = "global";
    public string Level { get; set; } = "global";
    public string? ProjectKey { get; set; }
    public string? Branch { get; set; }
    public string Mode { get; set; } = "previous_version";
    public int? NumberOfDays { get; set; }
    public string? ReferenceBranch { get; set; }
    public string? AnalysisId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class NewCodeModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<NewCodeDefinition>().ToTable("new_code_definitions").HasKey(x => x.ScopeKey);
        model.Entity<NewCodeDefinition>().HasIndex(x => new { x.ProjectKey, x.Branch });
        foreach (var property in model.Entity<NewCodeDefinition>().Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
