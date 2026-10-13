using System.Text.Json.Serialization;

namespace CodeInsights.VisualStudio.Host;

internal sealed class HostSettingsDto
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("projectKey")]
    public string ProjectKey { get; set; } = "";

    [JsonPropertyName("refreshIntervalSeconds")]
    public int RefreshIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("diagnosticsEnabled")]
    public bool DiagnosticsEnabled { get; set; } = true;

    [JsonPropertyName("liveAnalysisEnabled")]
    public bool LiveAnalysisEnabled { get; set; }

    [JsonPropertyName("liveAnalysisTargetBranch")]
    public string LiveAnalysisTargetBranch { get; set; } = "origin/master";

    [JsonPropertyName("liveAnalysisSemgrepConfig")]
    public string[] LiveAnalysisSemgrepConfig { get; set; } = [".codeinsights/semgrep-mqr-v1.json"];

    [JsonPropertyName("liveAnalysisUseDocker")]
    public bool LiveAnalysisUseDocker { get; set; }

    [JsonPropertyName("liveAnalysisImages")]
    public HostImagesDto LiveAnalysisImages { get; set; } = new();

    [JsonPropertyName("liveAnalysisTimeoutSeconds")]
    public int LiveAnalysisTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("liveAnalysisMaxFiles")]
    public int LiveAnalysisMaxFiles { get; set; } = 100;

    [JsonPropertyName("coverageEnabled")]
    public bool CoverageEnabled { get; set; } = true;

    [JsonPropertyName("coverageLocalLcov")]
    public string CoverageLocalLcov { get; set; } = "";

    [JsonPropertyName("duplicationEnabled")]
    public bool DuplicationEnabled { get; set; } = true;

    [JsonPropertyName("dependenciesEnabled")]
    public bool DependenciesEnabled { get; set; } = true;
}

internal sealed class HostImagesDto
{
    [JsonPropertyName("semgrep")]
    public string Semgrep { get; set; } = "semgrep/semgrep:1.145.0";

    [JsonPropertyName("gitleaks")]
    public string Gitleaks { get; set; } = "zricethezav/gitleaks:v8.30.0";
}

internal sealed class InitializeRequest
{
    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("cacheDir")]
    public string CacheDir { get; set; } = "";

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("settings")]
    public HostSettingsDto Settings { get; set; } = new();
}

internal sealed class Snapshot
{
    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("signedIn")]
    public bool SignedIn { get; set; }

    [JsonPropertyName("report")]
    public ReportState Report { get; set; } = new();

    [JsonPropertyName("reportNodes")]
    public List<ReportNodeDto> ReportNodes { get; set; } = [];

    [JsonPropertyName("issues")]
    public IssuesState Issues { get; set; } = new();

    [JsonPropertyName("issuesDescription")]
    public string IssuesDescription { get; set; } = "";

    [JsonPropertyName("rules")]
    public RulesState Rules { get; set; } = new();

    [JsonPropertyName("rulesDescription")]
    public string RulesDescription { get; set; } = "";

    [JsonPropertyName("details")]
    public DetailsDto? Details { get; set; }

    [JsonPropertyName("status")]
    public StatusDto Status { get; set; } = new();

    [JsonPropertyName("notice")]
    public string? Notice { get; set; }
}

internal sealed class ReportState
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

internal sealed class ReportNodeDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("children")]
    public List<ReportNodeDto>? Children { get; set; }

    [JsonPropertyName("open")]
    public OpenTarget? Open { get; set; }
}

internal sealed class OpenTarget
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }
}

internal sealed class IssuesState
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("groups")]
    public List<IssueGroupDto>? Groups { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

internal sealed class IssueGroupDto
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("worst")]
    public string Worst { get; set; } = "";

    [JsonPropertyName("issues")]
    public List<IssueRowDto> Issues { get; set; } = [];
}

internal sealed class IssueRowDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("excepted")]
    public bool Excepted { get; set; }

    [JsonPropertyName("documentationUrl")]
    public string? DocumentationUrl { get; set; }

    [JsonPropertyName("locationsSuffix")]
    public string LocationsSuffix { get; set; } = "";

    [JsonPropertyName("locations")]
    public List<LocationDto> Locations { get; set; } = [];
}

internal sealed class LocationDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

internal sealed class RulesState
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "message";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("groups")]
    public List<RuleGroupDto>? Groups { get; set; }
}

internal sealed class RuleGroupDto
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("rules")]
    public List<RuleRowDto> Rules { get; set; } = [];
}

internal sealed class RuleRowDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("occurrences")]
    public int Occurrences { get; set; }
}

internal sealed class DetailsDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("rows")]
    public List<DetailsRowDto> Rows { get; set; } = [];

    [JsonPropertyName("locations")]
    public List<string> Locations { get; set; } = [];

    [JsonPropertyName("documentationUrl")]
    public string? DocumentationUrl { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("descriptionNotice")]
    public string DescriptionNotice { get; set; } = "";
}

internal sealed class DetailsRowDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class StatusDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }
}

internal sealed class FileContextDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("diagnostics")]
    public List<FileDiagnosticDto> Diagnostics { get; set; } = [];

    [JsonPropertyName("lenses")]
    public List<FileLensDto> Lenses { get; set; } = [];

    [JsonPropertyName("coverage")]
    public List<CoverageMarkDto> Coverage { get; set; } = [];

    [JsonPropertyName("coverageLabel")]
    public string? CoverageLabel { get; set; }

    [JsonPropertyName("duplications")]
    public List<DuplicateMarkDto> Duplications { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<DependencyMarkDto> Dependencies { get; set; } = [];
}

internal sealed class FileDiagnosticDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("endColumn")]
    public int? EndColumn { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "information";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "CodeInsights";

    [JsonPropertyName("documentationUrl")]
    public string? DocumentationUrl { get; set; }
}

internal sealed class FileLensDto
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
}

internal sealed class CoverageMarkDto
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("covered")]
    public bool Covered { get; set; }
}

internal sealed class DuplicateMarkDto
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

internal sealed class DependencyMarkDto
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

internal sealed class PromptResultDto
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}
