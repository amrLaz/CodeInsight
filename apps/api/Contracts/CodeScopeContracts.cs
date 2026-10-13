namespace CodeInsights.Api.Contracts;

public sealed partial record IngestionManifest
{
    public string SourceRoot { get; init; } = "";
    public string CodeScopeVersion { get; init; } = "";
    public string[] RepositoryFiles { get; init; } = [];
}

public sealed record CodeScopeDefinition
{
    public string Version { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public string Component { get; init; } = "";
    public string MainBranch { get; init; } = "refs/heads/main";
    public string ReferenceKind { get; init; } = "branch";
    public string ReferenceBranch { get; init; } = "refs/heads/main";
    public string? ReferenceAnalysisId { get; init; }
    public string[] IncludedPaths { get; init; } = [];
    public string[] ExcludedPaths { get; init; } = [];
    public string[] TestPaths { get; init; } = [];
    public string[] GeneratedPaths { get; init; } = [];
    public string[] ThirdPartyPaths { get; init; } = [];
}

public sealed record CodeScopeSnapshot(CodeScopeDefinition? Definition, string? BaselineSha, string? BaselineAnalysisId, string? Reason);
public sealed record CodeScopeAssignment(string Version);
public sealed record BranchArchiveRequest(string Branch, bool Archived);
