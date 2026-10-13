namespace CodeInsights.Api.Services;

/// <summary>Analysis settings bound from the Analysis configuration section.</summary>
public sealed class AnalysisOptions
{
    public string Enforcement { get; set; } = "informative";
}
