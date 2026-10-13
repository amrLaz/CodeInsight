namespace CodeInsights.Api.Data;

public sealed class Project
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; } = null;
    public bool Archived { get; set; } = false;
    public string? OrganizationId { get; set; } = null;
    public string? AnalysisProfileId { get; set; } = null;
    public string? AnalysisProfileVersion { get; set; } = null;
    public string? MainBranch { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Branch
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AnalysisRecord
{
    public bool DetailsPurged { get; set; }
    public string AnalysisId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string CiProvider { get; set; } = string.Empty;
    public string ProviderRunId { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string QualityGate { get; set; } = "unknown";
    public int? MaintainabilityScore { get; set; } = null;
    public int? SecurityScore { get; set; } = null;
    public int? ReliabilityScore { get; set; } = null;
    public string? SecurityRating { get; set; }
    public string? ReliabilityRating { get; set; }
    public string? MaintainabilityRating { get; set; }
    public string? SecurityReviewRating { get; set; }
    public int? CodeSmells { get; set; } = null;
    public int? Vulnerabilities { get; set; } = null;
    public int? SecurityHotspots { get; set; } = null;
    public int? Bugs { get; set; } = null;
    public int? Coverage { get; set; } = null;
    public int? Duplication { get; set; } = null;
    public int? DependencyVulnerabilities { get; set; } = null;
    public string Measurements { get; set; } = "{}";
    public string ToolExecutions { get; set; } = "[]";
    public string Completeness { get; set; } = "incomplete";
    public string ProfileId { get; set; } = "";
    public string ProfileVersion { get; set; } = "0";
    public string FormulaVersion { get; set; } = "0";
    public string GateSnapshot { get; set; } = "{}";
    public string AffectedFiles { get; set; } = "{}";
    public string GateFailures { get; set; } = "[]";
    public string? PrId { get; set; } = "";
    public string? PrRepoId { get; set; } = "";
    public string? PrAdoProject { get; set; } = "";
    public string? PrTargetBranch { get; set; } = "";
    public string? PrAdoOrgUrl { get; set; } = "";
    public string? PrSourceSha { get; set; } = "";
    public string? PrTargetSha { get; set; } = "";
    public int DecorationRevision { get; set; }
    public long? GitHubCheckRunId { get; set; }
    public string ResultHash { get; set; } = string.Empty;
    public string FailureCode { get; set; } = string.Empty;
    public string QualityGateSummary { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public string? PrDecorationStatus { get; set; } = "";
    public string? PrDecorationError { get; set; } = "";
    public string CreatedAt { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class RetentionPolicy
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int KeepAnalysesDays { get; set; } = 90;
    public int KeepFailedAnalysesDays { get; set; } = 30;
    public int KeepMasterBranchDays { get; set; } = 365;
    public bool AutoDeleteEnabled { get; set; } = true;
    public DateTime? LastCleanupAt { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class QualityGateConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Default Gate";
    public bool IsDefault { get; set; } = false;
    public int MinMaintainabilityScore { get; set; } = 50;
    public int MinSecurityScore { get; set; } = 50;
    public int MinReliabilityScore { get; set; } = 50;
    public int MaxCodeSmells { get; set; } = 100;
    public int MaxVulnerabilities { get; set; } = 10;
    public int MaxSecurityHotspots { get; set; } = 10;
    public int MaxBugs { get; set; } = 10;
}

public sealed class ProjectGateAssignment
{
    public string ProjectKey { get; set; } = string.Empty;
    public string GateId { get; set; } = string.Empty;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AzureDevOpsOrganizationConfig
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigName { get; set; } = string.Empty;
    public string OrganizationUrl { get; set; } = string.Empty;
    public string Pat { get; set; } = string.Empty;
    public bool QualityGateEnabled { get; set; } = false;
    public bool WorkItemsEnabled { get; set; } = false;
    public bool RepoImportEnabled { get; set; } = false;
    public string Status { get; set; } = "ready";
    public DateTime? LastTestedAt { get; set; } = null;
    public string? LastError { get; set; } = null;
    public string? BotIdentityId { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuthProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Config { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
    public string Category { get; set; } = "DevOps Platform";
    public string ConnectionUrl { get; set; } = "";
    public string Status { get; set; } = "ready";
    public DateTime? LastTestedAt { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class PlatformApiToken
{
    public string TokenId { get; set; } = string.Empty;
    public string TokenName { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPreview { get; set; } = string.Empty;
    public bool IsCurrent { get; set; } = true;
    public bool Revoked { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; } = null;
    public string Scopes { get; set; } = "[]";

    /// <summary>Owner of a personal access token; null for CI tokens issued by an administrator.</summary>
    public string? OwnerUserId { get; set; } = null;
}

public sealed class ProjectIntegration
{
    public string Id { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public string RepoProvider { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string PipelineProvider { get; set; } = string.Empty;
    public string PipelineUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RuleSuppression
{
    public string Id { get; set; } = string.Empty;
    public string RuleKey { get; set; } = string.Empty;
    public DateTime SuppressedAt { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; } = null;
}

public sealed class PlatformUser
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = "default";
    public string? HashedPassword { get; set; } = null;
    public string AuthProvider { get; set; } = "local";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; } = null;
    public string? EntraTenantId { get; set; } = null;
    public string? EntraObjectId { get; set; } = null;
}

public sealed class Organization
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class UserGrant
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class EntraAccessGroup
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "default";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class EntraGroupGrant
{
    public string Id { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
}

public sealed class UserProjectFavorite
{
    public string UserId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TokenProjectAccess
{
    public string TokenId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class OidcTransaction
{
    public string StateHash { get; set; } = string.Empty;
    public string CookieHash { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow;
    public DateTime? UsedAt { get; set; } = null;
    public bool IsSilent { get; set; }
}

public sealed class PlatformSetting
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class NotificationChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ChannelType { get; set; } = string.Empty;
    public string Config { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public bool OnFail { get; set; } = true;
    public bool OnPass { get; set; } = false;
    public bool OnSlaBreach { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuditLog
{
    public string Id { get; set; } = string.Empty;
    public string ActorEmail { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? ResourceId { get; set; } = null;
    public string? ResourceLabel { get; set; } = null;
    public string? Details { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

