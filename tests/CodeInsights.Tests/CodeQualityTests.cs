using System.Text;
using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Services;

public sealed class CodeQualityTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var first = await Parse(
            "SF:src/a.cs\nDA:2,0\nDA:3,1\nBRDA:2,0,0,0\nBRDA:2,0,1,1\nend_of_record\n",
            "lcov"
        );
        var second = await Parse(
            "SF:src/a.cs\nDA:2,1\nBRDA:2,0,0,1\nBRDA:2,0,1,-\nend_of_record\n",
            "lcov"
        );
        var snapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(snapshot, first, Descriptor("one", "lcov"));
        CodeQualityData.AddReport(snapshot, second, Descriptor("two", "lcov"));
        var totals = CodeQualityData.Summarize(snapshot, first.Coverage.Concat(second.Coverage));
        Assert.True(
            totals.Lines.Numerator == 2
                && totals.Lines.Denominator == 2
                && totals.Branches.Numerator == 2
                && totals.Branches.Denominator == 2,
            "Exact LCOV branch identities and line union without averages"
        );
        Assert.True(
            CodeQualityData.Summarize(snapshot, first.Coverage, "src/absent.cs").Branches.State
                == "not_measured",
            "A file absent from the coverage report has no proved branch denominator"
        );
        var diff = new GitDiff { State = "available", Files = [new("src/a.cs", null, [2], [])] };
        var changed = CodeQualityData.Summarize(
            snapshot,
            first.Coverage,
            newCode: true,
            diff: diff
        );
        Assert.True(
            changed.Lines.Numerator == 0
                && changed.Lines.Denominator == 1
                && changed.Branches.Denominator == 2,
            "Changed-line coverage scope"
        );
        Assert.True(
            CodeQualityData.Summarize(snapshot, first.Coverage, newCode: true).Lines.State
                == "unavailable",
            "Missing diff never becomes measured zero"
        );
        var missing = new GitDiff
        {
            State = "available",
            Files = [new("src/absent.cs", null, [2], [])],
        };
        Assert.True(
            CodeQualityData
                .Summarize(snapshot, first.Coverage, newCode: true, diff: missing)
                .Lines.State == "not_applicable"
                && CodeQualityData
                    .Summarize(snapshot, first.Coverage, newCode: true, diff: missing)
                    .Branches.State == "not_applicable",
            "A measured report with no executable changed line is not applicable"
        );
        var mixed = diff with { Files = [.. diff.Files, new("docs/readme.md", null, [1], [])] };
        Assert.True(
            CodeQualityData.Summarize(snapshot, first.Coverage, newCode: true, diff: mixed).Lines.Denominator == 1,
            "Documentation in a mixed diff does not make executable coverage incomplete"
        );
        var frontend = await Parse(
            "SF:apps/web/src/App.tsx\nDA:10,1\nend_of_record\n",
            "lcov"
        );
        var frontendSnapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(frontendSnapshot, frontend, Descriptor("web", "lcov"));
        var frontendDiff = new GitDiff
        {
            State = "available",
            Files =
            [
                new("apps/web/src/App.tsx", null, [10], []),
                new("docs/interface.md", null, [4], []),
                new("apps/web/src/styles.css", null, [2], []),
            ],
        };
        Assert.True(
            CodeQualityData.Summarize(frontendSnapshot, frontend.Coverage, newCode: true, diff: frontendDiff).Lines.Percentage == 100,
            "frontend LCOV contributes while documentation and CSS stay outside executable lines"
        );
        const string cobertura =
            "<coverage branches-valid='2'><packages><package><classes><class name='A' filename='src/a.cs' complexity='3'><lines><line number='2' hits='0' branch='True' condition-coverage='50% (1/2)'/></lines></class></classes></package></packages></coverage>";
        var conditions = await Parse(cobertura, "cobertura");
        var conditionsSnapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(conditionsSnapshot, conditions, Descriptor("one", "cobertura"));
        var conditionTotals = CodeQualityData.Summarize(conditionsSnapshot, conditions.Coverage);
        Assert.True(
            conditionTotals.Conditions.Numerator == 1
                && conditionTotals.Conditions.Denominator == 2
                && conditionTotals.Branches.Numerator == 1
                && conditionTotals.Branches.Denominator == 2
                && conditionTotals.ClassComplexity == 3,
            "Cobertura native branch/condition counts and complexity"
        );
        const string coberturaMismatch =
            "<coverage branches-valid='99'><packages><package><classes><class name='A' filename='src/a.cs' complexity='3'><lines><line number='2' hits='0' branch='True' condition-coverage='50% (1/2)'/></lines></class></classes></package></packages></coverage>";
        var mismatch = await Parse(coberturaMismatch, "cobertura");
        var mismatchSnapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(mismatchSnapshot, mismatch, Descriptor("mismatch", "cobertura"));
        var mismatchTotals = CodeQualityData.Summarize(mismatchSnapshot, mismatch.Coverage);
        Assert.True(
            mismatchTotals.Branches.State == "not_measured"
                && mismatchTotals.Conditions.State == "not_measured",
            "A branches-valid mismatch stays not_measured at global scope even though raw branch rows are still present"
        );
        var lineOnly = await Parse(
            "<coverage><packages><package><classes><class name='A' filename='src/a.cs'><lines><line number='1' hits='1'/><line number='2' hits='0'/></lines></class></classes></package></packages></coverage>",
            "cobertura"
        );
        var lineOnlySnapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(
            lineOnlySnapshot,
            lineOnly,
            Descriptor("line-only", "cobertura")
        );
        var lineOnlyTotals = CodeQualityData.Summarize(
            lineOnlySnapshot,
            lineOnly.Coverage
        );
        var lineOnlyGate = IngestionProcessor
            .EvaluateConditions(
                new ProfileDefinition
                {
                    Conditions =
                    [
                        new("security_rating", "max", 1, "overall"),
                        new("coverage", "min", 80, "overall"),
                    ],
                },
                new() { ["security_rating"] = 1, ["coverage"] = null },
                new() { ["security_rating"] = "measured", ["coverage"] = "not_measured" },
                JsonSerializer.Deserialize<JsonElement>(
                    "{\"state\":\"available\",\"baseline_id\":\"base\"}"
                ),
                new GitDiff { State = "available" },
                1,
                2,
                true,
                lineOnlyTotals,
                lineOnlyTotals
            )
            .Single(condition => condition.Metric == "coverage");
        Assert.True(
            lineOnlyTotals.Conditions.State == "not_measured"
                && lineOnlyGate.State == "measured"
                && lineOnlyGate.Value == 50,
            "Overall coverage falls back to executable-line coverage when the producer has no condition details"
        );
        CodeQualityData.AddReport(conditionsSnapshot, conditions, Descriptor("two", "cobertura"));
        Assert.True(
            CodeQualityData.Summarize(conditionsSnapshot, conditions.Coverage).Conditions.State
                == "ambiguous",
            "Overlapping anonymous conditions cannot claim a union"
        );
        var zero = await Parse("SF:a.cs\nDA:1,1\nBRF:0\nend_of_record\n", "lcov");
        var zeroSnapshot = new CodeQualitySnapshot();
        CodeQualityData.AddReport(zeroSnapshot, zero, Descriptor("zero", "lcov"));
        Assert.True(
            CodeQualityData.Summarize(zeroSnapshot, zero.Coverage).Branches.State
                == "not_applicable",
            "Measured no branches distinguished from missing branch report"
        );
        const string clones =
            """{"duplicates":[{"format":"csharp","lines":3,"tokens":20,"fragment":"SECRET SOURCE","firstFile":{"name":"src/a.cs","start":2,"end":4},"secondFile":{"name":"src/b.cs","start":8,"end":10}}]}""";
        var duplicate = await Parse(clones, "jscpd");
        CodeQualityData.AddReport(snapshot, duplicate, Descriptor("duplicates", "jscpd"));
        Assert.True(
            CodeQualityData.Summarize(snapshot, [], newCode: true, diff: diff).DuplicateBlocks == 1,
            "Real duplicate blocks intersect changed lines"
        );
        Assert.True(
            !CodeQualityData.Serialize(snapshot).Contains("SECRET SOURCE"),
            "Duplicate fragments not persisted"
        );
        const string cloc =
            """{"header":{"cloc_version":"1"},"src/a.cs":{"language":"C#","code":12,"comment":2,"blank":1},"SUM":{"code":12}}""";
        CodeQualityData.AddReport(snapshot, await Parse(cloc, "cloc"), Descriptor("size", "cloc"));
        Assert.True(
            CodeQualityData.Summarize(snapshot, [], "src").CodeLines == 12,
            "cloc by-file count and directory total"
        );
        Assert.True(
            CodeQualityData.Summarize(snapshot, [], newCode: true, diff: diff).CodeLines is null,
            "File size not invented for changed-line subset"
        );
        const string lizard =
            "<cppncss><measure type='File'><labels><label>Nr.</label><label>NCSS</label><label>CCN</label><label>Functions</label></labels><item name='src/a.cs'><value>1</value><value>12</value><value>7</value><value>3</value></item></measure></cppncss>";
        var lizardReport = await Parse(lizard, "lizard");
        CodeQualityData.AddReport(snapshot, lizardReport, Descriptor("complexity", "lizard"));
        Assert.True(
            CodeQualityData.Summarize(snapshot, [], "src").ClassComplexity == 7,
            "Lizard file CCN is imported as repository complexity"
        );
        CodeQualityData.AddReport(
            conditionsSnapshot,
            lizardReport,
            Descriptor("lizard-complexity", "lizard")
        );
        Assert.True(
            CodeQualityData.Summarize(conditionsSnapshot, conditions.Coverage).ClassComplexity == 7,
            "Lizard complexity takes precedence over Cobertura complexity"
        );
        var profile = new ProfileDefinition
        {
            Conditions = [new("branch_coverage", "min", 80, "new_code")],
        };
        var gate = IngestionProcessor
            .EvaluateConditions(
                profile,
                [],
                [],
                JsonSerializer.Deserialize<JsonElement>(
                    "{\"state\":\"available\",\"baseline_id\":\"base\"}"
                ),
                diff,
                1,
                2,
                true,
                totals,
                totals
            )
            .Single();
        Assert.True(
            gate.Value == 100
                && gate.Numerator == 2
                && gate.Denominator == 2
                && gate.Result == "pass",
            "Branch gate preserves exact numerator and denominator"
        );
        var noBranches = CodeQualityData.Summarize(zeroSnapshot, zero.Coverage);
        gate = IngestionProcessor
            .EvaluateConditions(
                profile,
                [],
                [],
                JsonSerializer.Deserialize<JsonElement>(
                    "{\"state\":\"unavailable\",\"reason\":\"baseline_absent\"}"
                ),
                diff,
                0,
                0,
                true,
                noBranches,
                noBranches
            )
            .Single();
        Assert.True(
            gate.Result == "unknown",
            "Unavailable baseline overrides global non-applicable branch state"
        );
        foreach (
            var (text, format) in new[]
            {
                (clones.Replace("src/a.cs", "../secret"), "jscpd"),
                (cloc.Replace("12", "-1"), "cloc"),
                (lizard.Replace("<label>CCN</label>", ""), "lizard"),
                (cobertura.Replace("1/2", "3/2"), "cobertura"),
            }
        )
        {
            try
            {
                await Parse(text, format);
                throw new InvalidOperationException("Expected malformed quality report rejection");
            }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("Code quality import, scope, aggregation and provenance checks passed.");
    }

    private static ReportDescriptor Descriptor(string id, string format) =>
        new(id, format, "1", format, "1", "measured", 1, new string('a', 64));

    private static Task<ImportedReport> Parse(string text, string format) =>
        ReportImports.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            format,
            "1",
            format,
            CancellationToken.None
        );

}
