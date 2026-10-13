using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

public sealed record AnalysisProfile(
    string Version,
    string[] FormulaVersions,
    string[] RequiredMetrics,
    string[] RequiredTools
);

public static class GateEvaluation
{
    public static readonly string[] GateMetrics =
    [
        "maintainability_score",
        "security_score",
        "reliability_score",
        "code_smells",
        "vulnerabilities",
        "security_hotspots",
        "bugs",
    ];
    public static readonly string[] MeasuredMetrics =
    [
        .. GateMetrics,
        "duplication",
        "coverage",
        "dependency_vulnerabilities",
    ];
    public static readonly Dictionary<string, AnalysisProfile> Catalog = new()
    {
        ["platform-sast"] = new(
            "1",
            ["1"],
            GateMetrics.Order().ToArray(),
            ["semgrep", "trivy", "gitleaks", "checkov"]
        ),
        ["p0-dotnet"] = new(
            "1",
            ["1"],
            GateMetrics.Order().ToArray(),
            ["semgrep", "trivy", "gitleaks"]
        ),
        ["p0-react"] = new(
            "1",
            ["1"],
            GateMetrics.Order().ToArray(),
            ["semgrep", "trivy", "gitleaks"]
        ),
        ["p0-iac"] = new(
            "1",
            ["1"],
            ["security_score", "vulnerabilities"],
            ["checkov", "trivy", "gitleaks"]
        ),
        ["upcoop-security"] = new(
            "1",
            ["1"],
            ["security_hotspots", "vulnerabilities"],
            ["semgrep", "trivy", "gitleaks", "checkov"]
        ),
        ["upcoop-dotnet"] = new(
            "1",
            ["1"],
            [
                "bugs",
                "code_smells",
                "vulnerabilities",
                "security_hotspots",
                "coverage",
                "branch_coverage",
                "condition_coverage",
                "duplicate_blocks",
                "code_lines",
                "complexity",
                "dependency_vulnerabilities",
            ],
            [
                "semgrep",
                "trivy",
                "gitleaks",
                "checkov",
                "roslyn-code-smells",
                "roslyn-bugs",
                "dotnet-coverage",
                "jscpd",
                "cloc",
                "lizard",
            ]
        ),
        ["upcoop-react"] = new(
            "1",
            ["1"],
            [
                "code_smells",
                "vulnerabilities",
                "security_hotspots",
                "coverage",
                "branch_coverage",
                "duplicate_blocks",
                "code_lines",
                "complexity",
                "dependency_vulnerabilities",
            ],
            [
                "semgrep",
                "trivy",
                "gitleaks",
                "checkov",
                "eslint",
                "javascript-coverage",
                "jscpd",
                "cloc",
                "lizard",
            ]
        ),
        ["upcoop-python"] = new(
            "1",
            ["1"],
            [
                "code_smells",
                "vulnerabilities",
                "security_hotspots",
                "coverage",
                "branch_coverage",
                "condition_coverage",
                "duplicate_blocks",
                "code_lines",
                "complexity",
                "dependency_vulnerabilities",
            ],
            [
                "semgrep",
                "trivy",
                "gitleaks",
                "checkov",
                "ruff",
                "python-coverage",
                "jscpd",
                "cloc",
                "lizard",
            ]
        ),
        ["upcoop-flutter"] = new(
            "1",
            ["1"],
            [
                "code_smells",
                "vulnerabilities",
                "security_hotspots",
                "coverage",
                "branch_coverage",
                "duplicate_blocks",
                "code_lines",
                "complexity",
                "dependency_vulnerabilities",
            ],
            [
                "semgrep",
                "trivy",
                "gitleaks",
                "checkov",
                "dart-analyze",
                "flutter-coverage",
                "jscpd",
                "cloc",
                "dart-complexity",
            ]
        ),
        // MQR profile kept at version 1: PHPStan rules are classified in the ProfileEndpoints
        // catalogue, so Security/Reliability/Maintainability ratings and duplication density are
        // computed. PHPUnit's Cobertura writer never emits per-line branch detail, so branch and
        // condition coverage are not required: they stay unmeasured instead of zero.
        ["upcoop-php"] = new(
            "1",
            [MqrRatings.FormulaVersion],
            [
                "security_rating",
                "reliability_rating",
                "maintainability_rating",
                "coverage",
                "duplicated_lines_density",
            ],
            [
                "semgrep",
                "trivy",
                "gitleaks",
                "checkov",
                "phpstan",
                "php-coverage",
                "jscpd",
                "cloc",
                "lizard",
            ]
        ),
    };

    public static readonly AnalysisProfile DotNetMqrProfile = new(
        "2",
        [MqrRatings.FormulaVersion],
        [
            "security_rating",
            "reliability_rating",
            "maintainability_rating",
            "coverage",
            "duplicated_lines_density",
        ],
        [
            "semgrep",
            "trivy",
            "gitleaks",
            "checkov",
            "roslyn",
            "dotnet-coverage",
            "jscpd",
            "cloc",
            "lizard",
        ]
    );

    public static readonly AnalysisProfile ReactMqrProfile = new(
        "2",
        [MqrRatings.FormulaVersion],
        [
            "security_rating",
            "reliability_rating",
            "maintainability_rating",
            "coverage",
            "duplicated_lines_density",
        ],
        [
            "semgrep",
            "trivy",
            "gitleaks",
            "checkov",
            "eslint",
            "javascript-coverage",
            "jscpd",
            "cloc",
            "lizard",
        ]
    );

    public static bool TryGetProfile(string id, string version, out AnalysisProfile profile)
    {
        if (id == "upcoop-dotnet" && version == DotNetMqrProfile.Version)
        {
            profile = DotNetMqrProfile;
            return true;
        }
        if (id == "upcoop-react" && version == ReactMqrProfile.Version)
        {
            profile = ReactMqrProfile;
            return true;
        }
        if (Catalog.TryGetValue(id, out var current) && current.Version == version)
        {
            profile = current;
            return true;
        }
        profile = null!;
        return false;
    }

    public static IEnumerable<(string Id, AnalysisProfile Profile)> BuiltInProfiles =>
        Catalog.Select(item => (item.Key, item.Value))
            .Append(("upcoop-dotnet", DotNetMqrProfile))
            .Append(("upcoop-react", ReactMqrProfile));

    public static void Validate(AnalysisRequest request)
    {
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        static void Invalid(string field, string message) =>
            throw new ApiException(
                422,
                new[]
                {
                    new
                    {
                        type = "value_error",
                        loc = new[] { "body", field },
                        msg = message,
                    },
                }
            );
        if (request.ProjectKey is null || request.ProjectKey.Length < 2)
            Invalid("project_key", "String should have at least 2 characters");
        if (request.CommitSha is null || request.CommitSha.Length < 6)
            Invalid("commit_sha", "String should have at least 6 characters");
        if (request.Branch is null || request.Branch.Length < 1)
            Invalid("branch", "String should have at least 1 character");
        if (request.CiProvider is not ("azure-devops" or "github-actions" or "local"))
            Invalid("ci_provider", "Input should be 'azure-devops', 'github-actions' or 'local'");
        if (string.IsNullOrWhiteSpace(request.ProfileId) || request.ProfileId.Length > 100)
            Invalid("profile_id", "A profile identifier of 1 to 100 characters is required");
        if (string.IsNullOrWhiteSpace(request.ProfileVersion) || request.ProfileVersion.Length > 50)
            Invalid("profile_version", "A profile version of 1 to 50 characters is required");
        if (string.IsNullOrWhiteSpace(request.FormulaVersion) || request.FormulaVersion.Length > 50)
            Invalid("formula_version", "A formula version of 1 to 50 characters is required");
        if (request.Measurements is null || request.ToolExecutions is null)
            Invalid("body", "measurements and tool_executions must be provided");
        foreach (var (name, value) in request.Measurements)
        {
            if (!MeasuredMetrics.Contains(name))
                Invalid($"measurements.{name}", "Unknown measurement");
            if (value is null)
                Invalid($"measurements.{name}", "Null measurement is not permitted");
            if (value.State is not ("measured" or "absent" or "failed" or "not_measured"))
                Invalid($"measurements.{name}", "Invalid measurement state");
            if (
                (value.State == "measured") != (value.Value != null)
                || value.Value is < 0 or > 100000
                || value.Tool?.Length > 100
                || value.ToolVersion?.Length > 100
            )
                Invalid(
                    $"measurements.{name}",
                    "Only measured values must contain a value between 0 and 100000"
                );
            if (MeasuredMetrics.Contains(name) && value.Value is { } measured)
            {
                var max =
                    name.Contains("score") || name is "coverage" or "duplication" ? 100
                    : name is "code_smells" or "dependency_vulnerabilities" ? 100000
                    : 10000;
                if (measured > max)
                    Invalid($"measurements.{name}", $"Input should be less than or equal to {max}");
            }
        }
        foreach (var tool in request.ToolExecutions)
            if (
                tool is null
                || string.IsNullOrEmpty(tool.Tool)
                || tool.Tool.Length > 100
                || tool.Version?.Length > 100
                || tool.ErrorCode?.Length > 100
                || tool.Status is not ("success" or "failed" or "absent" or "not_measured")
            )
                Invalid("tool_executions", "Invalid tool execution");
    }

    public static AnalysisMetrics BuildMetrics(AnalysisRequest request)
    {
        var result = new AnalysisMetrics { AffectedFiles = request.AffectedFiles };
        foreach (var (name, measurement) in request.Measurements)
            result.Set(name, measurement.State == "measured" ? measurement.Value : null);
        return result;
    }

    public static string Completeness(AnalysisRequest request, Project project)
    {
        if (
            string.IsNullOrEmpty(project.AnalysisProfileId)
            || string.IsNullOrEmpty(project.AnalysisProfileVersion)
            || request.ProfileId != project.AnalysisProfileId
            || request.ProfileVersion != project.AnalysisProfileVersion
            || !TryGetProfile(project.AnalysisProfileId, project.AnalysisProfileVersion,
                out var profile)
            || !profile.FormulaVersions.Contains(request.FormulaVersion)
        )
            return "incomplete";
        var tools = request
            .ToolExecutions.GroupBy(x => x.Tool)
            .ToDictionary(x => x.Key, x => x.Last().Status);
        return
            profile.RequiredMetrics.Any(x =>
                !request.Measurements.TryGetValue(x, out var m) || m.State != "measured"
            ) || profile.RequiredTools.Any(x => !tools.TryGetValue(x, out var s) || s != "success")
            ? "incomplete"
            : "complete";
    }

    public static string TechnicalState(
        string completeness,
        IEnumerable<ToolExecution> tools,
        Dictionary<string, MeasurementValue> measurements
    )
    {
        var states = tools
            .Select(x => x.Status)
            .Concat(measurements.Values.Select(x => x.State))
            .ToHashSet();
        return states.Overlaps(["failed", "absent"]) ? "failed"
            : completeness != "complete" || states.Contains("not_measured") ? "incomplete"
            : "success";
    }

    public static (
        string Verdict,
        List<string> Failures,
        Dictionary<string, object?> Snapshot
    ) Evaluate(
        AnalysisMetrics metrics,
        QualityGateConfig gate,
        Dictionary<string, MeasurementValue> measurements,
        string profileId,
        string profileVersion,
        string formulaVersion
    )
    {
        int[] thresholds =
        [
            gate.MinMaintainabilityScore,
            gate.MinSecurityScore,
            gate.MinReliabilityScore,
            gate.MaxCodeSmells,
            gate.MaxVulnerabilities,
            gate.MaxSecurityHotspots,
            gate.MaxBugs,
        ];
        string[] labels =
        [
            "Maintainability score",
            "Security score",
            "Reliability score",
            "Code smells",
            "Vulnerabilities",
            "Security hotspots",
            "Bugs",
        ];
        List<string> failures = [];
        List<object> conditions = [];
        var knownProfile =
            TryGetProfile(profileId, profileVersion, out var profile)
            && profile.FormulaVersions.Contains(formulaVersion);
        var required = knownProfile ? profile!.RequiredMetrics : GateMetrics;
        var unknown = !knownProfile;
        for (var i = 0; i < GateMetrics.Length; i++)
        {
            var name = GateMetrics[i];
            var isRequired = required.Contains(name);
            measurements.TryGetValue(name, out var measurement);
            var value = measurement?.State == "measured" ? metrics.Get(name) : null;
            var passed = i < 3 ? value >= thresholds[i] : value <= thresholds[i];
            var result =
                value == null ? "unknown"
                : passed ? "pass"
                : "fail";
            unknown |= value == null && isRequired;
            if (result == "fail")
                failures.Add(
                    $"{labels[i]} {value} {(i < 3 ? "is below" : "exceed")} {thresholds[i]}"
                );
            conditions.Add(
                new
                {
                    metric = name,
                    required = isRequired,
                    @operator = i < 3 ? ">=" : "<=",
                    threshold = thresholds[i],
                    value,
                    state = measurement?.State ?? "not_measured",
                    tool = measurement?.Tool,
                    tool_version = measurement?.ToolVersion,
                    result,
                }
            );
        }
        return (
            failures.Count > 0 ? "fail"
            : unknown ? "unknown"
            : "pass",
            failures,
            new()
            {
                ["gate_id"] = gate.Id,
                ["gate_name"] = gate.Name,
                ["profile_id"] = profileId,
                ["profile_version"] = profileVersion,
                ["formula_version"] = formulaVersion,
                ["conditions"] = conditions,
            }
        );
    }

    public static T Parse<T>(string? json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? "null", ApiJson.Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public static AnalysisResponse ToResponse(AnalysisRecord r, bool includeFindings = true)
    {
        var metrics = new AnalysisMetrics
        {
            MaintainabilityScore = r.MaintainabilityScore,
            SecurityScore = r.SecurityScore,
            ReliabilityScore = r.ReliabilityScore,
            SecurityRating = r.SecurityRating,
            ReliabilityRating = r.ReliabilityRating,
            MaintainabilityRating = r.MaintainabilityRating,
            SecurityReviewRating = r.SecurityReviewRating,
            CodeSmells = r.CodeSmells,
            Vulnerabilities = r.Vulnerabilities,
            SecurityHotspots = r.SecurityHotspots,
            Bugs = r.Bugs,
            Coverage = r.Coverage,
            Duplication = r.Duplication,
            DependencyVulnerabilities = r.DependencyVulnerabilities,
            AffectedFiles = includeFindings
                ? Parse<Dictionary<string, JsonElement>?>(r.AffectedFiles, null)
                : null,
        };
        var measurements = Parse<Dictionary<string, MeasurementValue>>(r.Measurements, []);
        var tools = Parse<List<ToolExecution>>(r.ToolExecutions, []);
        var snapshot = Parse<Dictionary<string, JsonElement>>(r.GateSnapshot, []);
        var completeness = r.Completeness;
        return new(
            r.AnalysisId,
            r.ProjectKey,
            r.CommitSha,
            r.Branch,
            r.CiProvider,
            r.Status,
            r.QualityGate,
            metrics,
            Parse<List<string>>(r.GateFailures, []),
            r.CreatedAt,
            string.IsNullOrEmpty(r.PrId) ? null : r.PrId,
            string.IsNullOrEmpty(r.PrDecorationStatus) ? null : r.PrDecorationStatus,
            r.ProfileId,
            r.ProfileVersion,
            r.FormulaVersion,
            tools,
            measurements,
            completeness,
            snapshot.TryGetValue("technical_state", out var state)
                ? state.GetString() ?? TechnicalState(completeness, tools, measurements)
                : TechnicalState(completeness, tools, measurements),
            snapshot,
            new QualityGateResult(
                r.QualityGate,
                string.IsNullOrEmpty(r.QualityGateSummary) ? Summary(r.QualityGate, r.ErrorCount) : r.QualityGateSummary,
                r.ErrorCount,
                r.WarningCount,
                r.InfoCount
            ),
            r.StartedAt,
            r.CompletedAt,
            string.IsNullOrEmpty(r.ProviderRunId) ? null : r.ProviderRunId
        )
        {
            PrTargetBranch = string.IsNullOrEmpty(r.PrId) || string.IsNullOrEmpty(r.PrTargetBranch) ? null : r.PrTargetBranch,
        };
    }

    public static QualityGateResult Result(AnalysisRecord analysis) => new(
        analysis.QualityGate,
        string.IsNullOrEmpty(analysis.QualityGateSummary) ? Summary(analysis.QualityGate, analysis.ErrorCount) : analysis.QualityGateSummary,
        analysis.ErrorCount,
        analysis.WarningCount,
        analysis.InfoCount
    );

    public static string Summary(string status, int errors) => status switch
    {
        "pending" => "CodeInsights analysis in progress",
        "pass" => $"Quality Gate passed - {errors} blocking issues",
        "fail" => $"Quality Gate failed - {errors} blocking issues",
        _ => "CodeInsights analysis failed",
    };
}
