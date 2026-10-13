using System.Text.Json.Serialization;

namespace CodeInsights.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewCodeDefinitionRequest
{
    public string Level { get; init; } = "";
    public string? ProjectKey { get; init; }
    public string? Branch { get; init; }
    public string Mode { get; init; } = "";
    public int? NumberOfDays { get; init; }
    public string? ReferenceBranch { get; init; }
    public string? AnalysisId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NewCodeResolveRequest
{
    public string ProjectKey { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public string Branch { get; init; } = "";
    public string? ProjectVersion { get; init; }
    public string? PrTargetBranch { get; init; }
    public string? NewCodeReferenceBranch { get; init; }
    public string CiProvider { get; init; } = "azure-devops";
    public string ProfileId { get; init; } = "";
    public string ProfileVersion { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MainBranchRequest(string MainBranch);

public sealed record EffectiveNewCodeDefinition(
    string Mode,
    int? NumberOfDays,
    string? ReferenceBranch,
    string? AnalysisId
);

public sealed record NewCodeResolution(
    EffectiveNewCodeDefinition EffectiveDefinition,
    string EffectiveSource,
    string? BaselineIngestionId,
    string? BaselineAnalysisId,
    string? BaselineSha,
    string? BaselineBranch,
    string? BaselineVersion,
    DateTime? BaselineDate,
    string? Reason
);
