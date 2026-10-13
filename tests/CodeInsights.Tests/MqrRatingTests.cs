using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;

public sealed class MqrRatingTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        var qualities = Quality(codeLines: 100, newLines: 10);
        var review = Review("measured", 100, 1, 1);

        foreach (
            var (severity, expected) in new[]
            {
                ("info", "A"),
                ("low", "B"),
                ("medium", "C"),
                ("high", "D"),
                ("blocker", "E"),
            }
        )
        {
            var profile = Profile(new("R1", "bug", severity));
            var findings =
                severity == "info"
                    ? Array.Empty<FindingOccurrence>()
                    : new[] { Finding("R1", "bug", severity) };
            var ratings = MqrRatings.Calculate(
                profile,
                ExecutableProfiles.BuildRuleIndex(profile),
                Manifest("measured"),
                findings,
                qualities,
                qualities,
                review,
                review,
                null
            );
            Assert.True(
                ratings.Overall.Reliability.Rating == expected
                    && ratings.Overall.Reliability.State == "measured",
                $"Reliability {severity} must map to {expected}"
            );
        }

        var reliable = Profile(new("R1", "bug", "high"));
        var reliableRules = ExecutableProfiles.BuildRuleIndex(reliable);
        Assert.True(
            MqrRatings
                    .Calculate(
                        reliable,
                        reliableRules,
                        Manifest("measured"),
                        [],
                        qualities,
                        qualities,
                        review,
                        review,
                        null
                    )
                    .Overall.Reliability.Rating == "A",
            "A measured analyzer with zero findings must produce A"
        );
        Assert.True(
            MqrRatings
                    .Calculate(
                        reliable,
                        reliableRules,
                        Manifest("failed"),
                        [],
                        qualities,
                        qualities,
                        review,
                        review,
                        null
                    )
                    .Overall.Reliability.State == "failed",
            "A failed analyzer must stay unavailable"
        );
        Assert.True(
            MqrRatings
                    .Calculate(
                        reliable,
                        reliableRules,
                        new IngestionManifest(),
                        [],
                        qualities,
                        qualities,
                        review,
                        review,
                        null
                    )
                    .Overall.Reliability.Reason == "analyzer_not_executed",
            "An absent analyzer must stay unavailable"
        );
        Assert.True(
            MqrRatings
                    .Calculate(
                        reliable,
                        reliableRules,
                        Manifest("measured"),
                        [Finding("R1", "bug", "blocker", excepted: true)],
                        qualities,
                        qualities,
                        review,
                        review,
                        null
                    )
                    .Overall.Reliability.Rating == "A",
            "Accepted exceptions must not affect ratings"
        );
        foreach (var version in new[] { "10.0.201", "11.0.100" })
            Assert.True(MqrRatings.Calculate(reliable, reliableRules, Manifest("measured", version), [Finding("R1", "bug", "high")], qualities, qualities, review, review, null)
                    .Overall.Reliability.Rating == "D",
                $"A catalogued rule must keep its classification with Roslyn {version}");

        var maintainability = Profile(new("S1", "code_smell", "low"));
        var maintainabilityRules = ExecutableProfiles.BuildRuleIndex(maintainability);
        Assert.True(
            MqrRatings
                    .Calculate(
                        maintainability,
                        maintainabilityRules,
                        Manifest("measured"),
                        [Finding("S1", "code_smell", "low")],
                        qualities,
                        qualities,
                        review,
                        review,
                        null
                    )
                    .Overall.Maintainability.Reason == "partial_remediation_model",
            "A partial remediation model must not produce an optimistic rating"
        );
        var modeledMaintainability = maintainability with { RuleCatalog = [maintainability.RuleCatalog[0] with
        {
            Remediation = new("constant", 300, "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1848"),
        }] };
        Assert.True(MqrRatings.Calculate(modeledMaintainability, ExecutableProfiles.BuildRuleIndex(modeledMaintainability), Manifest("measured", "11.0.100"), [Finding("S1", "code_smell", "low")],
                qualities, qualities, review, review, null).Overall.Maintainability.Rating == "A",
            "Remediation is resolved independently of the analyzer version");
        var ambiguous = reliable with { RuleCatalog = [.. reliable.RuleCatalog, reliable.RuleCatalog[0] with { ToolVersion = "11.0.100" }] };
        Assert.True(MqrRatings.Calculate(ambiguous, ExecutableProfiles.BuildRuleIndex(ambiguous), Manifest("measured", "11.0.100"), [Finding("R1", "unknown", "unknown")],
                qualities, qualities, review, review, null).Overall.Reliability.Reason == "ambiguous_rule_catalogue",
            "An old ambiguous catalogue must never select a definition");
        Assert.True(MqrRatings.Calculate(reliable, reliableRules, Manifest("measured", "11.0.100"), [Finding("UNKNOWN", "unknown", "unknown")],
                qualities, qualities, review, review, null).Overall.Reliability.Reason == "unclassified_rules",
            "A genuinely unknown emitted rule keeps its rating unavailable");

        var unavailable = MqrRatings.Calculate(
            reliable,
            reliableRules,
            Manifest("measured"),
            [],
            qualities,
            qualities,
            review,
            review,
            "baseline_absent"
        );
        Assert.True(
            unavailable.NewCode.Reliability.Rating is null
                && unavailable.NewCode.Reliability.Reason == "baseline_absent",
            "A missing baseline must make New Code ratings unavailable with its exact cause"
        );

        var frozen = JsonSerializer.Serialize(unavailable);
        var evolved = Profile(new("R1", "bug", "blocker"));
        _ = MqrRatings.Calculate(
            evolved,
            ExecutableProfiles.BuildRuleIndex(evolved),
            Manifest("measured"),
            [Finding("R1", "bug", "blocker")],
            qualities,
            qualities,
            review,
            review,
            null
        );
        Assert.True(
            JsonSerializer.Serialize(unavailable) == frozen,
            "A captured rating snapshot must remain immutable after a profile evolves"
        );

        var pilot = ProfileEndpoints.BuiltInProfile(
            "upcoop-dotnet",
            GateEvaluation.DotNetMqrProfile
        );
        foreach (
            var ruleId in new[]
            {
                "CA1848",
                "ASP0027",
                "CA1050",
                "CA1068",
                "CA1859",
                "CA1860",
                "CA1861",
                "CA1862",
                "CA1873",
                "CS9236",
                "SYSLIB1045",
                "ASP0015",
                "CA1000",
                "CA1051",
                "CA1824",
                "CS0169",
                "MUD0010",
                "ASP5001",
                "CA2254",
                "CA1806",
                "CA1822",
                "CA1869",
                "CA1829",
                "CA1866",
                "CS8632",
                "AV0011",
                "AV0013",
                "AV0015",
                "AV0016",
                "AV0020",
                "ASPDEPR006",
                "CS0105",
            }
        )
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId);
            Assert.True(
                rule is { Category: "code_smell", Severity: "low", Remediation: not null },
                $"Pilot Roslyn rule {ruleId} must have a complete maintainability classification"
            );
        }
        foreach (var ruleId in new[] { "AZU-0001", "AZU-0015", "AZU-0017" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "trivy");
            Assert.True(
                rule is { Category: "security_hotspot", Severity: "low" },
                $"Trivy Terraform rule {ruleId} must be classified as a security hotspot"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "ASP0000") is { Category: "bug", Severity: "high" },
            "ASP0000 must be classified as a high-severity bug"
        );
        foreach (var ruleId in new[] { "CA2016", "CS0649", "RZ10012", "CS8629", "CS0472", "CS8073", "CS9179" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId);
            Assert.True(
                rule is { Category: "bug", Severity: "medium" },
                $"Pilot Roslyn rule {ruleId} must be classified as a medium-severity bug"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "CA1848").Remediation?.DurationSeconds == 300,
            "CA1848 must use the five-minute constant remediation model"
        );
        foreach (var ruleId in new[] { "SYSLIB0022", "SYSLIB0041", "SYSLIB0051", "SYSLIB0057" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId);
            Assert.True(
                rule is { Category: "vulnerability", Severity: "high" },
                $"Obsolete weak-cryptography diagnostic {ruleId} must be classified as a high-severity vulnerability"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "EF9102") is { Category: "code_smell", Severity: "low", Remediation: not null },
            "EF Core experimental-API diagnostic EF9102 must have a complete maintainability classification"
        );
        Assert.True(
            pilot.RuleCatalog.Select(item => (item.Tool, item.RuleId)).Distinct().Count()
                == pilot.RuleCatalog.Count(),
            "No catalog entry may collide on (tool, ruleId) — that pair, not ruleId alone, is a rule's "
                + "functional identity, since Gitleaks and Trivy legitimately share ~64 rule names "
                + "(e.g. \"private-key\") by convention"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "roslyn") > 600,
            "The bulk-imported NetAnalyzers and C# compiler warning catalogs must dominate the hand-curated entries"
        );
        foreach (var ruleId in new[] { "CS0028", "CS0067", "CS0078", "CS9336" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "roslyn");
            Assert.True(
                rule is { Category: "code_smell", Severity: "low", Remediation.DurationSeconds: 300 },
                $"Bulk-imported C# compiler warning {ruleId} must default to a five-minute code smell"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "CS0472") is { Category: "bug", Severity: "medium" },
            "A hand-curated CS rule found to indicate a real bug must stay excluded from the bulk compiler-warning default"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "CA1001") is { Category: "code_smell", Severity: "low" },
            "A bulk-imported Design-category rule must map to code_smell"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "checkov" && item.RuleId.StartsWith("CKV_AZURE_"))
                > 200,
            "The full Checkov Terraform-Azure checklist must be bulk-imported"
        );
        foreach (var ruleId in new[] { "CKV_AZURE_13", "CKV_AZURE_213", "CKV_OPENAPI_21" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "checkov");
            Assert.True(
                rule is { Category: "security_hotspot", Severity: "low" },
                $"Bulk-imported Checkov rule {ruleId} (observed on Up.France.ODI.Services.Financeur) must be classified"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "checkov" && item.RuleId.StartsWith("CKV2_AZURE_"))
                > 50,
            "The Checkov Terraform-Azure graph checks must be bulk-imported alongside the resource checks"
        );
        foreach (var ruleId in new[] { "CKV2_AZURE_1", "CKV2_AZURE_21", "CKV2_AZURE_26", "CKV2_AZURE_32", "CKV2_AZURE_33", "CKV2_AZURE_57", "CKV_AZURE_23" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "checkov");
            Assert.True(
                rule is { Category: "security_hotspot", Severity: "low" },
                $"Bulk-imported Checkov graph check {ruleId} (observed on Up.France.CodeInsights) must be classified"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "checkov" && (item.RuleId.StartsWith("CKV_DOCKER_") || item.RuleId.StartsWith("CKV2_DOCKER_")))
                == 28,
            "The full Checkov Dockerfile checklist (CKV_DOCKER_* + CKV2_DOCKER_*) must be bulk-imported"
        );
        foreach (var ruleId in new[] { "CKV_DOCKER_2", "CKV_DOCKER_8", "CKV2_DOCKER_1" })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "checkov");
            Assert.True(
                rule is { Category: "security_hotspot", Severity: "low", Language: "dockerfile" },
                $"Bulk-imported Checkov Dockerfile rule {ruleId} (observed on Up.France.ODI.Uptimise.API) must be classified"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "trivy" && item.RuleId.StartsWith("DS-")) == 28,
            "The full Trivy Dockerfile misconfiguration checklist must be bulk-imported"
        );
        foreach (var (ruleId, severity) in new[] { ("DS-0002", "high"), ("DS-0026", "low"), ("DS-0029", "high") })
        {
            var rule = pilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "trivy");
            Assert.True(
                rule is { Category: "security_hotspot", Language: "dockerfile" } && rule.Severity == severity,
                $"Bulk-imported Trivy Dockerfile rule {ruleId} (observed on Up.France.ODI.Uptimise.API) must preserve its real severity"
            );
        }
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "roslyn" && item.RuleId.StartsWith("IDE")) == 129,
            "The full Roslyn IDE style-rules index must be bulk-imported"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "IDE0028") is { Category: "code_smell", Severity: "low", Remediation: not null },
            "Bulk-imported Roslyn IDE rule IDE0028 (observed on Up.France.ODI.Uptimise.API) must have a complete maintainability classification"
        );
        Assert.True(
            !pilot.RuleCatalog.Any(item => item.RuleId == "IDE3000"),
            "IDE3000 (Implement with Copilot) is a VS action trigger, not a real diagnostic, and must not be imported"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.RuleId.StartsWith("ASP0") && item.RuleId.Length == 7) >= 26,
            "The full ASP.NET Core analyzer DiagnosticDescriptors must be bulk-imported"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "ASP0025") is { Category: "code_smell", Severity: "low" },
            "Bulk-imported ASP rule ASP0025 (observed on Up.France.ODI.Uptimise.API, Usage/Info) must be classified as a code smell"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "ASP0008") is { Category: "bug", Severity: "high" },
            "Bulk-imported ASP rule ASP0008 (Usage/Error) must be classified as a high-severity bug"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "ASP0026") is { Category: "vulnerability", Severity: "medium" },
            "Bulk-imported ASP rule ASP0026 (Security/Warning, the only Security-category ASP rule) must be classified as a vulnerability"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "EF1001") is { Category: "bug", Severity: "medium" },
            "Bulk-imported EF Core rule EF1001 (observed on Up.France.ODI.Services.Beneficiaire, Usage/Warning) must be classified as a medium-severity bug"
        );
        foreach (var ruleId in new[] { "EF1002", "EF1003" })
        {
            Assert.True(
                pilot.RuleCatalog.Single(item => item.RuleId == ruleId) is { Category: "vulnerability", Severity: "medium" },
                $"Bulk-imported EF Core raw-SQL rule {ruleId} (Security/Warning) must be classified as a vulnerability"
            );
        }
        Assert.True(
            !pilot.RuleCatalog.Any(item => item.RuleId == "EF1000"),
            "EF1000 was removed from EFCore.Analyzers and must not be imported"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.RuleId.StartsWith("SYSLIB")) >= 190,
            "The full dotnet/runtime SYSLIB diagnostic list must be bulk-imported"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "SYSLIB0014") is { Category: "code_smell", Severity: "low", Remediation: not null },
            "Bulk-imported SYSLIB obsoletion SYSLIB0014 must have a complete maintainability classification"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "trivy" && item.RuleId.StartsWith("AZU-")) > 60,
            "The full Trivy Azure Terraform misconfiguration checklist must be bulk-imported"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "AZU-0004" && item.Tool == "trivy")
                is { Category: "security_hotspot", Severity: "high" },
            "Trivy's own per-rule severity metadata (AZU-0004 is CRITICAL) must be preserved, not defaulted"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "CA2100") is { Category: "vulnerability", Severity: "high" },
            "A bulk-imported Security-category rule (SQL injection) must map to vulnerability"
        );
        Assert.True(
            pilot.RuleCatalog.Single(item => item.RuleId == "CA2000") is { Category: "bug", Severity: "medium" },
            "A bulk-imported Reliability-category rule must map to bug"
        );
        var mscRatings = MqrRatings.Calculate(pilot, ExecutableProfiles.BuildRuleIndex(pilot), Manifest("measured", "10.0.201"),
            [Finding("ASP0000", "bug", "high"), Finding("CA1848", "code_smell", "low")],
            qualities, qualities, review, review, null);
        Assert.True(mscRatings.Overall.Reliability is { State: "measured", Rating: "D" }
                && mscRatings.Overall.Maintainability.State == "measured",
            "ASP0000 and CA1848 must make MSC reliability and maintainability calculable");
        Assert.True(
            pilot.RuleCatalog.Any(item => item.RuleId == "dotnet.permissive-cors"),
            "The pilot profile must use the stable Semgrep rule identifier"
        );

        var reactPilot = ProfileEndpoints.BuiltInProfile("upcoop-react", GateEvaluation.ReactMqrProfile);
        Assert.True(
            reactPilot.RuleCatalog.Single(item => item.RuleId == "eqeqeq") is { Category: "bug", Severity: "low" },
            "eqeqeq must be classified as a low-severity bug"
        );
        Assert.True(
            reactPilot.RuleCatalog.Single(item => item.RuleId == "@typescript-eslint/no-explicit-any")
                is { Category: "code_smell", Remediation.DurationSeconds: 300 },
            "no-explicit-any must be a code smell with a five-minute constant remediation model"
        );
        foreach (var (tool, ruleId) in new[]
                 {
                     ("gitleaks", "generic-api-key"), ("gitleaks", "jwt"), ("gitleaks", "private-key"),
                     ("trivy", "private-key"),
                 })
        {
            var rule = reactPilot.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == tool);
            Assert.True(
                rule is { Category: "secret", Severity: "medium" },
                $"{tool} secret-detection rule {ruleId} must be classified as a secret finding"
            );
        }
        Assert.True(
            reactPilot.RuleCatalog.Select(item => (item.Tool, item.RuleId)).Distinct().Count()
                == reactPilot.RuleCatalog.Count(),
            "The bulk-imported Gitleaks/Trivy secret catalogs must not collide with any hand-curated rule"
        );
        Assert.True(
            !reactPilot.RuleCatalog.Any(item => item.Category == "security_hotspot")
                && reactPilot.SecurityReview.MinimumHotspotReviewPercent is null,
            "upcoop-react/2 has no security_hotspot rule to review, so SecurityReview must stay "
                + "disabled rather than show an unresolvable \"-\" forever"
        );
        Assert.True(
            reactPilot.RuleCatalog.Count(item => item.Tool == "gitleaks") > 200,
            "The full Gitleaks default ruleset must be bulk-imported"
        );
        Assert.True(
            pilot.RuleCatalog.Count(item => item.Tool == "gitleaks") > 200
                && pilot.RuleCatalog.Single(item => item.Tool == "gitleaks" && item.RuleId == "private-key")
                    is { Category: "secret", Severity: "medium" },
            "upcoop-dotnet/2 must carry the same Gitleaks bulk import as React, proactively — it hasn't fired here yet but scans the same way"
        );
        foreach (var checkovSecretPilot in new[] { (Name: "upcoop-dotnet/2", Profile: pilot), (Name: "upcoop-react/2", Profile: reactPilot) })
        {
            Assert.True(
                checkovSecretPilot.Profile.RuleCatalog.Count(item => item.Tool == "checkov" && item.RuleId.StartsWith("CKV_SECRET_")) == 19,
                $"{checkovSecretPilot.Name} must carry the full stable Checkov secrets checklist (CKV_SECRET_1..19)"
            );
            foreach (var ruleId in new[] { "CKV_SECRET_3", "CKV_SECRET_6" })
            {
                var rule = checkovSecretPilot.Profile.RuleCatalog.SingleOrDefault(item => item.RuleId == ruleId && item.Tool == "checkov");
                Assert.True(
                    rule is { Category: "secret", Severity: "medium" },
                    $"Bulk-imported Checkov secret rule {ruleId} (observed on Up.France.ODI.Uptimise.BMSC) must be classified as a secret finding on {checkovSecretPilot.Name}"
                );
            }
        }
        var reactManifest = new IngestionManifest
        {
            Reports =
            [
                new("eslint", "sarif", "2.1.0", "eslint", "9.39.2", "measured", 1, new string('a', 64)),
            ],
        };
        var reactRatings = MqrRatings.Calculate(
            reactPilot,
            ExecutableProfiles.BuildRuleIndex(reactPilot),
            reactManifest,
            [
                new()
                {
                    ReportId = "eslint",
                    Tool = "eslint",
                    RuleId = "eqeqeq",
                    Category = "bug",
                    Severity = "low",
                    ComparisonState = "new",
                },
                new()
                {
                    ReportId = "eslint",
                    Tool = "eslint",
                    RuleId = "@typescript-eslint/no-explicit-any",
                    Category = "code_smell",
                    Severity = "low",
                    ComparisonState = "new",
                },
            ],
            qualities,
            qualities,
            review,
            review,
            null
        );
        Assert.True(
            reactRatings.Overall.Reliability.State == "measured"
                && reactRatings.Overall.Maintainability.State == "measured",
            "Real observed ESLint rules (eqeqeq, no-explicit-any) must make React reliability and maintainability calculable"
        );

        var phpPilot = ProfileEndpoints.BuiltInProfile("upcoop-php", GateEvaluation.Catalog["upcoop-php"]);
        ProfileEndpoints.Validate(phpPilot);
        Assert.True(
            phpPilot.Conditions.Any(condition => condition.Metric.EndsWith("_rating", StringComparison.Ordinal))
                && phpPilot.Rules.Length == 0
                && !phpPilot.RequiredMetrics.Contains("branch_coverage"),
            "upcoop-php/1 is an MQR profile: rating conditions, catalogue-only classification, no branch coverage"
        );
        // Every identifier observed on the LogiwebServices (283004) and CardsServices (283076) pilots.
        foreach (var (ruleId, category) in new[]
                 {
                     ("argument.type", "bug"), ("arguments.count", "bug"), ("assign.propertyType", "bug"),
                     ("class.notFound", "bug"), ("interface.notFound", "bug"), ("larastan.relationExistence", "bug"),
                     ("method.nonObject", "bug"), ("method.notFound", "bug"), ("method.staticCall", "bug"),
                     ("offsetAccess.invalidOffset", "bug"), ("property.notFound", "bug"), ("return.missing", "bug"),
                     ("return.type", "bug"), ("return.void", "bug"), ("staticMethod.notFound", "bug"),
                     ("variable.undefined", "bug"),
                     ("arrayFilter.same", "code_smell"), ("booleanAnd.alwaysFalse", "code_smell"),
                     ("booleanAnd.leftAlwaysTrue", "code_smell"), ("booleanNot.alwaysFalse", "code_smell"),
                     ("booleanNot.alwaysTrue", "code_smell"), ("catch.neverThrown", "code_smell"),
                     ("class.nameCase", "code_smell"), ("deadCode.unreachable", "code_smell"),
                     ("empty.variable", "code_smell"), ("function.alreadyNarrowedType", "code_smell"),
                     ("generics.notSubtype", "code_smell"), ("greaterOrEqual.alwaysTrue", "code_smell"),
                     ("identical.alwaysFalse", "code_smell"), ("if.alwaysFalse", "code_smell"),
                     ("isset.offset", "code_smell"), ("larastan.noEnvCallsOutsideOfConfig", "code_smell"),
                     ("larastan.noUnnecessaryCollectionCall", "code_smell"), ("method.unused", "code_smell"),
                     ("method.void", "code_smell"), ("nullCoalesce.expr", "code_smell"),
                     ("nullCoalesce.offset", "code_smell"), ("nullCoalesce.variable", "code_smell"),
                     ("nullsafe.neverNull", "code_smell"), ("parameter.defaultValue", "code_smell"),
                     ("parameter.notFound", "code_smell"), ("parameter.phpDocType", "code_smell"),
                     ("parameter.requiredAfterOptional", "code_smell"), ("phpDoc.parseError", "code_smell"),
                     ("property.onlyWritten", "code_smell"), ("property.phpDocType", "code_smell"),
                     ("property.unused", "code_smell"), ("property.unusedType", "code_smell"),
                     ("return.phpDocType", "code_smell"), ("return.unusedType", "code_smell"),
                     ("throws.notThrowable", "code_smell"), ("trait.unused", "code_smell"),
                     ("phpstan.unidentified", "code_smell"),
                 })
        {
            var rule = phpPilot.RuleCatalog.SingleOrDefault(item => item.Tool == "phpstan" && item.RuleId == ruleId);
            Assert.True(
                rule is not null && rule.Category == category && rule.Remediation is { DurationSeconds: > 0 }
                    && rule.Severity == (category == "bug" ? "medium" : "low"),
                $"PHPStan identifier {ruleId} observed on a PHP pilot must be catalogued as {category} with a remediation cost"
            );
        }
        foreach (var (tool, ruleId, category) in new[]
                 {
                     ("gitleaks", "generic-api-key", "secret"), ("gitleaks", "azure-ad-client-secret", "secret"),
                     ("trivy", "azure-entra-client-secret", "secret"), ("trivy", "DS-0002", "security_hotspot"),
                     ("trivy", "DS-0026", "security_hotspot"), ("trivy", "DS-0029", "security_hotspot"),
                     ("checkov", "CKV_DOCKER_2", "security_hotspot"), ("checkov", "CKV_DOCKER_3", "security_hotspot"),
                     ("checkov", "CKV2_DOCKER_17", "security_hotspot"), ("checkov", "CKV_OPENAPI_4", "security_hotspot"),
                     ("checkov", "CKV_OPENAPI_5", "security_hotspot"), ("checkov", "CKV_OPENAPI_21", "security_hotspot"),
                 })
        {
            Assert.True(
                phpPilot.RuleCatalog.SingleOrDefault(item => item.Tool == tool && item.RuleId == ruleId)?.Category == category,
                $"{tool} rule {ruleId} observed on a PHP pilot must be catalogued as {category}"
            );
        }
        Assert.True(
            phpPilot.RuleCatalog.SingleOrDefault(item => item.Tool == "semgrep" && item.RuleId == "php.lang.security.assert-use.assert-use")
                    is { Category: "vulnerability" }
                && phpPilot.RuleCatalog.Count(item => item.Tool == "semgrep" && item.Category == "vulnerability") > 100,
            "Semgrep p/ci and p/php rules must give the Security rating applicable vulnerability rules"
        );
        Assert.True(
            phpPilot.RuleCatalog.Count(item => item.Tool == "phpstan") > 1100
                && phpPilot.RuleCatalog.Select(item => (item.Tool, item.RuleId)).Distinct().Count() == phpPilot.RuleCatalog.Length,
            "The full PHPStan/Larastan identifier list must be bulk-imported without colliding with the security catalogues"
        );
        var phpRatings = MqrRatings.Calculate(
            phpPilot,
            ExecutableProfiles.BuildRuleIndex(phpPilot),
            new IngestionManifest
            {
                Reports = [new("phpstan", "sarif", "2.1.0", "phpstan", "2.3.1", "measured", 1, new string('a', 64))],
            },
            [
                new()
                {
                    ReportId = "phpstan",
                    Tool = "phpstan",
                    RuleId = "class.notFound",
                    Category = "bug",
                    Severity = "medium",
                    ComparisonState = "new",
                },
                new()
                {
                    ReportId = "phpstan",
                    Tool = "phpstan",
                    RuleId = "phpDoc.parseError",
                    Category = "code_smell",
                    Severity = "low",
                    ComparisonState = "new",
                },
            ],
            qualities,
            qualities,
            review,
            review,
            null
        );
        Assert.True(
            phpRatings.Overall.Reliability is { State: "measured", Rating: "C" }
                && phpRatings.Overall.Maintainability.State == "measured",
            "Observed PHPStan rules must make PHP reliability (medium bug: C) and maintainability calculable"
        );

        Console.WriteLine("MQR rating, availability, exception and snapshot checks passed.");
    }

    private static ProfileDefinition Profile(TestRule rule) =>
        new()
        {
            ProfileId = "mqr-check",
            Version = "1",
            RuleCatalog =
            [
                new(
                    "roslyn",
                    "10.0.104",
                    rule.Id,
                    "csharp",
                    rule.Category,
                    JsonSerializer.SerializeToElement(new { }),
                    Severity: rule.Severity
                ),
            ],
        };

    private static IngestionManifest Manifest(string state, string toolVersion = "10.0.104") =>
        new()
        {
            Reports =
            [
                new(
                    "roslyn",
                    "sarif",
                    "2.1.0",
                    "roslyn",
                    toolVersion,
                    state,
                    1,
                    new string('a', 64)
                ),
            ],
        };

    private static FindingOccurrence Finding(
        string rule,
        string category,
        string severity,
        bool excepted = false
    ) =>
        new()
        {
            ReportId = "roslyn",
            Tool = "roslyn",
            RuleId = rule,
            Category = category,
            Severity = severity,
            ComparisonState = "new",
            Excepted = excepted,
        };

    private static CodeQualityTotals Quality(long codeLines, long newLines) =>
        new(
            new("measured", 1, 1, 100),
            new("not_applicable", 0, 0, null),
            new("not_applicable", 0, 0, null),
            codeLines,
            null,
            "measured",
            0,
            0,
            new("measured", 0, codeLines, 0),
            newLines
        );

    private static SecurityReviewResult Review(
        string state,
        double? value,
        int numerator,
        int denominator
    ) =>
        new(
            "overall",
            state,
            state == "measured" ? "pass" : "not_applicable",
            value,
            numerator,
            denominator,
            100,
            null,
            [],
            [],
            DateTime.UnixEpoch,
            []
        );

private sealed record TestRule(string Id, string Category, string Severity);
}
