namespace CodeInsights.Api.Data;

public sealed class BootstrapOptions
{
    public string AdminEmail { get; set; } = "admin@codeinsights.local";
    public string AdminPassword { get; set; } = "";
    public bool RequireCompleteMappings { get; set; }
}
