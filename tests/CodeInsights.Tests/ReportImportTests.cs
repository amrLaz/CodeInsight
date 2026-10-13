using System.Text;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;

public sealed class ReportImportTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var sarif = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"example","rules":[{"id":"RULE1"}]}},"results":[{"ruleIndex":0,"level":"error","message":{"text":"Example"},"partialFingerprints":{"stable":"abc"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":4}}}]}]}]}
            """;
        var parsed = await Parse(sarif, "sarif", "2.1.0");
        Assert.True(
            parsed.Findings
                is [
                    {
                        RuleId: "RULE1",
                        OriginalSeverity: "error",
                        Severity: "high",
                        Category: "unknown",
                        Path: "src/a.cs",
                        Line: 4
                    },
                ],
            "SARIF provenance and unclassified rules"
        );
        Assert.True(parsed.Findings[0].Fingerprint!.Contains("abc"), "SARIF stable fingerprint");
        const string sarifZeroLine =
            """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"example","rules":[{"id":"RULE1"}]}},"results":[{"ruleIndex":0,"level":"error","message":{"text":"Example"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":0}}}]}]}]}
            """;
        var zeroLineParsed = await Parse(sarifZeroLine, "sarif", "2.1.0");
        Assert.True(
            zeroLineParsed.Findings is [{ RuleId: "RULE1", Path: "src/a.cs", Line: null }],
            "A producer reporting startLine 0 (no precise span) is unmeasured, not fatal"
        );
        // Trivy's "fs --scanners vuln,misconfig,secret" interleaves dependency-CVE/GHSA results
        // into the same SARIF run as its misconfiguration findings. The CVE/GHSA ones are an
        // unbounded, non-enumerable ID space unlike every cataloged rule -- they must be dropped
        // here (the CycloneDX SBOM already feeds them as real ImportedVulnerability entries), not
        // left to show up as permanently-unclassified "unknown" findings.
        const string trivyMixedSarif = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"trivy","rules":[{"id":"CVE-2026-50648"},{"id":"GHSA-g4vj-cjjj-v7hg"},{"id":"AZU-0004"}]}},"results":[{"ruleIndex":0,"level":"error","message":{"text":"vuln"}},{"ruleIndex":1,"level":"warning","message":{"text":"vuln"}},{"ruleIndex":2,"level":"error","message":{"text":"misconfig"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"main.tf"},"region":{"startLine":1}}}]}]}]}
            """;
        var trivyMixedParsed = await Parse(trivyMixedSarif, "sarif", "2.1.0");
        Assert.True(
            trivyMixedParsed.Findings is [{ RuleId: "AZU-0004" }],
            "Trivy CVE/GHSA dependency-advisory results are excluded from SARIF findings; only the misconfiguration result survives"
        );
        // Same ID shape from a non-Trivy producer is not excluded -- the filter is scoped to the
        // tool, not just the ID pattern, since nothing else is known to reuse CVE/GHSA as a ruleId.
        const string nonTrivyCveShapedSarif = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"example","rules":[{"id":"CVE-2026-50648"}]}},"results":[{"ruleIndex":0,"level":"error","message":{"text":"not trivy"}}]}]}
            """;
        var nonTrivyCveShapedParsed = await Parse(nonTrivyCveShapedSarif, "sarif", "2.1.0");
        Assert.True(
            nonTrivyCveShapedParsed.Findings is [{ RuleId: "CVE-2026-50648" }],
            "The CVE/GHSA exclusion is scoped to tool \"trivy\", not applied to any producer using a similarly-shaped ruleId"
        );
        var mscSarif = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"roslyn","rules":[{"id":"ASP0000"},{"id":"CA1848"}]}},"results":[{"ruleIndex":0,"level":"warning","message":{"text":"ASP0000"}},{"ruleIndex":1,"level":"note","message":{"text":"CA1848"}}]}]}
            """;
        var mscImported = await ReportImports.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(mscSarif)), "sarif", "2.1.0", "roslyn", CancellationToken.None);
        var dotnetProfile = ProfileEndpoints.BuiltInProfile("upcoop-dotnet", GateEvaluation.DotNetMqrProfile);
        var dotnetRules = ExecutableProfiles.BuildRuleIndex(dotnetProfile);
        foreach (var toolVersion in new[] { "10.0.201", "11.0.100" })
        {
            var descriptor = new ReportDescriptor("roslyn", "sarif", "2.1.0", "roslyn", toolVersion, "measured", 1, new string('a', 64));
            var classified = mscImported.Findings.ToDictionary(finding => finding.RuleId,
                finding => IngestionProcessor.ClassifyFinding(dotnetProfile, dotnetRules, descriptor, finding));
            Assert.True(classified["ASP0000"] == ("bug", "high") && classified["CA1848"] == ("code_smell", "low"),
                $"MSC SARIF findings must use the v2 catalogue with Roslyn {toolVersion}");
        }
        await Reject(sarif.Replace("src/a.cs", "../private"), "sarif", "2.1.0");
        await Reject(sarif.Replace("src/a.cs", "https://host/code"), "sarif", "2.1.0");
        await Reject(sarif, "sarif", "2.2.0");
        await Reject("{}", "sarif", "2.1.0");
        await Reject(
            "{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"scanner\"}}}]}",
            "sarif",
            "2.1.0"
        );
        await Reject(
            "{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"scanner\"}},\"invocations\":[{\"executionSuccessful\":false}],\"results\":[]}]}",
            "sarif",
            "2.1.0"
        );

        parsed = await Parse(
            "SF:./src/a.cs\nDA:1,0\nDA:2,1\nend_of_record\nSF:src/a.cs\nDA:1,1\nend_of_record\n",
            "lcov",
            "1"
        );
        Assert.True(
            parsed.Coverage.Count == 2 && parsed.Coverage.All(x => x.Covered),
            "LCOV duplicate coverage union"
        );
        parsed = await Parse("SF:src/zero.cs\nDA:1,0\nend_of_record\n", "lcov", "1");
        Assert.True(parsed.Coverage is [{ Covered: false }], "Measured zero remains zero");
        parsed = await Parse(
            "SF:/agent/source/src/rooted.cs\nDA:1,1\nend_of_record\n",
            "lcov",
            "1",
            "/agent/source"
        );
        Assert.True(parsed.Coverage.Single().Path == "src/rooted.cs", "LCOV path under source root");
        await Reject("SF:a.cs\nDA:1,-2\nend_of_record\n", "lcov", "1");
        await Reject("SF:a.cs\nDA:1,2\n", "lcov", "1");
        await Reject("DA:1,2\nend_of_record\n", "lcov", "1");

        const string cobertura =
            "<coverage><packages><package><classes><class filename='src/a.cs'><lines><line number='1' hits='0'/><line number='2' hits='4'/></lines></class></classes></package></packages></coverage>";
        parsed = await Parse(cobertura, "cobertura", "1");
        Assert.True(
            parsed.Coverage.Count == 2 && parsed.Coverage.Count(x => x.Covered) == 1,
            "Cobertura executable lines"
        );
        const string coberturaBranchesUnmeasured =
            "<coverage branches-valid='4'><packages><package><classes><class filename='src/a.cs'><lines><line number='1' hits='1'/></lines></class></classes></package></packages></coverage>";
        parsed = await Parse(coberturaBranchesUnmeasured, "cobertura", "1");
        Assert.True(
            !parsed.CodeData.ConditionsMeasured && !parsed.CodeData.BranchesMeasured,
            "dotnet-coverage's MTP export can declare branches-valid with no per-line branch detail; unmeasured, not fatal"
        );
        parsed = await Parse(
            "<coverage branches-valid='2'><packages><package><classes><class filename='src/a.cs'>"
                + "<lines><line number='1' hits='1' branch='True' condition-coverage='50% (1/4)'/></lines>"
                + "</class></classes></package></packages></coverage>",
            "cobertura",
            "1"
        );
        Assert.True(
            !parsed.CodeData.ConditionsMeasured && !parsed.CodeData.BranchesMeasured,
            "A branches-valid total that disagrees with per-line detail (any direction) is unmeasured, not fatal"
        );
        parsed = await Parse(
            "<coverage><packages><package><classes>"
                + "<class name='A' filename='src/a.cs' complexity='NaN'><lines><line number='1' hits='1'/></lines></class>"
                + "</classes></package></packages></coverage>",
            "cobertura",
            "1"
        );
        Assert.True(
            parsed.CodeData.Metrics.Count == 0 && parsed.Coverage.Count == 1,
            "dotnet-coverage's complexity='NaN' for some classes is unmeasured, not fatal to the whole report"
        );
        parsed = await Parse(
            cobertura.Replace("src/a.cs", "/agent/source/src/a.cs"),
            "cobertura",
            "1",
            "/agent/source"
        );
        Assert.True(
            parsed.Coverage.All(x => x.Path == "src/a.cs"),
            "Cobertura path under source root"
        );
        parsed = await Parse(
            "<!DOCTYPE coverage SYSTEM \"http://cobertura.sourceforge.net/xml/coverage-04.dtd\">"
                + cobertura,
            "cobertura",
            "1"
        );
        Assert.True(
            parsed.Coverage.Count == 2,
            "A benign external-SYSTEM DOCTYPE (common in real Cobertura exports) does not fail the report"
        );
        await Reject(
            "<!DOCTYPE coverage [<!ENTITY e SYSTEM 'file:///etc/passwd'>]>"
                + cobertura.Replace("src/a.cs", "&e;"),
            "cobertura",
            "1"
        );
        parsed = await Parse(cobertura.Replace("src/a.cs", "C:\\secret"), "cobertura", "1");
        Assert.True(
            parsed.Coverage.Count == 0 && parsed.CodeData.SourceFiles.Count == 0,
            "An absolute path with no declared source_root leaves that one class unmeasured, not fatal to the whole report"
        );
        parsed = await Parse(
            cobertura.Replace("src/a.cs", "/agent/other/a.cs"),
            "cobertura",
            "1",
            "/agent/source"
        );
        Assert.True(
            parsed.Coverage.Count == 0 && parsed.CodeData.SourceFiles.Count == 0,
            "reportgenerator merging coverage across drives/projects can emit a class outside source_root; that one class is unmeasured, not fatal to the whole report"
        );
        await Reject("<coverage/>", "cobertura", "1");
        await Reject(
            "<coverage><packages>"
                + string.Concat(Enumerable.Repeat("<a>", 65))
                + string.Concat(Enumerable.Repeat("</a>", 65))
                + "</packages></coverage>",
            "cobertura",
            "1"
        );

        const string cdx = """
            {"bomFormat":"CycloneDX","specVersion":"1.6","metadata":{"component":{"bom-ref":"root","name":"app"}},"components":[{"bom-ref":"one","name":"same","version":"1","purl":"pkg:nuget/same@1","licenses":[{"expression":"MIT OR Apache-2.0"}]},{"bom-ref":"two","name":"same","version":"2","purl":"pkg:npm/same@2"},{"bom-ref":"isolated","name":"unknown"}],"dependencies":[{"ref":"root","dependsOn":["one"]},{"ref":"one","dependsOn":["two"]}],"vulnerabilities":[{"id":"CVE-2025-1234","source":{"name":"producer-db"},"published":"2025-01-01T00:00:00Z","ratings":[{"severity":"high"}],"affects":[{"ref":"two"}]}]}
            """;
        parsed = await Parse(cdx, "cyclonedx", "1.6");
        Assert.True(
            parsed.Components.Count == 4
                && parsed.Components[1].Relationship == "direct"
                && parsed.Components[2].Relationship == "transitive"
                && parsed.Components[3].Relationship == "unknown",
            "SBOM relationships and homonyms"
        );
        Assert.True(
            parsed.Components[1].Licenses.Single() == "MIT OR Apache-2.0"
                && parsed.Components[2].Ecosystem == "npm",
            "Licenses and ecosystem"
        );
        Assert.True(
            parsed.Vulnerabilities
                is [{ ComponentReference: "two", Source: "producer-db", PublishedAt: not null }],
            "Exact vulnerability reference and provenance"
        );
        Assert.True(parsed.VulnerabilityAssessmentMeasured == true, "Explicit vulnerability assessment");
        var inventoryOnly = await Parse(
            "{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.6\",\"components\":[]}",
            "cyclonedx",
            "1.6"
        );
        Assert.True(
            inventoryOnly.VulnerabilityAssessmentMeasured is null,
            "SBOM inventory alone is not a vulnerability assessment"
        );
        var assessedZero = await Parse(
            "{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.6\",\"components\":[],\"vulnerabilities\":[]}",
            "cyclonedx",
            "1.6"
        );
        Assert.True(
            assessedZero.VulnerabilityAssessmentMeasured == true
                && assessedZero.Vulnerabilities.Count == 0,
            "Explicit empty assessment remains measured zero"
        );
        foreach (var version in new[] { "1.4", "1.5", "1.7" })
            await Parse(cdx.Replace("1.6", version), "cyclonedx", version);
        await Reject(cdx, "cyclonedx", "1.3");
        await Reject(cdx.Replace("\"ref\":\"two\"", "\"ref\":\"missing\""), "cyclonedx", "1.6");
        await Reject(cdx.Replace("\"bom-ref\":\"two\"", "\"bom-ref\":\"one\""), "cyclonedx", "1.6");

        const string spdx = """
            {"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","documentDescribes":["SPDXRef-App"],"packages":[{"SPDXID":"SPDXRef-App","name":"app"},{"SPDXID":"SPDXRef-Lib","name":"app","versionInfo":"2","licenseDeclared":"NOASSERTION","externalRefs":[{"referenceType":"purl","referenceLocator":"pkg:nuget/app@2"}]}],"relationships":[{"spdxElementId":"SPDXRef-Lib","relatedSpdxElement":"SPDXRef-App","relationshipType":"DEPENDENCY_OF"}]}
            """;
        parsed = await Parse(spdx, "spdx", "2.3");
        Assert.True(
            parsed.Components.Count == 2
                && parsed.Components[1].Relationship == "direct"
                && parsed.Components[1].Licenses.Single() == "NOASSERTION",
            "SPDX reversed dependency and unknown license"
        );
        Assert.True(parsed.Vulnerabilities.Count == 0, "No fabricated CVE enrichment");
        await Reject(spdx.Replace("SPDX-2.3", "SPDX-3.0"), "spdx", "2.3");
        await Reject("PK archive", "zip", "1");

        const string trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-09-08T10:00:00Z" finish="2026-09-08T10:00:03Z" />
              <ResultSummary><Counters total="5" executed="4" passed="3" failed="1" notExecuted="1" /></ResultSummary>
              <Results><UnitTestResult duration="00:00:01.250" /><UnitTestResult duration="00:00:00.750" /></Results>
            </TestRun>
            """;
        parsed = await Parse(trx, "trx", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(5, 1, 1, 2000), "TRX test summary");
        await Reject(trx.Replace("total=\"5\"", "total=\"1\""), "trx", "1");
        parsed = await Parse(
            "<!DOCTYPE TestRun SYSTEM \"testrun.dtd\">" + trx,
            "trx",
            "1"
        );
        Assert.True(
            parsed.Tests == new ImportedTestSummary(5, 1, 1, 2000),
            "A benign external-SYSTEM DOCTYPE does not fail a TRX report"
        );
        await Reject(
            "<!DOCTYPE TestRun [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>"
                + trx.Replace("total=\"5\"", "total=\"&x;\""),
            "trx",
            "1"
        );

        const string junit = """
            <testsuites tests="7" failures="1" errors="1" skipped="2" time="1.234">
              <testsuite name="ignored-aggregate-child" tests="7" failures="1" errors="1" skipped="2" time="1.234" />
            </testsuites>
            """;
        parsed = await Parse(junit, "junit", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(7, 2, 2, 1234), "JUnit test summary without double counting");
        parsed = await Parse("<testsuites><testsuite tests='2' failures='0' skipped='1' time='0.1'/><testsuite tests='3' errors='1' time='0.2'/></testsuites>", "junit", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(5, 1, 1, 300), "JUnit child suite aggregation");
        parsed = await Parse("<testsuites tests='2'><testsuite tests='2' failures='1' time='0.1'/></testsuites>", "junit", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(2, 1, 0, 100), "JUnit partial root does not hide child failures");
        parsed = await Parse("<testsuites><testsuites><testsuite tests='2' skipped='1' time='0.2'/></testsuites></testsuites>", "junit", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(2, 0, 1, 200), "JUnit nested terminal suite aggregation");
        // jest-junit on a repo with zero test files emits <testsuites tests="0" .../> with no
        // <testsuite> children at all -- a real "no tests ran" result, not malformed data.
        // Trx() already allows zero UnitTestResult entries the same way; JUnit() must match.
        parsed = await Parse("<testsuites name=\"jest tests\" tests=\"0\" failures=\"0\" errors=\"0\" time=\"0.018\"></testsuites>", "junit", "1");
        Assert.True(parsed.Tests == new ImportedTestSummary(0, 0, 0, 0), "JUnit zero suites is a valid empty result, not rejected");
        await Reject("<testsuite tests='1' failures='2'/>", "junit", "1");
        await Reject("<testsuite tests='1' time='999999999999'/>", "junit", "1");
        foreach (var format in new[] { "trx", "junit" })
        {
            var manifest = new IngestionManifest
            {
                ProjectKey = "checks",
                BuildId = "build-1",
                CommitSha = "abcdef",
                Branch = "main",
                Reports = [new(format, format, "1", "tests", "1", "measured", 1, new string('a', 64))],
            };
            IngestionValidation.Validate(manifest, new IngestionOptions());
        }
        foreach (var (format, version) in new[]
        {
            ("sarif", "2.1.0"), ("cyclonedx", "1.6"), ("spdx", "2.3"),
            ("cobertura", "1"), ("lcov", "1"), ("jscpd", "1"), ("cloc", "1"),
            ("lizard", "1"), ("trx", "1"), ("junit", "1"),
        })
        {
            var missingProvenance = new IngestionManifest
            {
                ProjectKey = "checks", BuildId = "build-1", CommitSha = "abcdef", Branch = "main",
                Reports = [new(format, format, version, "tool", " ", "measured", 1, new string('a', 64))],
            };
            try
            {
                IngestionValidation.Validate(missingProvenance, new IngestionOptions());
                throw new InvalidOperationException($"Blank tool_version accepted for {format}.");
            }
            catch (ApiException exception) when (exception.StatusCode == 422) { }
        }
        var dartSarif = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"dart-analyze","version":"3.12.2","rules":[{"id":"unused_local_variable","helpUri":"https://dart.dev/diagnostics/unused_local_variable","defaultConfiguration":{"level":"warning"}}]}},"results":[{"ruleId":"unused_local_variable","ruleIndex":0,"level":"warning","message":{"text":"The value of the local variable 'x' isn't used."},"partialFingerprints":{"dartAnalyzeDiagnostic":"9f1c"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"/agent/source/lib/main.dart"},"region":{"startLine":2,"startColumn":7}}}]}]}]}
            """;
        parsed = await Parse(dartSarif, "sarif", "2.1.0", "/agent/source");
        Assert.True(
            parsed.Findings
                is [
                    {
                        Tool: "dart-analyze",
                        RuleId: "unused_local_variable",
                        OriginalSeverity: "warning",
                        Severity: "medium",
                        Category: "unknown",
                        Path: "lib/main.dart",
                        Line: 2
                    },
                ],
            "dart analyze SARIF keeps producer identity and repository-relative paths"
        );
        await Reject(dartSarif, "sarif", "2.1.0");

        parsed = await Parse(
            "SF:lib/main.dart\nDA:3,1\nDA:4,0\nBRDA:3,0,0,1\nBRDA:3,0,1,-\nBRF:2\nBRH:1\nLF:2\nLH:1\nend_of_record\n",
            "lcov",
            "1"
        );
        Assert.True(
            parsed.Coverage.Count == 2
                && parsed.CodeData.BranchesMeasured
                && parsed.CodeData.Branches.Count == 2
                && parsed.CodeData.Branches.Count(x => x.Covered == 1) == 1,
            "Flutter branch coverage LCOV keeps line and branch evidence"
        );

        const string dartComplexity =
            "<cppncss><measure type='File'><labels><label>Nr.</label><label>NCSS</label><label>CCN</label><label>Functions</label></labels><item name='lib/main.dart'><value>1</value><value>18</value><value>9</value><value>4</value></item></measure></cppncss>";
        parsed = await Parse(dartComplexity, "lizard", "1");
        Assert.True(
            parsed.CodeData.Metrics is [{ Path: "lib/main.dart", Metric: "class_complexity", Value: 9 }],
            "Dart complexity report follows the cppncss contract"
        );
        parsed = await Parse(
            dartComplexity.Replace("lib/main.dart", "/agent/source/lib/main.dart"),
            "lizard",
            "1",
            "/agent/source"
        );
        Assert.True(
            parsed.CodeData.Metrics.Single().Path == "lib/main.dart",
            "Lizard path under source root"
        );
        await Reject(
            dartComplexity.Replace("lib/main.dart", "/agent/source/lib/main.dart"),
            "lizard",
            "1"
        );
        await Reject(
            dartComplexity.Replace("lib/main.dart", "/agent/other/lib/main.dart"),
            "lizard",
            "1",
            "/agent/source"
        );

        var flutterManifest = new IngestionManifest
        {
            ProjectKey = "checks",
            BuildId = "build-1",
            CommitSha = "abcdef",
            Branch = "main",
            ProfileId = "upcoop-flutter",
            ProfileVersion = "1",
            Reports =
            [
                new("dart-analyze", "sarif", "2.1.0", "dart-analyze", "3.12.2", "measured", 1, new string('a', 64)),
                new("flutter-coverage", "lcov", "1", "flutter-coverage", "3.44.8", "measured", 1, new string('b', 64)),
                new("dart-complexity", "lizard", "1", "dart-complexity", "1.1.1", "measured", 1, new string('c', 64)),
            ],
        };
        IngestionValidation.Validate(flutterManifest, new IngestionOptions());

        parsed = await Parse(
            """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"phpstan","version":"2.3.1","rules":[{"id":"argument.type","helpUri":"https://phpstan.org/error-identifiers/argument.type","defaultConfiguration":{"level":"warning"}}]}},"results":[{"ruleId":"argument.type","level":"warning","message":{"text":"Parameter #1 $id of method expects int, string given."},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"app/Services/OrderService.php"},"region":{"startLine":42}}}]}]}]}
            """,
            "sarif",
            "2.1.0",
            "/agent/source"
        );
        Assert.True(
            parsed.Findings is [{ Tool: "phpstan", RuleId: "argument.type", Path: "app/Services/OrderService.php", Line: 42 }],
            "PHPStan SARIF keeps its error identifier and repository-relative path"
        );

        // PHPUnit's Cobertura writer: DOCTYPE, class complexity and no per-line branch detail.
        const string phpunitCobertura = """
            <?xml version="1.0"?>
            <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
            <coverage line-rate="0.5" branch-rate="0" lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0" complexity="" version="0.4" timestamp="1">
              <sources><source>/agent/source/app</source></sources>
              <packages><package name="Services" line-rate="0.5" branch-rate="0" complexity="3"><classes>
                <class name="App\Services\OrderService" filename="app/Services/OrderService.php" line-rate="0.5" branch-rate="0" complexity="3">
                  <methods/><lines><line number="12" hits="4"/><line number="13" hits="0"/></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        parsed = await Parse(phpunitCobertura.Replace(" branches-covered=\"0\" branches-valid=\"0\"", ""), "cobertura", "1", "/agent/source");
        Assert.True(
            parsed.Coverage.Count == 2
                && parsed.Coverage.All(x => x.Path == "app/Services/OrderService.php")
                && !parsed.CodeData.BranchesMeasured
                && !parsed.CodeData.ConditionsMeasured
                && parsed.CodeData.Metrics is [{ Metric: "class_complexity", Value: 3 }],
            "normalized PHPUnit Cobertura keeps line coverage and leaves branches unmeasured"
        );
        parsed = await Parse(phpunitCobertura, "cobertura", "1", "/agent/source");
        Assert.True(
            parsed.CodeData.BranchesMeasured && parsed.CodeData.Branches.Count == 0,
            "raw PHPUnit branches-valid=0 would declare a measured empty branch set; the PHP producer removes it"
        );

        parsed = await Parse(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuites>
              <testsuite name="/agent/source/phpunit.xml" tests="3" assertions="3" errors="0" failures="1" skipped="1" time="0.250000">
                <testsuite name="Unit" tests="3" assertions="3" errors="0" failures="1" skipped="1" time="0.250000">
                  <testsuite name="Tests\Unit\ExampleTest" file="/agent/source/tests/Unit/ExampleTest.php" tests="3" assertions="3" errors="0" failures="1" skipped="1" time="0.250000">
                    <testcase name="test_that_true_is_true" time="0.1"/>
                  </testsuite>
                </testsuite>
              </testsuite>
            </testsuites>
            """,
            "junit",
            "1"
        );
        Assert.True(
            parsed.Tests is { Total: 3, Failed: 1, Skipped: 1 },
            "nested PHPUnit JUnit suites are counted once, at the leaf"
        );

        IngestionValidation.Validate(
            new IngestionManifest
            {
                ProjectKey = "checks",
                BuildId = "build-1",
                CommitSha = "abcdef",
                Branch = "main",
                ProfileId = "upcoop-php",
                ProfileVersion = "1",
                Reports =
                [
                    new("phpstan", "sarif", "2.1.0", "phpstan", "2.3.1", "measured", 1, new string('a', 64)),
                    new("php-coverage", "cobertura", "1", "php-coverage", "12.5.8", "measured", 1, new string('b', 64)),
                    new("phpunit", "junit", "1", "phpunit", "12.5.8", "measured", 1, new string('c', 64)),
                ],
            },
            new IngestionOptions()
        );

        Console.WriteLine("Report import checks passed.");
    }

    private static Task<ImportedReport> Parse(
        string data,
        string format,
        string version,
        string sourceRoot = ""
    ) =>
        ReportImports.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(data)),
            format,
            version,
            "fixture",
            CancellationToken.None,
            sourceRoot
        );

    private static async Task Reject(
        string data,
        string format,
        string version,
        string sourceRoot = ""
    )
    {
        try
        {
            await Parse(data, format, version, sourceRoot);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected rejection for {format}.");
    }

}
