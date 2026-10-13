using System.Data;
using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeInsights.Api.Endpoints;

public static class ProfileEndpoints
{
    public static async Task<ProfileDefinition> ResolveAsync(AppDbContext db, string id, string version, CancellationToken ct)
    {
        var row = await db.Set<ProfileVersion>().FindAsync([id, version], ct);
        if (row is not null) return JsonSerializer.Deserialize<ProfileDefinition>(row.Definition, ApiJson.Options)!;
        if (GateEvaluation.TryGetProfile(id, version, out var builtIn) )
            return BuiltInProfile(id, builtIn);
        throw new ApiException(422, "Assign a published profile version to the project before importing");
    }

    /// <summary>Builds the immutable definition used by a built-in analysis profile.</summary>
    public static ProfileDefinition BuiltInProfile(string id, AnalysisProfile profile) =>
        id == "upcoop-dotnet" && profile.Version == "2" ? DotNetMqrProfile(profile)
        : id == "upcoop-react" && profile.Version == "2" ? ReactMqrProfile(profile)
        : id == "upcoop-php" ? PhpMqrProfile(profile)
        : id is "upcoop-security" or "upcoop-dotnet" or "upcoop-react" or "upcoop-python" or "upcoop-flutter"
            ? new ProfileDefinition
            {
                ProfileId = id,
                Version = profile.Version,
                Stack = id,
                RequiredTools = profile.RequiredTools,
                RequiredMetrics = profile.RequiredMetrics,
                Rules =
                [
                    new("semgrep", "*", "vulnerability"),
                    new("trivy", "*", "vulnerability"),
                    new("gitleaks", "*", "vulnerability"),
                    new("checkov", "*", "security_hotspot"),
                    .. id == "upcoop-dotnet"
                        ? new ProfileRule[]
                        {
                            new("roslyn-code-smells", "*", "code_smell"),
                            new("roslyn-bugs", "*", "bug"),
                        }
                        : [],
                    .. id == "upcoop-react"
                        ? new ProfileRule[] { new("eslint", "*", "code_smell") }
                        : [],
                    .. id == "upcoop-python"
                        ? new ProfileRule[] { new("ruff", "*", "code_smell") }
                        : [],
                    .. id == "upcoop-flutter"
                        ? new ProfileRule[] { new("dart-analyze", "*", "code_smell") }
                        : [],
                ],
                Conditions =
                [
                    new("vulnerabilities", "max", 10),
                    new("security_hotspots", "max", 10),
                ],
            }
            : new ProfileDefinition
            {
                ProfileId = id,
                Version = profile.Version,
                Stack = id,
                RequiredTools = profile.RequiredTools,
                RequiredMetrics = profile.RequiredMetrics,
            };
    private sealed record BulkAnalyzerRule(
        string Id,
        string Category,
        string Severity,
        int? RemediationMinutes,
        string DocumentationUrl
    );

    /// <summary>Every CA rule shipped by Microsoft.CodeAnalysis.NetAnalyzers, mapped from its
    /// official category (dotnet/roslyn-analyzers' AnalyzerReleases.Shipped/Unshipped.md) to
    /// one of our three: Security -&gt; vulnerability, Reliability -&gt; bug, everything else
    /// (Design, Performance, Naming, Globalization, Maintainability, Interoperability,
    /// Documentation, Publish, Usage) -&gt; code_smell. This is Microsoft's own published
    /// classification, not a guess — unlike the individually hand-curated entries above, which
    /// stay authoritative for any rule ID present in both places. Regenerate by re-running
    /// apps/api/Resources/dotnet-analyzer-rules.json's build script against the upstream
    /// AnalyzerReleases files when a new .NET analyzer release ships.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedDotNetRules = JsonSerializer
        .Deserialize<List<BulkAnalyzerRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "dotnet-analyzer-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every active C# compiler warning (WRN_* in dotnet/roslyn's
    /// src/Compilers/CSharp/Portable/Errors/ErrorCode.cs), defaulted to code_smell/low/5min since
    /// Microsoft does not publish a category taxonomy for compiler diagnostics the way
    /// AnalyzerReleases does for NetAnalyzers. A rule ID hand-curated above (e.g. one found to
    /// actually indicate a bug, like CS0472/CS8073/CS8629) stays authoritative and is excluded
    /// here. ERR_/FTL_ diagnostics are not included: a build producing SARIF for us already
    /// compiled, so those can never appear as findings. Regenerate by re-parsing that file when a
    /// new C# language version ships new diagnostics.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedCSharpCompilerWarnings =
        JsonSerializer
            .Deserialize<List<BulkAnalyzerRule>>(
                File.ReadAllText(
                    Path.Combine(
                        Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                        "Resources",
                        "csharp-compiler-warnings.json"
                    )
                ),
                ApiJson.Options
            )!;

    /// <summary>Every Roslyn IDE code-style rule ID shipped under the "IDE" diagnostic prefix
    /// (learn.microsoft.com's own published "Code-style rules overview" index) — distinct from
    /// NetAnalyzers (CA*) and compiler warnings (CS*): these aren't in a dotnet/roslyn-analyzers
    /// AnalyzerReleases file the way NetAnalyzers are, they're documented as a single Microsoft
    /// Learn table instead. First observed unclassified on Up.France.ODI.Uptimise.API (IDE0028).
    /// Uniform code_smell/low/5min, same treatment as the C# compiler warnings bulk import:
    /// Microsoft doesn't publish a severity/category split for this family either, and every rule
    /// here is by definition a style preference, not a bug. IDE3000 ("Implement with Copilot") is
    /// excluded -- a Visual Studio action trigger, not a real analyzer diagnostic a build can
    /// produce. Regenerate by re-parsing that index page.</summary>
    private static readonly IReadOnlyList<string> BulkImportedRoslynIdeStyleRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "roslyn-ide-style-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every ASP.NET Core analyzer rule ID (dotnet/aspnetcore's own
    /// src/Framework/AspNetCoreAnalyzers/src/Analyzers/DiagnosticDescriptors.cs, the single source
    /// file defining every ASP0* descriptor with its real category and Roslyn severity -- a
    /// genuine per-rule signal, unlike Gitleaks/Checkov-secrets). First observed unclassified on
    /// Up.France.ODI.Uptimise.API (ASP0025). Mapped from Microsoft's own (category, severity)
    /// pair: "Security" category -&gt; vulnerability; "Usage" category at Info severity -&gt;
    /// code_smell (a style nudge, matching the existing hand-curated ASP0015/ASP0027 entries this
    /// import supersedes); "Usage" at Warning/Error -&gt; bug, since this analyzer package exists
    /// specifically to catch incorrect API usage, not style. Roslyn severity scales Error-&gt;high,
    /// Warning-&gt;medium, Info-&gt;low. ASP0000 (hand-curated above, not in this source file) and
    /// ASPDEPR006 (a different, deprecation-specific diagnostic family) stay untouched. Regenerate
    /// by re-parsing that file.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedAspNetCoreAnalyzerRules = JsonSerializer
        .Deserialize<List<BulkAnalyzerRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "aspnetcore-analyzer-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every EF Core diagnostic ID: the EFCore.Analyzers rules from dotnet/efcore's
    /// src/EFCore.Analyzers/AnalyzerReleases.Shipped/Unshipped.md (removed EF1000 excluded), plus
    /// the [Experimental] IDs declared in src/Shared/EFDiagnostics.cs. First observed unclassified
    /// on Up.France.ODI.Services.Beneficiaire (EF1001). Analyzer rules use the same
    /// (category, severity) mapping as the ASP.NET Core import above: Security -&gt; vulnerability,
    /// Usage -&gt; bug (EF1001 internal API usage, EF1004 ToAsyncEnumerable on IQueryable), Roslyn
    /// Warning -&gt; medium. Experimental IDs get EF9102's hand-curated code_smell/low/10min
    /// treatment; EF9102 itself stays hand-curated above and is excluded. Regenerate by
    /// re-parsing those two files.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedEfCoreDiagnosticRules = JsonSerializer
        .Deserialize<List<BulkAnalyzerRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "efcore-diagnostic-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every SYSLIB diagnostic ID listed in dotnet/runtime's
    /// docs/project/list-of-diagnostics.md: obsoletions (SYSLIB0xxx), library analyzers and source
    /// generators (SYSLIB1xxx) and experimental APIs (SYSLIB5xxx); "reserved" placeholder rows
    /// are excluded. Microsoft publishes no category taxonomy for them, so every entry defaults to
    /// code_smell/low/10min, SYSLIB1045's hand-curated treatment. The security-motivated
    /// obsoletions hand-curated above (SYSLIB0022/0041/0051/0057) stay authoritative and are
    /// excluded. Regenerate by re-parsing that file.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedSyslibDiagnosticRules = JsonSerializer
        .Deserialize<List<BulkAnalyzerRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "syslib-diagnostic-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every PHPStan error identifier (phpstan/phpstan website/src/errorsIdentifiers.json on
    /// the 2.3.x branch, the authoritative list the PHPStan site publishes, including the rule
    /// classes that emit each identifier) plus every Larastan 3.13 identifier and the converter's
    /// phpstan.unidentified fallback. PHPStan publishes no category taxonomy, so the split follows
    /// PHP's runtime semantics: an identifier PHP sanctions with an Error/TypeError/fatal error
    /// (unknown symbol, wrong argument count or type, call on a non-object, visibility, readonly,
    /// missing return) is a medium bug at 10 min; anything emitted only by PhpDoc, Deprecations,
    /// DeadCode or Generics rules, and every other identifier (always-true/false conditions,
    /// unused code, PHPDoc, missingType), is a low code smell at 5 min. Reviewed against the 52
    /// identifiers observed on the LogiwebServices and CardsServices pilots. Regenerate with the
    /// procedure in docs/mqr-ratings.md when PHPStan or Larastan ships new identifiers.</summary>
    private static readonly IReadOnlyList<BulkAnalyzerRule> BulkImportedPhpstanRules = JsonSerializer
        .Deserialize<List<BulkAnalyzerRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "phpstan-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    private sealed record SemgrepRegistryRule(string Id, string Category, string Severity);

    /// <summary>Every rule of the Semgrep registry rulesets p/ci (UpCoopSecurityScan@2's default)
    /// and p/php (its PHP-dedicated set), downloaded from semgrep.dev/c/p/ci and semgrep.dev/c/p/php.
    /// All are security rules: metadata.subcategory "vuln" is a vulnerability, "audit" a
    /// security_hotspot to review; severity maps ERROR to high, WARNING/MEDIUM to medium and INFO
    /// to low. Regenerate by re-downloading both rulesets.</summary>
    private static readonly IReadOnlyList<SemgrepRegistryRule> BulkImportedSemgrepCiPhpRules = JsonSerializer
        .Deserialize<List<SemgrepRegistryRule>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "semgrep-ci-php-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every built-in Gitleaks rule ID (gitleaks/config/gitleaks.toml, the authoritative
    /// default ruleset — the same file this org's UpCoopSecurityScan@2 container runs). Unlike
    /// NetAnalyzers/compiler warnings, Gitleaks has no category taxonomy to map: every rule it has
    /// is secret detection, so every entry gets the same category/severity as Checkov's own
    /// CKV_SECRET_* rules in these profiles. Regenerate by re-downloading that file.</summary>
    private static readonly IReadOnlyList<string> BulkImportedGitleaksRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "gitleaks-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every Trivy secret-detection rule ID (aquasecurity/trivy's
    /// pkg/fanal/secret/builtin-rules.go), the subsystem that flagged "private-key" on
    /// Commercant.Web — distinct from Trivy's Terraform misconfiguration checks (AZU-* etc.,
    /// still hand-curated above) and from its dependency-CVE scanning (a separate SCA dimension).
    /// Same uniform secret/Medium classification as Gitleaks, for the same reason: Trivy doesn't
    /// publish categories for these either. Regenerate by re-downloading that file.</summary>
    private static readonly IReadOnlyList<string> BulkImportedTrivySecretRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "trivy-secret-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Checkov's own secret-detection check IDs (bridgecrewio/checkov's
    /// checkov/secrets/runner.py, SECRET_TYPE_TO_ID — the stable, documented core mapping, 19
    /// entries, matching checkov.io's own published secrets policy index). Checkov's secrets
    /// scanner additionally assigns undocumented, dynamically-numbered IDs (observed in source:
    /// CKV_SECRET_30/40/48/49/80/116/192/380/381...) for detect-secrets-library plugin types with
    /// no fixed Checkov ID — unlike every other bulk import in this file, that upstream ID space
    /// isn't finite or published, so only the stable 19 are imported; an unseen dynamic ID will
    /// still show unclassified, same tradeoff as a CVE/GHSA ID. Same secret dimension as Gitleaks
    /// and Trivy's secret scanner — first observed unclassified on Up.France.ODI.Uptimise.BMSC
    /// (CKV_SECRET_3, CKV_SECRET_6), but every repo scanned by UpCoopSecurityScan@2 runs Checkov's
    /// secrets detector the same way. Regenerate by re-reading that dict.</summary>
    private static readonly IReadOnlyList<string> BulkImportedCheckovSecretRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "checkov-secrets-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every Checkov Terraform-Azure and OpenAPI check ID (bridgecrewio/checkov's own
    /// check source, checkov/terraform/checks/resource/azure/*.py, the graph checks in
    /// checkov/terraform/checks/graph_checks/azure/*.yaml — mostly CKV2_AZURE_*, plus a few
    /// CKV_AZURE_* IDs that exist only there — and checkov/openapi/checks/**), three of the
    /// frameworks this org's scanner actually runs against. Uniform security_hotspot/Low,
    /// the same role as this profile's Terraform Trivy checks below — Checkov doesn't publish a
    /// finer category taxonomy split by severity. Checkov's other ~13 providers (AWS, GCP,
    /// Kubernetes, GitHub Actions...) are out of scope: this org's Terraform is Azure, not
    /// multi-cloud, and importing checks nobody's config can ever trigger adds nothing. Dockerfile
    /// is its own separate bulk import below — every repo with a docker/Dockerfile actually
    /// triggers that category, unlike the dropped ~13.
    /// Regenerate by re-walking those three directories.</summary>
    private static readonly IReadOnlyList<string> BulkImportedCheckovAzureOpenApiRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "checkov-terraform-azure-openapi-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    /// <summary>Every Checkov Dockerfile check ID (bridgecrewio/checkov's
    /// checkov/dockerfile/checks/*.py, the CKV_DOCKER_* plain checks, and
    /// checkov/dockerfile/checks/graph_checks/*.yaml, the CKV2_DOCKER_* graph-based checks).
    /// First observed unclassified on Up.France.ODI.Uptimise.API (CKV_DOCKER_2/8, triggered by
    /// this repo's docker/Dockerfile) — every repo in this org that builds a Docker image via
    /// UpCoopSecurityScan@2 is scanned the same way, so this gap was latent everywhere, not just
    /// here. Same uniform security_hotspot/Low treatment as the other Checkov bulk imports.
    /// Regenerate by re-walking those two locations.</summary>
    private static readonly IReadOnlyList<string> BulkImportedCheckovDockerfileRuleIds = JsonSerializer
        .Deserialize<List<string>>(
            File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                    "Resources",
                    "checkov-dockerfile-rules.json"
                )
            ),
            ApiJson.Options
        )!;

    private sealed record TrivyMisconfigRule(string Id, string Severity);

    /// <summary>Every Trivy Azure Terraform misconfiguration check (aquasecurity/trivy-checks'
    /// checks/cloud/azure/**.rego, each carrying its own "# custom: id: AZU-*" and "severity:"
    /// metadata — a real per-rule signal, unlike Gitleaks/Checkov, so severity is preserved rather
    /// than defaulted). Supersedes the 3 individually hand-added AZU-0001/0015/0017 entries.
    /// Regenerate by re-walking that directory.</summary>
    private static readonly IReadOnlyList<TrivyMisconfigRule> BulkImportedTrivyAzureMisconfigRules =
        JsonSerializer
            .Deserialize<List<TrivyMisconfigRule>>(
                File.ReadAllText(
                    Path.Combine(
                        Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                        "Resources",
                        "trivy-azure-misconfig-rules.json"
                    )
                ),
                ApiJson.Options
            )!;

    /// <summary>Every Trivy Dockerfile misconfiguration check (aquasecurity/trivy-checks'
    /// checks/docker/*.rego, each carrying the same real per-rule "# custom: id: DS-*"/"severity:"
    /// metadata as the Azure Terraform checks above) — distinct from Checkov's own Dockerfile
    /// checks (CKV_DOCKER_*/CKV2_DOCKER_*, a different tool's independent ruleset for the same
    /// file) and from Trivy's own Terraform misconfig checks (AZU-*, a different scanner mode).
    /// First observed unclassified on Up.France.ODI.Uptimise.API (DS-0002/0026/0029, triggered by
    /// this repo's docker/Dockerfile) — every repo building a Docker image via
    /// UpCoopSecurityScan@2 runs this check mode the same way. Regenerate by re-walking that
    /// directory.</summary>
    private static readonly IReadOnlyList<TrivyMisconfigRule> BulkImportedTrivyDockerfileMisconfigRules =
        JsonSerializer
            .Deserialize<List<TrivyMisconfigRule>>(
                File.ReadAllText(
                    Path.Combine(
                        Path.GetDirectoryName(typeof(ProfileEndpoints).Assembly.Location)!,
                        "Resources",
                        "trivy-dockerfile-misconfig-rules.json"
                    )
                ),
                ApiJson.Options
            )!;

    private static ProfileDefinition DotNetMqrProfile(AnalysisProfile profile)
    {
        var parameters = JsonSerializer.SerializeToElement(new { });
        const string nullableDocs =
            "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/nullable-warnings";
        CatalogueRule Rule(
            string id,
            string category,
            string severity,
            int? remediationMinutes = null,
            string? documentationUrl = null
        ) =>
            new(
                "roslyn",
                "10.0.104",
                id,
                "csharp",
                category,
                parameters,
                documentationUrl ?? nullableDocs,
                "net10.0",
                remediationMinutes is { } minutes
                    ? new("constant", minutes * 60, documentationUrl ?? nullableDocs)
                    : null,
                severity
            );
        CatalogueRule SemgrepRule(string id, string category, string severity) =>
            new(
                "semgrep",
                "1.176.1",
                id,
                "csharp",
                category,
                parameters,
                $"https://semgrep.dev/r/{id}",
                "net10.0",
                Severity: severity
            );
        // Trivy's Terraform misconfiguration checks (AVD-*/AZU-* IDs), distinct from its
        // dependency-CVE scanning which stays its own separate SCA dimension. Classified
        // security_hotspot, same role as Checkov's IaC checks.
        CatalogueRule TrivyRule(string id, string severity) =>
            new(
                "trivy",
                "0.74.0",
                id,
                id.StartsWith("DS-", StringComparison.Ordinal) ? "dockerfile" : "terraform",
                "security_hotspot",
                parameters,
                $"https://avd.aquasec.com/misconfig/{id.ToLowerInvariant()}",
                "net10.0",
                Severity: severity
            );
        CatalogueRule CheckovRule(string id) =>
            new(
                "checkov",
                "3.3.16",
                id,
                id.StartsWith("CKV_DOCKER_", StringComparison.Ordinal) || id.StartsWith("CKV2_DOCKER_", StringComparison.Ordinal)
                    ? "dockerfile"
                    : "terraform",
                "security_hotspot",
                parameters,
                id.StartsWith("CKV_OPENAPI_", StringComparison.Ordinal)
                    ? "https://www.checkov.io/5.Policy%20Index/openapi.html"
                    : id.StartsWith("CKV_DOCKER_", StringComparison.Ordinal) || id.StartsWith("CKV2_DOCKER_", StringComparison.Ordinal)
                        ? "https://www.checkov.io/5.Policy%20Index/dockerfile.html"
                        : "https://www.checkov.io/5.Policy%20Index/terraform.html",
                "net10.0",
                Severity: "low"
            );
        // Same secret-detection dimension found unclassified on Commercant.Web (React) — Gitleaks
        // and Trivy scan every repo through the same UpCoopSecurityScan@2 container regardless of
        // stack, so this profile has the identical latent gap even though it hasn't fired here yet.
        CatalogueRule GitleaksRule(string id) =>
            new(
                "gitleaks",
                "8.30.1",
                id,
                "secret",
                "secret",
                parameters,
                "https://github.com/gitleaks/gitleaks/blob/master/config/gitleaks.toml",
                "net10.0",
                Severity: "medium"
            );
        CatalogueRule TrivySecretRule(string id) =>
            new(
                "trivy",
                "0.74.0",
                id,
                "secret",
                "secret",
                parameters,
                "https://trivy.dev/latest/docs/scanner/secret/",
                "net10.0",
                Severity: "medium"
            );
        CatalogueRule CheckovSecretRule(string id) =>
            new(
                "checkov",
                "3.3.16",
                id,
                "secret",
                "secret",
                parameters,
                "https://www.checkov.io/5.Policy%20Index/secrets.html",
                "net10.0",
                Severity: "medium"
            );
        return new ProfileDefinition
        {
            ProfileId = "upcoop-dotnet",
            Version = profile.Version,
            Stack = "dotnet",
            RequiredTools = profile.RequiredTools,
            RequiredMetrics = profile.RequiredMetrics,
            Rules = [],
            RuleCatalog =
            [
                SemgrepRule(
                    "dotnet.dangerous-accept-any-server-certificate",
                    "vulnerability",
                    "high"
                ),
                SemgrepRule("dotnet.permissive-cors", "security_hotspot", "medium"),
                Rule(
                    "CS4014",
                    "bug",
                    "high",
                    documentationUrl: "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs4014"
                ),
                Rule("ASP0000", "bug", "high", documentationUrl: "https://learn.microsoft.com/aspnet/core/diagnostics/asp0000?view=aspnetcore-10.0"),
                Rule("CS8600", "bug", "medium"),
                Rule("CS8601", "bug", "medium"),
                Rule("CS8602", "bug", "high"),
                Rule("CS8603", "bug", "medium"),
                Rule("CS8604", "bug", "high"),
                Rule("CS8618", "bug", "medium"),
                Rule("CS8625", "bug", "medium"),
                Rule("CS8629", "bug", "medium"),
                Rule(
                    "CA2016",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016"
                ),
                Rule(
                    "CS0649",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/dotnet/csharp/misc/cs0649"
                ),
                // Best-effort classification, not independently verified against MudBlazor's
                // analyzer rule documentation or Razor's diagnostic catalogue.
                Rule(
                    "RZ10012",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/aspnet/core/blazor/"
                ),
                Rule(
                    "CS0168",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/csharp/misc/cs0168"
                ),
                Rule(
                    "CS0219",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/csharp/misc/cs0219"
                ),
                Rule(
                    "CS0169",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/csharp/misc/cs0169"
                ),
                Rule(
                    "CS0618",
                    "code_smell",
                    "low",
                    15,
                    "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs0618"
                ),
                Rule(
                    "CS1998",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs1998"
                ),
                Rule(
                    "CA1000",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1000"
                ),
                Rule(
                    "CA1051",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1051"
                ),
                Rule(
                    "CA1824",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1824"
                ),
                Rule(
                    "CA1829",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1829"
                ),
                Rule(
                    "CA1866",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1866"
                ),
                Rule(
                    "CA1806",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1806"
                ),
                Rule(
                    "CA1822",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1822"
                ),
                Rule(
                    "CA1869",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1869"
                ),
                Rule(
                    "CA1050",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1050"
                ),
                Rule(
                    "CA1068",
                    "code_smell",
                    "low",
                    15,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1068"
                ),
                Rule("CA1848", "code_smell", "low", 5, "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1848"),
                Rule(
                    "CA1859",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1859"
                ),
                Rule(
                    "CA1860",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1860"
                ),
                Rule(
                    "CA1861",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1861"
                ),
                Rule(
                    "CA1862",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1862"
                ),
                Rule(
                    "CA1873",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1873"
                ),
                Rule(
                    "CS9236",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/overload-resolution"
                ),
                Rule(
                    "SYSLIB1045",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib1045"
                ),
                // Both flag genuinely weak cryptography (obsolete cipher / weak KDF defaults),
                // same severity as CA5379 (Security -> vulnerability, High) in the bulk import.
                Rule(
                    "SYSLIB0022",
                    "vulnerability",
                    "high",
                    documentationUrl: "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0022"
                ),
                Rule(
                    "SYSLIB0041",
                    "vulnerability",
                    "high",
                    documentationUrl: "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0041"
                ),
                // Same SYSLIB00xx security-motivated-obsoletion family as SYSLIB0022/SYSLIB0041
                // above: flags the legacy BinaryFormatter-era ISerializable(SerializationInfo,
                // StreamingContext) constructor pattern Microsoft is removing as part of its
                // BinaryFormatter deserialization-RCE mitigation push, first observed on
                // Up.France.ODI.Loyalty.
                Rule(
                    "SYSLIB0051",
                    "vulnerability",
                    "high",
                    documentationUrl: "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0051"
                ),
                // Same family: flags the X509Certificate/X509Certificate2 constructors that load
                // raw byte[]/file-path content, obsoleted in favor of X509CertificateLoader for
                // safer, more consistent certificate-loading behavior -- filed by Microsoft under
                // its cryptography compatibility docs, not a plain API-shape deprecation.
                Rule(
                    "SYSLIB0057",
                    "vulnerability",
                    "high",
                    documentationUrl: "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0057"
                ),
                // EF Core's own "[Experimental]"-attribute diagnostic (e.g. the Cosmos
                // ToPageAsync extension): using an evaluation-only API that may change or be
                // removed is a maintainability risk, not a bug or a vulnerability -- same role as
                // SYSLIB1045's deprecation-code_smell treatment above.
                Rule(
                    "EF9102",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/ef/core/what-is-new/ef-core-9.0/breaking-changes"
                ),
                // Best-effort classification, not independently verified against MudBlazor's
                // analyzer rule documentation.
                Rule(
                    "MUD0010",
                    "code_smell",
                    "low",
                    10,
                    "https://mudblazor.com/"
                ),
                Rule(
                    "ASP5001",
                    "code_smell",
                    "low",
                    10,
                    "https://learn.microsoft.com/aspnet/core/mvc/compatibility-version"
                ),
                Rule(
                    "CA2254",
                    "code_smell",
                    "low",
                    5,
                    "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2254"
                ),
                Rule(
                    "CS0472",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/dotnet/csharp/misc/cs0472"
                ),
                Rule(
                    "CS8073",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/dotnet/csharp/misc/cs0472"
                ),
                Rule(
                    "CS9179",
                    "bug",
                    "medium",
                    documentationUrl: "https://learn.microsoft.com/dotnet/csharp/language-reference/proposals/csharp-12.0/primary-constructors"
                ),
                Rule(
                    "CS8632",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/nullable-warnings"
                ),
                Rule(
                    "CS0105",
                    "code_smell",
                    "low",
                    2,
                    "https://learn.microsoft.com/dotnet/csharp/misc/cs0105"
                ),
                // Asp.Versioning.Mvc.ApiExplorer analyzer rules (AVxxxx), first observed on
                // Up.France.ODI.Services.Commercant's integration/tst branch. Best-effort
                // classification as configuration-recommendation code smells, not independently
                // verified against the tool's own documentation.
                Rule(
                    "AV0011",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/dotnet/aspnet-api-versioning"
                ),
                Rule(
                    "AV0013",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/dotnet/aspnet-api-versioning"
                ),
                Rule(
                    "AV0015",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/dotnet/aspnet-api-versioning"
                ),
                Rule(
                    "AV0016",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/dotnet/aspnet-api-versioning"
                ),
                Rule(
                    "AV0020",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/dotnet/aspnet-api-versioning"
                ),
                Rule(
                    "ASPDEPR006",
                    "code_smell",
                    "low",
                    10,
                    "https://aka.ms/aspnet/deprecate/006"
                ),
                .. BulkImportedDotNetRules.Select(bulk =>
                    Rule(bulk.Id, bulk.Category, bulk.Severity, bulk.RemediationMinutes, bulk.DocumentationUrl)
                ),
                .. BulkImportedCSharpCompilerWarnings.Select(bulk =>
                    Rule(bulk.Id, bulk.Category, bulk.Severity, bulk.RemediationMinutes, bulk.DocumentationUrl)
                ),
                .. BulkImportedRoslynIdeStyleRuleIds.Select(id =>
                    Rule(id, "code_smell", "low", 5, "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/")
                ),
                .. BulkImportedAspNetCoreAnalyzerRules.Select(bulk =>
                    Rule(bulk.Id, bulk.Category, bulk.Severity, bulk.RemediationMinutes, bulk.DocumentationUrl)
                ),
                .. BulkImportedEfCoreDiagnosticRules.Select(bulk =>
                    Rule(bulk.Id, bulk.Category, bulk.Severity, bulk.RemediationMinutes, bulk.DocumentationUrl)
                ),
                .. BulkImportedSyslibDiagnosticRules.Select(bulk =>
                    Rule(bulk.Id, bulk.Category, bulk.Severity, bulk.RemediationMinutes, bulk.DocumentationUrl)
                ),
                .. BulkImportedGitleaksRuleIds.Select(GitleaksRule),
                .. BulkImportedTrivySecretRuleIds.Select(TrivySecretRule),
                .. BulkImportedCheckovSecretRuleIds.Select(CheckovSecretRule),
                .. BulkImportedTrivyAzureMisconfigRules.Select(bulk => TrivyRule(bulk.Id, bulk.Severity)),
                .. BulkImportedTrivyDockerfileMisconfigRules.Select(bulk => TrivyRule(bulk.Id, bulk.Severity)),
                .. BulkImportedCheckovAzureOpenApiRuleIds.Select(CheckovRule),
                .. BulkImportedCheckovDockerfileRuleIds.Select(CheckovRule),
            ],
            Conditions =
            [
                new("security_rating", "max", 1, "new_code"),
                new("reliability_rating", "max", 1, "new_code"),
                new("maintainability_rating", "max", 1, "new_code"),
                new("coverage", "min", 80, "new_code"),
                new("duplicated_lines_density", "max", 3, "new_code"),
            ],
            SecurityReview = new(true, 100, "new_code"),
            SmallChangesThreshold = 20,
        };
    }

    /// <summary>Builds the immutable definition for upcoop-react/2, the first MQR-rated version of
    /// this profile. Catalog curated from ESLint findings actually observed on a real pilot
    /// (Up.France.ODI.Commercant.Web, 176 findings across 10 rule IDs) — not an exhaustive
    /// import of the recommended/typescript-eslint rulesets.</summary>
    private static ProfileDefinition ReactMqrProfile(AnalysisProfile profile)
    {
        var parameters = JsonSerializer.SerializeToElement(new { });
        CatalogueRule Rule(string id, string category, string severity, int? remediationMinutes = null, string? documentationUrl = null) =>
            new(
                "eslint",
                "9.39.2",
                id,
                "typescript",
                category,
                parameters,
                documentationUrl,
                "9.39.2",
                remediationMinutes is { } minutes ? new("constant", minutes * 60, documentationUrl!) : null,
                severity
            );
        CatalogueRule SemgrepRule(string id, string category, string severity) =>
            new(
                "semgrep",
                "1.176.1",
                id,
                "typescript",
                category,
                parameters,
                $"https://semgrep.dev/r/{id}",
                "9.39.2",
                Severity: severity
            );
        CatalogueRule CheckovRule(string id, string category, string severity) =>
            new(
                "checkov",
                "3.2.x",
                id,
                "typescript",
                category,
                parameters,
                $"https://www.checkov.io/5.Policy%20Index/secrets.html#{id}",
                "9.39.2",
                Severity: severity
            );
        // Gitleaks, Trivy and Checkov's own secret-detection check IDs are all bulk-imported below
        // rather than hand-curated — the two CKV_SECRET_6/9 entries this profile used to hand-pick
        // are now part of the full BulkImportedCheckovSecretRuleIds import (same stable 19-entry
        // set as upcoop-dotnet/2), so they moved there instead of staying separate literals here.
        CatalogueRule GitleaksRule(string id, string category, string severity) =>
            new(
                "gitleaks",
                "8.30.1",
                id,
                "typescript",
                category,
                parameters,
                "https://github.com/gitleaks/gitleaks/blob/master/config/gitleaks.toml",
                "9.39.2",
                Severity: severity
            );
        CatalogueRule TrivyRule(string id, string category, string severity) =>
            new(
                "trivy",
                "0.74.0",
                id,
                "typescript",
                category,
                parameters,
                "https://trivy.dev/latest/docs/scanner/secret/",
                "9.39.2",
                Severity: severity
            );
        return new ProfileDefinition
        {
            ProfileId = "upcoop-react",
            Version = profile.Version,
            Stack = "react",
            RequiredTools = profile.RequiredTools,
            RequiredMetrics = profile.RequiredMetrics,
            Rules = [],
            RuleCatalog =
            [
                SemgrepRule(
                    "package_managers.npm.npm-missing-minimum-release-age.npm-missing-minimum-release-age",
                    "vulnerability",
                    "medium"
                ),
                .. BulkImportedGitleaksRuleIds.Select(id => GitleaksRule(id, "secret", "medium")),
                .. BulkImportedTrivySecretRuleIds.Select(id => TrivyRule(id, "secret", "medium")),
                .. BulkImportedCheckovSecretRuleIds.Select(id => CheckovRule(id, "secret", "medium")),
                Rule(
                    "eqeqeq",
                    "bug",
                    "low",
                    documentationUrl: "https://eslint.org/docs/latest/rules/eqeqeq"
                ),
                Rule(
                    "@typescript-eslint/no-explicit-any",
                    "code_smell",
                    "low",
                    5,
                    "https://typescript-eslint.io/rules/no-explicit-any"
                ),
                Rule(
                    "@typescript-eslint/naming-convention",
                    "code_smell",
                    "low",
                    5,
                    "https://typescript-eslint.io/rules/naming-convention"
                ),
                Rule(
                    "@typescript-eslint/no-empty-function",
                    "code_smell",
                    "low",
                    5,
                    "https://typescript-eslint.io/rules/no-empty-function"
                ),
                Rule(
                    "react/no-unescaped-entities",
                    "code_smell",
                    "low",
                    2,
                    "https://github.com/jsx-eslint/eslint-plugin-react/blob/master/docs/rules/no-unescaped-entities.md"
                ),
                Rule(
                    "spaced-comment",
                    "code_smell",
                    "low",
                    1,
                    "https://eslint.org/docs/latest/rules/spaced-comment"
                ),
                Rule(
                    "@typescript-eslint/no-inferrable-types",
                    "code_smell",
                    "low",
                    2,
                    "https://typescript-eslint.io/rules/no-inferrable-types"
                ),
                Rule(
                    "unused-imports/no-unused-vars",
                    "code_smell",
                    "low",
                    5,
                    "https://github.com/sweepline/eslint-plugin-unused-imports"
                ),
                Rule(
                    "prefer-template",
                    "code_smell",
                    "low",
                    2,
                    "https://eslint.org/docs/latest/rules/prefer-template"
                ),
                Rule(
                    "no-unused-vars",
                    "code_smell",
                    "low",
                    5,
                    "https://eslint.org/docs/latest/rules/no-unused-vars"
                ),
            ],
            Conditions =
            [
                new("security_rating", "max", 1, "new_code"),
                new("reliability_rating", "max", 1, "new_code"),
                new("maintainability_rating", "max", 1, "new_code"),
                new("coverage", "min", 80, "new_code"),
                new("duplicated_lines_density", "max", 3, "new_code"),
            ],
            // Unlike upcoop-dotnet/2 (Checkov Terraform + Semgrep permissive-cors both classified
            // security_hotspot), this catalog has no rule in that category at all — Checkov's own
            // entries here are "secret" (CKV_SECRET_*). A hotspot-review threshold against zero
            // possible hotspots can never resolve; showing "-" forever isn't a pending measurement,
            // it's a structurally unsatisfiable one. Disabled until a real security_hotspot finding
            // is observed on a React pilot to classify.
            SecurityReview = new(false, null, "new_code"),
            SmallChangesThreshold = 20,
        };
    }

    private static ProfileDefinition PhpMqrProfile(AnalysisProfile profile)
    {
        var parameters = JsonSerializer.SerializeToElement(new { });
        CatalogueRule PhpstanRule(BulkAnalyzerRule bulk) =>
            new(
                "phpstan",
                "2.3.1",
                bulk.Id,
                "php",
                bulk.Category,
                parameters,
                bulk.DocumentationUrl,
                "2.3.1",
                new("constant", bulk.RemediationMinutes!.Value * 60, bulk.DocumentationUrl),
                bulk.Severity
            );
        // Same Dockerfile classification as upcoop-dotnet/2: both pilots ship a Dockerfile and
        // reported CKV_DOCKER_2/3, CKV2_DOCKER_17 and DS-0002/0026/0029 as security hotspots.
        CatalogueRule TrivyRule(string id, string severity) =>
            new(
                "trivy",
                "0.74.0",
                id,
                "dockerfile",
                "security_hotspot",
                parameters,
                $"https://avd.aquasec.com/misconfig/{id.ToLowerInvariant()}",
                "php",
                Severity: severity
            );
        CatalogueRule CheckovRule(string id) =>
            new(
                "checkov",
                "3.3.16",
                id,
                "dockerfile",
                "security_hotspot",
                parameters,
                "https://www.checkov.io/5.Policy%20Index/dockerfile.html",
                "php",
                Severity: "medium"
            );
        // Laravel services version their OpenAPI specification (BmscServices reported
        // CKV_OPENAPI_4/5/21 unclassified): same security_hotspot/Low role as upcoop-dotnet/2.
        CatalogueRule CheckovOpenApiRule(string id) =>
            new(
                "checkov",
                "3.3.16",
                id,
                "openapi",
                "security_hotspot",
                parameters,
                "https://www.checkov.io/5.Policy%20Index/openapi.html",
                "php",
                Severity: "low"
            );
        CatalogueRule SemgrepRule(SemgrepRegistryRule bulk) =>
            new(
                "semgrep",
                "1.176.1",
                bulk.Id,
                "php",
                bulk.Category,
                parameters,
                $"https://semgrep.dev/r/{bulk.Id}",
                "php",
                Severity: bulk.Severity
            );
        CatalogueRule SecretRule(string tool, string toolVersion, string id, string documentationUrl) =>
            new(tool, toolVersion, id, "secret", "secret", parameters, documentationUrl, "php", Severity: "medium");
        return new ProfileDefinition
        {
            ProfileId = "upcoop-php",
            Version = profile.Version,
            Stack = "php",
            RequiredTools = profile.RequiredTools,
            RequiredMetrics = profile.RequiredMetrics,
            Rules = [],
            RuleCatalog =
            [
                .. BulkImportedPhpstanRules.Select(PhpstanRule),
                .. BulkImportedSemgrepCiPhpRules.Select(SemgrepRule),
                .. BulkImportedGitleaksRuleIds.Select(id => SecretRule(
                    "gitleaks", "8.30.1", id, "https://github.com/gitleaks/gitleaks/blob/master/config/gitleaks.toml")),
                .. BulkImportedTrivySecretRuleIds.Select(id => SecretRule(
                    "trivy", "0.74.0", id, "https://trivy.dev/latest/docs/scanner/secret/")),
                .. BulkImportedCheckovSecretRuleIds.Select(id => SecretRule(
                    "checkov", "3.3.16", id, "https://www.checkov.io/5.Policy%20Index/secrets.html")),
                .. BulkImportedTrivyDockerfileMisconfigRules.Select(bulk => TrivyRule(bulk.Id, bulk.Severity)),
                .. BulkImportedCheckovDockerfileRuleIds.Select(CheckovRule),
                .. BulkImportedCheckovAzureOpenApiRuleIds
                    .Where(id => id.StartsWith("CKV_OPENAPI_", StringComparison.Ordinal))
                    .Select(CheckovOpenApiRule),
            ],
            Conditions =
            [
                new("security_rating", "max", 1, "new_code"),
                new("reliability_rating", "max", 1, "new_code"),
                new("maintainability_rating", "max", 1, "new_code"),
                new("coverage", "min", 80, "new_code"),
                new("duplicated_lines_density", "max", 3, "new_code"),
            ],
            SecurityReview = new(true, 100, "new_code"),
            SmallChangesThreshold = 20,
        };
    }

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/profiles").RequireAuthorization();
        routes.MapGet("/{id}/versions/{version}/catalogue", async (string id, string version, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var principal = await auth.RequireAsync(context, ct: ct); auth.AssertCiPermission(principal);
            var profile = await ResolveAsync(db, id, version, ct);
            var size = InsightEndpoints.PageSize(context.Request);
            var cursor = InsightEndpoints.DecodeCursor(context.Request, 1);
            var search = context.Request.Query["search"].ToString();
            var language = context.Request.Query["language"].ToString();
            var rules = profile.RuleCatalog.Where(rule => (language.Length == 0 || rule.Language == language) && (search.Length == 0 || rule.RuleId.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(ExecutableProfiles.RuleKey, StringComparer.Ordinal)
                .Where(rule => cursor is null || string.CompareOrdinal(ExecutableProfiles.RuleKey(rule), cursor[0]) > 0).Take(size + 1).ToList();
            if (rules.Count > size) { rules.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(ExecutableProfiles.RuleKey(rules[^1]))); }
            return Results.Ok(rules);
        });
        routes.MapGet("/{id}/versions/{version}/diff/{previous}", async (string id, string version, string previous, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var principal = await auth.RequireAsync(context, ct: ct); auth.AssertCiPermission(principal);
            return Results.Ok(ExecutableProfiles.Diff(await ResolveAsync(db, id, previous, ct), await ResolveAsync(db, id, version, ct)));
        });
        routes.MapGet("/projects/{key}/configuration", async (string key, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var principal = await auth.RequireAsync(context, ct: ct); await auth.AssertProjectAccessAsync(principal, key, ct: ct);
            var policy = await db.Set<IngestionProjectPolicy>().FindAsync([key], ct) ?? throw new ApiException(404, "No profile assigned");
            return Results.Ok(ExecutableProfiles.Export(await ResolveAsync(db, policy.ProfileId, policy.ProfileVersion, ct)));
        });
        routes.MapGet("/projects/{key}/impact", async (string key, string profileId, string version, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var principal = await auth.RequireAsync(context, "admin", ct); await auth.AssertProjectAccessAsync(principal, key, ct: ct);
            var target = await ResolveAsync(db, profileId, version, ct);
            var policy = await db.Set<IngestionProjectPolicy>().FindAsync([key], ct);
            var current = policy is null ? new ProfileDefinition() : await ResolveAsync(db, policy.ProfileId, policy.ProfileVersion, ct);
            return Results.Ok(new { project_key = key, current_profile = policy?.ProfileId, current_version = policy?.ProfileVersion,
                target_profile = target.ProfileId, target_version = target.Version, changes = ExecutableProfiles.Diff(current, target),
                historical_verdicts_changed = false, future_evaluation = "uses_new_profile_and_requires_matching_effective_configuration", reevaluated_analysis_count = 0 ,
                    });
        });
        routes.MapGet("", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var principal = await auth.RequireAsync(context, ct: ct); auth.AssertCiPermission(principal);
            var size = InsightEndpoints.PageSize(context.Request);
            var cursor = InsightEndpoints.DecodeCursor(context.Request, 2);
            var query = db.Set<ProfileVersion>().AsNoTracking();
            if (cursor is not null) { var id = cursor[0]; var version = cursor[1]; query = query.Where(x => string.Compare(x.ProfileId, id) > 0 || x.ProfileId == id && string.Compare(x.Version, version) > 0); }
            var rows = await query.OrderBy(x => x.ProfileId).ThenBy(x => x.Version).Take(size + 1).Select(x => new { x.ProfileId, x.Version, x.Definition ,
                    }).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(rows[^1].ProfileId, rows[^1].Version)); }
            return Results.Ok(rows.Select(x => JsonSerializer.Deserialize<ProfileDefinition>(x.Definition, ApiJson.Options)));
        });
        routes.MapPost("", async (HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, "admin", ct);
            var profile = await ApiJson.ReadAsync<ProfileDefinition>(context, ct);
            Validate(profile);
            if (GateEvaluation.TryGetProfile(profile.ProfileId, profile.Version, out _) || await db.Set<ProfileVersion>().AnyAsync(x => x.ProfileId == profile.ProfileId && x.Version == profile.Version, ct))
                throw new ApiException(409, "Profile versions are immutable; publish a new version");
            db.Add(new ProfileVersion { ProfileId = profile.ProfileId, Version = profile.Version, Definition = JsonSerializer.Serialize(profile, ApiJson.Options) ,
                    });
            audit.Log(user.Email, user.Role, "profile.publish", "profile", resourceLabel: $"{profile.ProfileId}/{profile.Version}");
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/v1/profiles", profile);
        });
        routes.MapGet("/projects/{key}", async (string key, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, ct: ct); await auth.AssertProjectAccessAsync(user, key, ct: ct);
            var policy = await db.Set<IngestionProjectPolicy>().FindAsync([key], ct);
            return Results.Json(JsonSerializer.SerializeToElement(policy is null ? null : new ProfileAssignment(policy.ProfileId, policy.ProfileVersion, policy.Enforcement), ApiJson.Options));
        });
        routes.MapPut("/projects/{key}", async (string key, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, "admin", ct); await auth.AssertProjectAccessAsync(user, key, "analysis:write", ct);
            var body = await ApiJson.ReadAsync<ProfileAssignment>(context, ct);
            await ResolveAsync(db, body.ProfileId, body.ProfileVersion, ct);
            if (body.Enforcement is not ("informative" or "blocking")) throw new ApiException(422, "Invalid enforcement");
            var row = await db.Set<IngestionProjectPolicy>().FindAsync([key], ct);
            if (row is null) { row = new IngestionProjectPolicy { ProjectKey = key }; db.Add(row); }
            row.ProfileId = body.ProfileId; row.ProfileVersion = body.ProfileVersion; row.Enforcement = body.Enforcement;
            audit.Log(user.Email, user.Role, "profile.assign", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct); return Results.Ok(body);
        });
        routes.MapPost("/projects/{key}/rule-activation", async (string key, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, "admin", ct); await auth.AssertProjectAccessAsync(user, key, "analysis:write", ct);
            var body = await ApiJson.ReadAsync<RuleActivationRequest>(context, ct);
            if (string.IsNullOrWhiteSpace(body.Tool) || string.IsNullOrWhiteSpace(body.ToolVersion) || string.IsNullOrWhiteSpace(body.RuleId)
                || string.IsNullOrWhiteSpace(body.ExpectedProfileId) || string.IsNullOrWhiteSpace(body.ExpectedProfileVersion))
                throw new ApiException(422, "Rule activation requires producer, version, rule and expected profile assignment");
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var policy = await db.Set<IngestionProjectPolicy>().FromSqlInterpolated(
                $"SELECT * FROM ingestion_project_policies WHERE project_key = {key} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new ApiException(404, "No profile assigned");
            if (policy.ProfileId != body.ExpectedProfileId || policy.ProfileVersion != body.ExpectedProfileVersion)
                throw new ApiException(409, "Profile assignment changed; reload before updating the rule");
            var current = await ResolveAsync(db, policy.ProfileId, policy.ProfileVersion, ct);
            var catalogueRule = ExecutableProfiles.BuildRuleIndex(current).Resolve(body.Tool, body.RuleId);
            if (catalogueRule is null) throw new ApiException(422, "Rule is absent or ambiguous in the catalogue");
            var identity = new DisabledRule(catalogueRule.Tool, catalogueRule.ToolVersion, catalogueRule.RuleId);
            var normalizedBody = body with { Tool = identity.Tool, ToolVersion = identity.ToolVersion, RuleId = identity.RuleId };
            if (!ExecutableProfiles.CanManage(current, identity))
                throw new ApiException(422, "Rule is absent from the catalogue or its analyzer configuration cannot be transformed reliably");
            var wasDisabled = current.DisabledRules.Any(rule => ExecutableProfiles.Matches(rule, identity));
            if (wasDisabled == !body.Enabled)
                return Results.Ok(RuleActivationResponse(key, current, current, normalizedBody, policy.Enforcement));
            var disabled = body.Enabled
                ? current.DisabledRules.Where(rule => !ExecutableProfiles.Matches(rule, identity)).ToArray()
                : [.. current.DisabledRules, identity];
            var version = await NextRuleVersionAsync(db, current.ProfileId, current.Version, ct);
            var updated = current with { Version = version, DisabledRules = disabled };
            Validate(updated);
            db.Add(new ProfileVersion { ProfileId = updated.ProfileId, Version = updated.Version, Definition = JsonSerializer.Serialize(updated, ApiJson.Options) ,
                    });
            policy.ProfileVersion = updated.Version;
            audit.Log(user.Email, user.Role, body.Enabled ? "profile.rule.enable" : "profile.rule.disable", "project", resourceLabel: key,
                details: new { normalizedBody.Tool, normalizedBody.ToolVersion, normalizedBody.RuleId, previous_version = current.Version, profile_version = updated.Version ,
                    });
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (Exception exception) when (exception is DbUpdateException or PostgresException { SqlState: "40001" or "23505" })
            {
                throw new ApiException(409, "Profile assignment changed; reload before updating the rule");
            }
            return Results.Ok(RuleActivationResponse(key, current, updated, normalizedBody, policy.Enforcement));
        });
        routes.MapGet("/projects/{key}/exceptions", async (string key, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, ct: ct); await auth.AssertProjectAccessAsync(user, key, ct: ct);
            return Results.Ok(await db.Set<ProfileException>().Where(x => x.ProjectKey == key).OrderByDescending(x => x.CreatedAt).Take(100)
                .Select(x => new { x.Id, x.Tool, x.RuleId, x.Path, x.Reason, x.ExpiresAt ,
                        }).ToListAsync(ct));
        });
        routes.MapPost("/projects/{key}/exceptions", async (string key, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await auth.RequireAsync(context, "admin", ct); await auth.AssertProjectAccessAsync(user, key, "analysis:write", ct);
            var body = await ApiJson.ReadAsync<ExceptionRequest>(context, ct);
            if (string.IsNullOrWhiteSpace(body.Tool) || string.IsNullOrWhiteSpace(body.RuleId) || string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 2000 || body.ExpiresAt <= DateTime.UtcNow || body.Path is not null && !IngestionValidation.SafePath(body.Path)) throw new ApiException(422, "Exception requires tool/rule, reason, future expiry and a safe optional path");
            var row = new ProfileException { ProjectKey = key, Tool = body.Tool, RuleId = body.RuleId, Path = body.Path, Reason = body.Reason, ExpiresAt = body.ExpiresAt.ToUniversalTime() ,
                };
            db.Add(row); audit.Log(user.Email, user.Role, "profile.exception", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct); return Results.Created($"/api/v1/profiles/projects/{key}/exceptions", new { row.Id });
        });
        return app;
    }

    private static object RuleActivationResponse(string projectKey, ProfileDefinition previous, ProfileDefinition current, RuleActivationRequest request, string enforcement) => new
    {
        project_key = projectKey,
        profile_id = current.ProfileId,
        previous_version = previous.Version,
        profile_version = current.Version,
        enforcement,
        tool = request.Tool,
        tool_version = request.ToolVersion,
        rule_id = request.RuleId,
        enabled = request.Enabled,
        status = request.Enabled ? "enabled" : "disabled",
        changes = ExecutableProfiles.Diff(previous, current),
        historical_verdicts_changed = false,
        future_evaluation = "uses_new_profile_and_requires_matching_effective_configuration"
    ,
    };

    private static async Task<string> NextRuleVersionAsync(AppDbContext db, string profileId, string currentVersion, CancellationToken ct)
    {
        const string marker = "-rules.";
        var markerIndex = currentVersion.LastIndexOf(marker, StringComparison.Ordinal);
        var baseVersion = markerIndex > 0 ? currentVersion[..markerIndex] : currentVersion;
        var prefix = baseVersion + marker;
        var versions = await db.Set<ProfileVersion>().Where(row => row.ProfileId == profileId && row.Version.StartsWith(prefix))
            .Select(row => row.Version).ToListAsync(ct);
        var next = versions.Select(version => int.TryParse(version.AsSpan(prefix.Length), out var value) ? value : 0).DefaultIfEmpty().Max() + 1;
        var result = prefix + next;
        if (result.Length > 50) throw new ApiException(422, "Profile version is too long to create a rule activation revision");
        return result;
    }

    public static void Validate(ProfileDefinition profile)
    {
        ExecutableProfiles.Validate(profile);
        if (profile.SecurityReview is null) throw new ApiException(422, "Security review policy must be an object");
        profile.SecurityReview.Validate();
        string[] metrics = ["bugs", "code_smells", "vulnerabilities", "security_hotspots", "coverage", "branch_coverage", "condition_coverage", "duplicate_blocks", "duplicated_lines",
            "duplicated_lines_density",
            "code_lines", "complexity", "dependency_vulnerabilities", "license_violations",
            "security_rating",
            "reliability_rating",
            "maintainability_rating",
            "security_review_rating",
        ];
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || profile.ProfileId.Length > 100 || string.IsNullOrWhiteSpace(profile.Version) || profile.Version.Length > 50
            || profile.RequiredTools is null || profile.RequiredTools.Length is < 1 or > 100 || profile.RequiredTools.Any(string.IsNullOrWhiteSpace)
            || profile.RequiredMetrics is null || profile.RequiredMetrics.Except(metrics).Any() || profile.Rules is null || profile.Rules.Length > 10000
            || profile.Conditions is null || profile.Conditions.Length > 100 || profile.Conditions.Any(c => c is null || c.Scope is not ("global" or "new_code") || c.Scope == "new_code" && c.Metric is ("dependency_vulnerabilities" or "license_violations" or "code_lines" or "complexity") || !metrics.Contains(c.Metric) || c.Operator is not ("min" or "max") || !double.IsFinite(c.Threshold) || c.Threshold < 0|| c.Metric.EndsWith("_rating", StringComparison.Ordinal)
                    && c.Threshold is < 1 or > 5
            )
            || profile.Rules.Any(r => string.IsNullOrWhiteSpace(r.Tool) || string.IsNullOrWhiteSpace(r.RuleId) || r.Category is not ("bug" or "code_smell" or "vulnerability" or "security_hotspot" or "secret" or "unknown"))
            || profile.SmallChangesThreshold is < 0 or > 1000
            || profile.Rules.Select(r => (r.Tool, r.RuleId)).Distinct().Count() != profile.Rules.Length
            || profile.AllowedLicenses is null || profile.DeniedLicenses is null || profile.AllowedLicenses.Intersect(profile.DeniedLicenses).Any())
            throw new ApiException(422, "Invalid profile definition, dimensions, conditions or licence policy");
    }
}
