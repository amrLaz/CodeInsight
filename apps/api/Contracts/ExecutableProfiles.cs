using System.Text.Json;

namespace CodeInsights.Api.Contracts;

public sealed partial record ProfileDefinition
{
    public CatalogueRule[] RuleCatalog { get; init; } = [];
    public AnalyzerConfiguration[] AnalyzerConfigurations { get; init; } = [];
    public DisabledRule[] DisabledRules { get; init; } = [];
}

public sealed partial record IngestionManifest
{
    public EffectiveConfiguration[] EffectiveConfigurations { get; init; } = [];
}

public sealed record CatalogueRule(string Tool, string ToolVersion, string RuleId, string Language, string Category,
    JsonElement Parameters, string? DocumentationUrl = null, string? DocumentationVersion = null, RemediationModel? Remediation = null,
    string? Severity = null);
public sealed record DisabledRule(string Tool, string ToolVersion, string RuleId);
public sealed record RemediationModel(string Model, int DurationSeconds, string DocumentationUrl);
public sealed record AnalyzerConfiguration(string Tool, string ToolVersion, string Format, string Path, string Content);
public sealed record EffectiveConfiguration(string Tool, string ToolVersion, string Path, string Sha256);
public sealed record ExportedConfiguration(string Tool, string ToolVersion, string Format, string Path, string Content, string Sha256);
public sealed record ConfigurationExport(string ProfileId, string ProfileVersion, string ConfigurationHash, ExportedConfiguration[] Files);
public sealed record ConfigurationEvaluation(string State, string ExpectedHash, string EffectiveHash, string? Reason);
