using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeInsights.Api.Data;

public sealed class CodeScopeVersion
{
    public string ProjectKey { get; set; } = "";
    public string Version { get; set; } = "";
    public string Definition { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class CodeScopeAssignment
{
    public string ProjectKey { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class ArchivedCodeBranch
{
    public string ProjectKey { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool Archived { get; set; }
}

public static class CodeScopeModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CodeScopeVersion>().ToTable("code_scope_versions").HasKey(x => new { x.ProjectKey, x.Version });
        model.Entity<CodeScopeAssignment>().ToTable("code_scope_assignments").HasKey(x => x.ProjectKey);
        model.Entity<ArchivedCodeBranch>().ToTable("archived_code_branches").HasKey(x => new { x.ProjectKey, x.Branch });
        foreach (var type in new[] { typeof(CodeScopeVersion), typeof(CodeScopeAssignment), typeof(ArchivedCodeBranch) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
