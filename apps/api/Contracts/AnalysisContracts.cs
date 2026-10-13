using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeInsights.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ToolExecution(
    [property: JsonRequired] string Tool,
    [property: JsonRequired] string Status,
    string? Version = null,
    string? ErrorCode = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MeasurementValue(
    [property: JsonRequired] string State,
    int? Value = null,
    string? Tool = null,
    string? ToolVersion = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AnalysisRequest
{
    [JsonRequired]
    public string ProjectKey { get; set; } = "";

    [JsonRequired]
    public string CommitSha { get; set; } = "";

    [JsonRequired]
    public string Branch { get; set; } = "";

    [JsonRequired]
    public string CiProvider { get; set; } = "local";

    [JsonRequired]
    public string ProfileId { get; set; } = "";

    [JsonRequired]
    public string ProfileVersion { get; set; } = "";

    [JsonRequired]
    public string FormulaVersion { get; set; } = "";

    [JsonRequired]
    public Dictionary<string, MeasurementValue> Measurements { get; set; } = [];

    [JsonRequired]
    public List<ToolExecution> ToolExecutions { get; set; } = [];
    public Dictionary<string, JsonElement>? AffectedFiles { get; set; }
    public string? PrId { get; set; }
    public string? PrRepoId { get; set; }
    public string? PrAdoProject { get; set; }
    public string? PrTargetBranch { get; set; }
    public string? PrAdoOrgUrl { get; set; }
    public string? PlatformUrl { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? ProviderRunId { get; set; }
    public string? PrSourceSha { get; set; }
    public string? PrTargetSha { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AnalysisStartRequest
{
    [JsonRequired] public string ProjectKey { get; set; } = "";
    [JsonRequired] public string CommitSha { get; set; } = "";
    [JsonRequired] public string Branch { get; set; } = "";
    [JsonRequired] public string CiProvider { get; set; } = "local";
    [JsonRequired] public string ProviderRunId { get; set; } = "";
    public string? RepositoryUrl { get; set; }
    public string? PrId { get; set; }
    public string? PrRepoId { get; set; }
    public string? PrAdoProject { get; set; }
    public string? PrAdoOrgUrl { get; set; }
    public string? PrTargetBranch { get; set; }
    public string? PrSourceSha { get; set; }
    public string? PrTargetSha { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisFailureRequest([property: JsonRequired] string ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SourceDeclaration([property: JsonRequired] string Path, [property: JsonRequired] long SizeBytes, [property: JsonRequired] string Sha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalysisSourcesRequest([property: JsonRequired] List<SourceDeclaration> Sources);

public sealed record QualityGateResult(string Status, string Summary, int ErrorCount, int WarningCount, int InfoCount);

public sealed class AnalysisMetrics
{
    public int? MaintainabilityScore { get; set; }
    public int? SecurityScore { get; set; }
    public int? ReliabilityScore { get; set; }
    public string? SecurityRating { get; set; }
    public string? ReliabilityRating { get; set; }
    public string? MaintainabilityRating { get; set; }
    public string? SecurityReviewRating { get; set; }
    public int? CodeSmells { get; set; }
    public int? Vulnerabilities { get; set; }
    public int? SecurityHotspots { get; set; }
    public int? Bugs { get; set; }
    public int? Duplication { get; set; }
    public int? Coverage { get; set; }
    public int? DependencyVulnerabilities { get; set; }
    public Dictionary<string, JsonElement>? AffectedFiles { get; set; }

    public int? Get(string name) =>
        name switch
        {
            "maintainability_score" => MaintainabilityScore,
            "security_score" => SecurityScore,
            "reliability_score" => ReliabilityScore,
            "code_smells" => CodeSmells,
            "vulnerabilities" => Vulnerabilities,
            "security_hotspots" => SecurityHotspots,
            "bugs" => Bugs,
            "coverage" => Coverage,
            "duplication" => Duplication,
            "dependency_vulnerabilities" => DependencyVulnerabilities,
            _ => null,
        };

    public void Set(string name, int? value)
    {
        switch (name)
        {
            case "maintainability_score":
                MaintainabilityScore = value;
                break;
            case "security_score":
                SecurityScore = value;
                break;
            case "reliability_score":
                ReliabilityScore = value;
                break;
            case "code_smells":
                CodeSmells = value;
                break;
            case "vulnerabilities":
                Vulnerabilities = value;
                break;
            case "security_hotspots":
                SecurityHotspots = value;
                break;
            case "bugs":
                Bugs = value;
                break;
            case "coverage":
                Coverage = value;
                break;
            case "duplication":
                Duplication = value;
                break;
            case "dependency_vulnerabilities":
                DependencyVulnerabilities = value;
                break;
        }
    }
}

public sealed record AnalysisResponse(
    string AnalysisId,
    string ProjectKey,
    string CommitSha,
    string Branch,
    string CiProvider,
    string Status,
    string QualityGate,
    AnalysisMetrics Metrics,
    List<string> GateFailures,
    string CreatedAt,
    string? PrId,
    string? PrDecorationStatus,
    string ProfileId,
    string ProfileVersion,
    string FormulaVersion,
    List<ToolExecution> ToolExecutions,
    Dictionary<string, MeasurementValue> Measurements,
    string Completeness,
    string TechnicalState,
    Dictionary<string, JsonElement> GateSnapshot,
    QualityGateResult QualityGateResult,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? ProviderRunId
)
{
    /// <summary>Branch a pull request analysis merges into; null for a branch build or when it was not recorded.</summary>
    public string? PrTargetBranch { get; init; }
}
