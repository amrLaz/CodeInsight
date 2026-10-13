using System.Text.Json.Serialization;
using CodeInsights.Api.Services;

namespace CodeInsights.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record IngestionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectKey { get; set; } = "";
    public string RepositoryUrl { get; init; } = "";
    public string CiProvider { get; init; } = "azure-devops";
    public string BuildId { get; init; } = "";
    public string CommitSha { get; init; } = "";
    public string Branch { get; init; } = "";
    public string? ProjectVersion { get; init; }
    public string? NewCodeReferenceBranch { get; init; }
    public string SourceSha { get; init; } = "";
    public string TargetSha { get; init; } = "";
    public string? PrId { get; init; }
    public string? PrRepoId { get; init; }
    public string? PrAdoProject { get; init; }
    public string? PrAdoOrgUrl { get; init; }
    public string? PrTargetBranch { get; init; }
    public string ProfileId { get; init; } = "";
    public string ProfileVersion { get; init; } = "";
    public List<ReportDescriptor> Reports { get; set; } = [];
    public GitDiff GitDiff { get; init; } = new();
}
public sealed record ReportDescriptor(string ReportId, string Format, string FormatVersion, string Tool, string ToolVersion, string State, long SizeBytes, string Sha256);
public sealed record GitDiff
{
    public string State { get; init; } = "unavailable";
    public string BaseSha { get; init; } = "";
    public string HeadSha { get; init; } = "";
    public List<GitFileDiff> Files { get; init; } = [];
}
public sealed record GitFileDiff(string Path, string? OldPath, int[] AddedLines, Dictionary<string, int> LineMap);
public sealed partial record ProfileDefinition
{
    public string ProfileId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Stack { get; init; } = "";
    public string[] RequiredTools { get; init; } = [];
    public string[] RequiredMetrics { get; init; } = [];
    public ProfileRule[] Rules { get; init; } = [];
    public ProfileCondition[] Conditions { get; init; } = [];
    public string[] AllowedLicenses { get; init; } = [];
    public string[] DeniedLicenses { get; init; } = [];
public int? SmallChangesThreshold { get; init; }
}
public sealed record ProfileRule(string Tool, string RuleId, string Category);
public sealed record ProfileCondition(string Metric, string Operator, double Threshold, string Scope = "global");
public sealed record ProfileAssignment(string ProfileId, string ProfileVersion, string Enforcement = "informative");
public sealed record RuleActivationRequest(string Tool, string ToolVersion, string RuleId, bool Enabled, string ExpectedProfileId, string ExpectedProfileVersion);
public sealed record ExceptionRequest(string Tool, string RuleId, string? Path, string Reason, DateTime ExpiresAt);

public static class IngestionValidation
{
    public static void Validate(IngestionManifest value, IngestionOptions options)
    {
        try { SourcePaths.ValidateRoot(value.SourceRoot); }
        catch (InvalidDataException ex) { throw new ApiException(422, ex.Message); }
        static bool Sha(string? v) => v is { Length: >= 6 and <= 64 } && v.All(Uri.IsHexDigit);
        if (value.SchemaVersion != 1 || !Sha(value.CommitSha) || string.IsNullOrWhiteSpace(value.BuildId)
            || value.BuildId.Length > 200 || string.IsNullOrWhiteSpace(value.Branch) || value.Branch.Length > 500
            || value.CiProvider is not ("azure-devops" or "github-actions" or "local")
            || value.RepositoryUrl.Length > 2000 || value.ProjectKey.Length > 100)
            throw new ApiException(422, "Invalid publication identity or schema_version");
        if (value.ProjectVersion is { Length: > 200 } || value.ProjectVersion?.Any(char.IsControl) == true
            || value.NewCodeReferenceBranch is { Length: > 500 } || value.NewCodeReferenceBranch?.Any(char.IsControl) == true
            || value.PrTargetBranch is { Length: > 500 } || value.PrTargetBranch?.Any(char.IsControl) == true)
            throw new ApiException(422, "Invalid project_version or new_code_reference_branch");
        if (value.Reports is null || value.Reports.Count is < 1 or > 100 || value.Reports.Any(x => x is null)
            || value.Reports.Select(x => x.ReportId).Distinct(StringComparer.Ordinal).Count() != value.Reports.Count)
            throw new ApiException(422, "One to 100 reports with unique report_id are required");
        long total = 0;
        foreach (var r in value.Reports)
        {
            if (string.IsNullOrWhiteSpace(r.ReportId) || r.ReportId.Length > 100 || !r.ReportId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                || string.IsNullOrWhiteSpace(r.Tool) || r.Tool.Length > 100 || string.IsNullOrWhiteSpace(r.ToolVersion) || r.ToolVersion.Length > 100
                || r.State is not ("measured" or "absent" or "failed" or "not_measured")
                || r.Sha256 is null || r.SizeBytes < 0 || r.SizeBytes > options.MaxReportBytes)
                throw new ApiException(422, "Invalid report descriptor or size limit exceeded");
            var supported = r.Format switch { "sarif" => r.FormatVersion == "2.1.0", "cyclonedx" => new[] { "1.4", "1.5", "1.6", "1.7" }.Contains(r.FormatVersion), "spdx" => r.FormatVersion == "2.3", "jscpd" or "cloc" or "lizard" or "trx" or "junit" => r.FormatVersion == "1" && !string.IsNullOrWhiteSpace(r.ToolVersion), "cobertura" or "lcov" => r.FormatVersion is "1" or "1.0", _ => false ,
            };
            if (!supported || (r.State == "measured" && (r.SizeBytes == 0 || r.Sha256 is not { Length: 64 } || !r.Sha256.All(Uri.IsHexDigit))))
                throw new ApiException(422, "Unsupported format/version or invalid report digest");
            total += r.SizeBytes;
        }
        if (total > options.MaxManifestBytes) throw new ApiException(413, "Manifest size limit exceeded");
        var diff = value.GitDiff ?? throw new ApiException(422, "git_diff must be an object");
        if (diff.Files is null || diff.Files.Count > 10000 || diff.Files.Any(x => x is null) || diff.Files.Select(x => x.Path).Distinct().Count() != diff.Files.Count) throw new ApiException(422, "Invalid or duplicate diff files");
        if (diff.Files.Sum(x => (long)(x.AddedLines?.Length ?? 0) + (x.LineMap?.Count ?? 0)) > 250000) throw new ApiException(413, "Diff exceeds 250000 line entries");
        if (diff.State == "available" && (diff.BaseSha != value.TargetSha || diff.HeadSha != value.CommitSha || !Sha(diff.BaseSha)))
            throw new ApiException(422, "Diff commits do not match publication");
        foreach (var f in diff.Files)
            if (f is null || !SafePath(f.Path) || f.OldPath is not null && !SafePath(f.OldPath) || f.AddedLines is null || f.LineMap is null
                || f.AddedLines.Any(x => x <= 0) || f.LineMap.Any(x => !int.TryParse(x.Key, out var n) || n <= 0 || x.Value <= 0))
                throw new ApiException(422, "Invalid diff paths or lines");
    }
    public static bool SafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1500 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && !path.Split('/').Any(x => x is ".." or "." or "") && !path.Any(char.IsControl);
}
