using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;

public sealed class NewCodeGateTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        var diff = new GitDiff
        {
            State = "available",
            BaseSha = "aaaaaa",
            HeadSha = "bbbbbb",
            Files = [new("code.cs", null, Enumerable.Range(1, 150).ToArray(), [])],
        };
        var profile = new ProfileDefinition
        {
            ProfileId = "checks",
            Version = "1",
            RequiredTools = ["scanner"],
            Conditions = [new("bugs", "max", 0, "new_code")],
        };
        var metrics = new Dictionary<string, double?> { ["bugs"] = 500, ["coverage"] = 50 };
        var states = new Dictionary<string, string>
        {
            ["bugs"] = "measured",
            ["coverage"] = "measured",
        };
        IngestionProcessor.ConditionResult Evaluate(string comparison, bool measured = true, CodeQualityTotals? newQuality = null) =>
            IngestionProcessor
                .EvaluateConditions(
                    profile,
                    metrics,
                    states,
                    JsonSerializer.Deserialize<JsonElement>(comparison),
                    diff,
                    1,
                    2,
                    measured,
                    newQuality: newQuality
                )
                .Single();
        string Compare(
            List<FindingOccurrence> findings,
            Dictionary<(string Path, int Line), bool>? coverage = null
        ) => IngestionProcessor.Compare(diff, [], findings, coverage ?? [], "baseline");
        Assert.True(
            Evaluate(Compare([])).Result == "pass",
            "historical debt does not block a clean new-code gate"
        );
        var many = Enumerable
            .Range(1, 150)
            .Select(n => new FindingOccurrence
            {
                Tool = "scanner",
                RuleId = "R",
                Category = "bug",
                Path = "code.cs",
                Line = n,
            })
            .ToList();
        var counted = Evaluate(Compare(many));
        Assert.True(
            counted.Value == 150 && counted.Numerator == 150 && counted.Result == "fail",
            "exact new-code count exceeds the 100 detail limit"
        );
        Assert.True(
            counted.BaseSha == "aaaaaa"
                && counted.HeadSha == "bbbbbb"
                && counted.BaselineId == "baseline",
            "frozen diff provenance"
        );
        Assert.True(
            Evaluate("{\"state\":\"unavailable\",\"reason\":\"profiles_incompatible\"}").Result
                == "unknown",
            "incompatible baseline never passes"
        );
        many[0].Line = null;
        Assert.True(
            Evaluate(Compare(many)).Result == "unknown",
            "ambiguous matching cannot produce a measured new count"
        );
        metrics["bugs"] = null;
        Assert.True(Evaluate(Compare([])).Result == "unknown", "missing measurement never becomes zero");
        profile = profile with { Conditions = [new("coverage", "min", 80, "new_code")] };
        var emptyRatio = new CodeRatio("not_applicable", 0, 0, null);
        var smallChange = new CodeQualityTotals(emptyRatio, emptyRatio, emptyRatio, 0, 0, "not_applicable", 0, 0, emptyRatio, 15);
        profile = profile with { SmallChangesThreshold = 20 };
        var smallChangeCondition = Evaluate(Compare([]), newQuality: smallChange);
        Assert.True(
            smallChangeCondition.State == "not_applicable" && smallChangeCondition.Result == "not_applicable",
            "small changes keep coverage explicitly not applicable"
        );
        var na = Evaluate(Compare([], new() { [("code.cs", 151)] = true }));
        Assert.True(
            na.Result == "not_applicable"
                && na.Value is null
                && na.Numerator == 0
                && na.Denominator == 0,
            "no changed executable line is explicitly not applicable"
        );
        Assert.True(
            Evaluate(Compare([]), false).Result == "unknown",
            "missing coverage is not not-applicable"
        );
        Assert.True(
            Evaluate(Compare([])).Result == "not_applicable",
            "a measured report with no changed executable line is not applicable"
        );
        Assert.True(
            Evaluate(
                IngestionProcessor.Compare(diff with { Files = [] }, [], [], [], "baseline")
            ).Result == "not_applicable",
            "diff without additions has no applicable executable lines"
        );
        var partialDiff = diff with { Files = [.. diff.Files, new("docs/readme.md", null, [1], [])] };
        Assert.True(
            Evaluate(
                IngestionProcessor.Compare(
                    partialDiff,
                    [],
                    [],
                    new() { [("code.cs", 1)] = true },
                    "baseline"
                )
            ).Result == "pass",
            "documentation outside the executable coverage scope does not invalidate measured code"
        );
        var coverageResult = Evaluate(
            Compare(
                [],
                new()
                {
                    [("code.cs", 1)] = true,
                    [("code.cs", 2)] = false,
                    [("old.cs", 1)] = true,
                }
            )
        );
        Assert.True(
            coverageResult.Value == 50
                && coverageResult.Numerator == 1
                && coverageResult.Denominator == 2
                && coverageResult.Result == "fail",
            "coverage uses changed executable lines only"
        );
        var mqrProfile = profile with
        {
            SmallChangesThreshold = null,
            Conditions =
            [
                new("security_rating", "max", 1, "new_code"),
                new("coverage", "min", 80, "new_code"),
            ],
        };
        var mqrCoverage = IngestionProcessor.EvaluateConditions(
            mqrProfile,
            metrics,
            states,
            JsonSerializer.Deserialize<JsonElement>(Compare([])),
            diff,
            0,
            0,
            true,
            newQuality: emptyRatio is { } ? new CodeQualityTotals(emptyRatio, emptyRatio, emptyRatio, 0, 0, "not_applicable", 0, 0, emptyRatio, 0) : null
        ).Single(x => x.Metric == "coverage");
        Assert.True(
            mqrCoverage.Result == "not_applicable" && mqrCoverage.Reason == "no_changed_executable_lines",
            "MQR coverage keeps an empty executable diff not applicable"
        );
        profile = profile with { Conditions = [new("bugs", "max", 0)] };
        metrics["bugs"] = 500;
        Assert.True(Evaluate(Compare([])).Result == "fail", "omitted scope retains global behavior");
        var currentProfileJson = JsonSerializer.Serialize(profile, ApiJson.Options);
        var oldProfileJson = currentProfileJson.Replace(",\"scope\":\"global\"", "");
        Assert.True(
            oldProfileJson != currentProfileJson
                && IngestionProcessor.ProfilesCompatible(oldProfileJson, currentProfileJson),
            "pre-scope snapshots are compatible with explicit global defaults"
        );
        Assert.True(
            !IngestionProcessor.ProfilesCompatible(
                oldProfileJson,
                JsonSerializer.Serialize(
                    profile with
                    {
                        Conditions = [new("bugs", "max", 0, "new_code")],
                    },
                    ApiJson.Options
                )
            ),
            "different scope is incompatible"
        );
        Assert.True(
            !IngestionProcessor.ProfilesCompatible(
                oldProfileJson,
                JsonSerializer.Serialize(profile with { Version = "2" }, ApiJson.Options)
            ),
            "different profile version is incompatible"
        );
        var versionedProfile = profile with
        {
            RuleCatalog = [new("roslyn", "10.0.104", "CA1848", "csharp", "code_smell", JsonSerializer.SerializeToElement(new { }))],
            AnalyzerConfigurations = [new("roslyn", "10.0.104", "editorconfig", ".editorconfig", "[*.cs]\n")],
            DisabledRules = [new("roslyn", "10.0.104", "CA1848")],
        };
        var newerProducerProfile = versionedProfile with
        {
            RuleCatalog = [versionedProfile.RuleCatalog[0] with { ToolVersion = "11.0.100" }],
            AnalyzerConfigurations = [versionedProfile.AnalyzerConfigurations[0] with { ToolVersion = "11.0.100" }],
            DisabledRules = [versionedProfile.DisabledRules[0] with { ToolVersion = "11.0.100" }],
        };
        Assert.True(IngestionProcessor.ProfilesCompatible(JsonSerializer.Serialize(versionedProfile, ApiJson.Options),
                JsonSerializer.Serialize(newerProducerProfile, ApiJson.Options)),
            "tool versions are provenance and do not invalidate a New Code baseline");
        Assert.True(!IngestionProcessor.ProfilesCompatible(JsonSerializer.Serialize(versionedProfile, ApiJson.Options),
                JsonSerializer.Serialize(versionedProfile with
                {
                    RuleCatalog = [.. versionedProfile.RuleCatalog, versionedProfile.RuleCatalog[0] with { RuleId = "ASP0000" }],
                }, ApiJson.Options)),
            "a catalogue policy change invalidates the New Code baseline even within the same profile version");
        var baselineProducer = new IngestionManifest
        {
            Reports = [new("r", "sarif", "2.1.0", "roslyn", "10.0.104", "measured", 1, "")],
        };
        Assert.True(IngestionProcessor.ProducersCompatible(baselineProducer,
                baselineProducer with { Reports = [baselineProducer.Reports[0] with { ToolVersion = "11.0.100" }] }),
            "producer compatibility ignores tool version changes");
        Assert.True(!IngestionProcessor.ProducersCompatible(baselineProducer,
                baselineProducer with { Reports = [baselineProducer.Reports[0] with { FormatVersion = "2.2.0" }] }),
            "producer compatibility keeps format version changes incompatible");
        var classifier = profile with { Rules = [new("bugs-scanner", "R", "bug")] };
        var classifierRules = ExecutableProfiles.BuildRuleIndex(classifier);
        var manifest = new IngestionManifest
        {
            Reports = [new("r", "sarif", "2.1.0", "other-scanner", "1", "measured", 1, "")],
        };
        Assert.True(
            IngestionProcessor.FindingCount(classifier, classifierRules, manifest, [], true, "bug") is null,
            "unrelated SARIF cannot stand in for a missing category producer"
        );
        manifest = manifest with { Reports = [manifest.Reports[0] with { Tool = "bugs-scanner" }] };
        Assert.True(
            IngestionProcessor.FindingCount(classifier, classifierRules, manifest, [], true, "bug") == 0,
            "empty SARIF from the actual category producer measures zero"
        );
        var cataloguedCounter = profile with
        {
            RuleCatalog = [new("roslyn", "10.0.104", "ASP0000", "csharp", "bug", JsonSerializer.SerializeToElement(new { }))],
        };
        var cataloguedCounterRules = ExecutableProfiles.BuildRuleIndex(cataloguedCounter);
        manifest = manifest with { Reports = [new("r", "sarif", "2.1.0", "roslyn", "11.0.100", "measured", 1, "")] };
        Assert.True(IngestionProcessor.FindingCount(cataloguedCounter, cataloguedCounterRules, manifest, [], true, "bug") == 0,
            "category counters accept any observed analyzer version");
        var ambiguousCounter = cataloguedCounter with
            {
                RuleCatalog = [.. cataloguedCounter.RuleCatalog, cataloguedCounter.RuleCatalog[0] with { ToolVersion = "11.0.100" }],
            };
        Assert.True(IngestionProcessor.FindingCount(ambiguousCounter, ExecutableProfiles.BuildRuleIndex(ambiguousCounter), manifest, [], true, "bug") is null,
            "an ambiguous legacy catalogue cannot produce a measured counter");
        var packaged = ProfileEndpoints.BuiltInProfile(
            "upcoop-security",
            GateEvaluation.Catalog["upcoop-security"]
        );
        ProfileEndpoints.Validate(packaged);
        Assert.True(
            packaged.RequiredTools.SequenceEqual(["semgrep", "trivy", "gitleaks", "checkov"])
                && packaged.RequiredMetrics.SequenceEqual(
                    ["security_hotspots", "vulnerabilities"]
                )
                && packaged.Rules.All(rule => rule.RuleId == "*")
                && packaged.Conditions.Length == 2,
            "packaged security profile has explicit producers, metrics and thresholds"
        );
        var dotnet = ProfileEndpoints.BuiltInProfile(
            "upcoop-dotnet",
            GateEvaluation.Catalog["upcoop-dotnet"]
        );
        ProfileEndpoints.Validate(dotnet);
        Assert.True(
            dotnet.RequiredTools.Contains("roslyn-code-smells")
                && dotnet.RequiredTools.Contains("dotnet-coverage")
                && dotnet.Rules.Any(rule =>
                    rule.Tool == "roslyn-code-smells" && rule.Category == "code_smell"
                )
                && dotnet.Rules.Any(rule => rule.Tool == "roslyn-bugs" && rule.Category == "bug")
                && !dotnet.RequiredMetrics.Contains("license_violations"),
            "packaged .NET profile measures quality without inventing a license policy"
        );
        foreach (var (id, qualityTool, coverageTool, complexityTool, branchCoverage, conditionCoverage) in new[]
        {
            ("upcoop-react", "eslint", "javascript-coverage", "lizard", true, false),
            ("upcoop-python", "ruff", "python-coverage", "lizard", true, true),
            ("upcoop-flutter", "dart-analyze", "flutter-coverage", "dart-complexity", true, false),
        })
        {
            var stack = ProfileEndpoints.BuiltInProfile(id, GateEvaluation.Catalog[id]);
            ProfileEndpoints.Validate(stack);
            Assert.True(
                stack.RequiredTools.Contains(qualityTool)
                    && stack.RequiredTools.Contains(coverageTool)
                    && stack.RequiredTools.Contains("jscpd")
                    && stack.RequiredTools.Contains("cloc")
                    && stack.RequiredTools.Contains(complexityTool)
                    && stack.Rules.Any(rule => rule.Tool == qualityTool && rule.Category == "code_smell")
                    && stack.RequiredMetrics.Contains("branch_coverage") == branchCoverage
                    && stack.RequiredMetrics.Contains("condition_coverage") == conditionCoverage
                    && !stack.RequiredMetrics.Contains("license_violations"),
                $"packaged {id} profile has stack-native quality producers"
            );
        }
        foreach (
            var condition in new[]
            {
                new ProfileCondition("bugs", "max", 0, "invalid"),
                new ProfileCondition("license_violations", "max", 0, "new_code"),
            }
        )
        {
            try
            {
                ProfileEndpoints.Validate(profile with { Conditions = [condition] });
                throw new InvalidOperationException("Invalid scope accepted");
            }
            catch (ApiException ex) when (ex.StatusCode == 422) { }
        }
        Console.WriteLine("New-code gate checks passed.");
    }

}
