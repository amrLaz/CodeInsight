using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeInsights.Api.Services;

public sealed record ImportedFinding(string Tool, string RuleId, string Severity, string OriginalSeverity, string Category, string Message, string? Path, int? Line, string? Fingerprint)
{
    public FindingEvidence? Evidence { get; init; }
}
public sealed record ImportedCoverage(string Path, int Line, bool Covered);
public sealed record ImportedComponent(string Reference, string Name, string? Version, string? Purl, string? Ecosystem, List<string> Licenses, string Relationship);
public sealed record ImportedDependency(string From, string To, string Relationship);
public sealed record ImportedVulnerability(string ComponentReference, string Id, string? Severity, string Source, DateTimeOffset? PublishedAt);
/// <summary>Normalized aggregate from one TRX or JUnit report.</summary>
public sealed record ImportedTestSummary(long Total, long Failed, long Skipped, long DurationMilliseconds);
public sealed record ImportedReport(List<ImportedFinding> Findings, List<ImportedCoverage> Coverage, List<ImportedComponent> Components, List<ImportedDependency> Dependencies, List<ImportedVulnerability> Vulnerabilities)
{
    public bool? VulnerabilityAssessmentMeasured { get; init; }
    public CodeQualityReport CodeData { get; init; } = new();
    public ImportedTestSummary? Tests { get; init; }
}

/// <summary>Imports bounded, untrusted reports without resolving external resources.</summary>
public static class ReportImports
{
    public const string MappingVersion = "1";
    private const int MaxBytes = 50 * 1024 * 1024;
    private const int MaxEntries = 250_000;
    private static readonly JsonElement EmptyArray = JsonElement.Parse("[]");
    private static readonly string[] LcovRecordPrefixes = ["TN:", "FN:", "FNDA:", "FNF:", "FNH:", "BRDA:", "BRF:", "BRH:", "LF:", "LH:", "VER:"];

    /// <summary>Parses supported report formats. Invalid reports raise InvalidDataException.</summary>
    public static async Task<ImportedReport> ParseAsync(Stream input, string format, string version, string tool, CancellationToken ct, string sourceRoot = "")
    {
        SourcePaths.ValidateRoot(sourceRoot);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Report exceeds 50 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        buffer.Position = 0;
        var report = new ImportedReport([], [], [], [], []);
        try
        {
            switch (format.ToLowerInvariant())
            {
                case "lcov":
                    Require(version is "1" or "1.0", "Unsupported LCOV version.");
                    await LcovAsync(buffer, report, sourceRoot, ct);
                    break;
                case "cobertura":
                    Require(version is "1" or "1.0", "Unsupported Cobertura version.");
                    await CoberturaAsync(buffer, report, sourceRoot, ct);
                    break;
                case "lizard":
                    Require(version == "1", "Unsupported Lizard version.");
                    Lizard(buffer, report, sourceRoot, ct);
                    break;
                case "trx":
                    Require(version == "1", "Unsupported TRX version.");
                    report = report with { Tests = Trx(buffer, ct) };
                    break;
                case "junit":
                    Require(version == "1", "Unsupported JUnit version.");
                    report = report with { Tests = JUnit(buffer, ct) };
                    break;
                case "sarif" when version == "2.1.0":
                case "cyclonedx" when version is "1.4" or "1.5" or "1.6" or "1.7":
                case "spdx" when version == "2.3":
                case "jscpd" when version == "1":
                case "cloc" when version == "1":
                    using (var json = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 64 }, ct))
                    {
                        Require(json.RootElement.ValueKind == JsonValueKind.Object, "Report must be an object.");
                        if (format.Equals("sarif", StringComparison.OrdinalIgnoreCase)) Sarif(json.RootElement, report, tool, version, sourceRoot, ct);
                        else if (format.Equals("cyclonedx", StringComparison.OrdinalIgnoreCase))
                        {
                            report = report with { VulnerabilityAssessmentMeasured = Property(json.RootElement, "vulnerabilities").ValueKind == JsonValueKind.Array ? true : null };
                            CycloneDx(json.RootElement, report, version, ct);
                        }
                        else if (format.Equals("spdx", StringComparison.OrdinalIgnoreCase)) Spdx(json.RootElement, report, ct);
                        else CodeQualityData.ImportJson(json.RootElement, format, report.CodeData, ct);
                    }
                    break;
                default: throw new InvalidDataException("Unsupported report format/version.");
            }
            report = report with { Coverage = report.Coverage.GroupBy(x => (x.Path, x.Line)).Select(g => new ImportedCoverage(g.Key.Path, g.Key.Line, g.Any(x => x.Covered))).ToList() };
            Require(report.Components.Select(x => x.Reference).Distinct(StringComparer.Ordinal).Count() == report.Components.Count, "Duplicate component reference.");
            return report;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException or OverflowException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid report structure.", ex);
        }
    }

    private static ImportedTestSummary Trx(Stream stream, CancellationToken ct)
    {
        var root = TestDocument(stream).Root;
        Require(root?.Name.LocalName == "TestRun", "Missing TRX TestRun root.");
        var counters = root!
            .Descendants()
            .SingleOrDefault(x => x.Name.LocalName == "Counters");
        Require(counters is not null, "Missing TRX counters.");
        var total = Count(counters!, "total");
        var failed = Count(counters!, "failed");
        var skipped = Count(counters!, "notExecuted") + Count(counters!, "notRunnable")
            + Count(counters!, "inconclusive") + Count(counters!, "pending");
        var results = root.Descendants().Where(x => x.Name.LocalName == "UnitTestResult").ToArray();
        Require(results.Length <= MaxEntries, "TRX contains too many test results.");
        long duration = 0;
        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();
            if ((string?)result.Attribute("duration") is { Length: > 0 } text)
            {
                Require(TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) && value >= TimeSpan.Zero, "Invalid TRX duration.");
                duration = checked(duration + (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero));
            }
        }
        if (duration == 0)
        {
            var times = root.Descendants().SingleOrDefault(x => x.Name.LocalName == "Times");
            if ((string?)times?.Attribute("start") is { } startText && (string?)times.Attribute("finish") is { } finishText)
            {
                var startValid = DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start);
                var finishValid = DateTimeOffset.TryParse(finishText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var finish);
                Require(startValid && finishValid && finish >= start, "Invalid TRX times.");
                duration = checked((long)Math.Round((finish - start).TotalMilliseconds, MidpointRounding.AwayFromZero));
            }
        }
        return TestSummary(total, failed, skipped, duration);
    }

    private static ImportedTestSummary JUnit(Stream stream, CancellationToken ct)
    {
        var root = TestDocument(stream).Root;
        Require(root is not null && root.Name.LocalName is "testsuite" or "testsuites", "Missing JUnit test suite root.");
        var summaries = root!
            .DescendantsAndSelf()
            .Where(x => x.Name.LocalName == "testsuite")
            .Where(x => !x.Descendants().Any(child => child.Name.LocalName == "testsuite"))
            .ToArray();
        // Zero <testsuite> elements is a legitimate "no tests ran" result (e.g. jest-junit
        // on a repo with no test files yet), not malformed data -- Trx() above already
        // allows zero UnitTestResult entries the same way. Only cap the upper bound.
        Require(summaries.Length <= MaxEntries, "Oversized JUnit suites.");
        long total = 0, failed = 0, skipped = 0, duration = 0;
        foreach (var suite in summaries)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + Count(suite, "tests"));
            failed = checked(failed + Count(suite, "failures") + Count(suite, "errors"));
            skipped = checked(skipped + Count(suite, "skipped") + Count(suite, "disabled"));
            duration = checked(duration + DurationMilliseconds((string?)suite.Attribute("time")));
        }
        return TestSummary(total, failed, skipped, duration);
    }

    private static XDocument TestDocument(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static long Count(XElement element, string name)
    {
        var text = (string?)element.Attribute(name) ?? "0";
        Require(long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 && value <= MaxEntries, $"Invalid test count '{name}'.");
        return value;
    }

    private static long DurationMilliseconds(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        Require(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0, "Invalid JUnit duration.");
        return checked((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
    }

    private static ImportedTestSummary TestSummary(long total, long failed, long skipped, long duration)
    {
        Require(total <= MaxEntries && failed <= total && skipped <= total - failed && duration is >= 0 and <= int.MaxValue, "Inconsistent test summary.");
        return new(total, failed, skipped, duration);
    }

    private static void Lizard(Stream stream, ImportedReport report, string sourceRoot, CancellationToken ct)
    {
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
            }
        );
        var root = XDocument.Load(reader, LoadOptions.None).Root;
        Require(
            root?.Name.LocalName == "cppncss" && root.Name.NamespaceName.Length == 0,
            "Missing Lizard cppncss root."
        );
        var measures = root!
            .Elements("measure")
            .Where(x => (string?)x.Attribute("type") == "File")
            .ToArray();
        Require(measures.Length == 1, "Lizard requires exactly one file measure.");
        var labels =
            measures[0].Element("labels")?.Elements("label").Select(x => x.Value).ToArray()
            ?? [];
        var complexityIndex = System.Array.IndexOf(labels, "CCN");
        Require(complexityIndex >= 0, "Lizard file measure has no CCN column.");
        foreach (var item in measures[0].Elements("item"))
        {
            ct.ThrowIfCancellationRequested();
            var values = item
                .Elements("value")
                .Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture))
                .ToArray();
            Require(
                complexityIndex < values.Length
                    && double.IsFinite(values[complexityIndex])
                    && values[complexityIndex] >= 0,
                "Invalid Lizard complexity."
            );
            var path = SafePath(
                SourcePaths.Relative((string?)item.Attribute("name") ?? "", sourceRoot)
            );
            Add(
                report.CodeData.Metrics,
                new CodeFileMetric(path, "class_complexity", values[complexityIndex], path)
            );
        }
    }

    private static readonly Regex DependencyAdvisoryIdPattern = new(
        @"^(CVE-\d{4}-\d+|GHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>True for a CVE or GHSA advisory identifier, Trivy's ruleId shape for a
    /// dependency-vulnerability (SCA) result as opposed to a misconfiguration (AVD-*/AZU-*) or
    /// secret-detection result.</summary>
    private static bool IsDependencyAdvisoryId(string ruleId) => DependencyAdvisoryIdPattern.IsMatch(ruleId);

    private static void Sarif(JsonElement root, ImportedReport report, string producer, string version, string sourceRoot, CancellationToken ct)
    {
        Require(Text(root, "version") == version, "SARIF version mismatch.");
        foreach (var run in Array(root, "runs", true))
        {
            ct.ThrowIfCancellationRequested();
            Require(!run.TryGetProperty("externalPropertyFileReferences", out _), "External SARIF result references are not supported.");
            Require(!Array(run, "invocations").Any(x => Property(x, "executionSuccessful").ValueKind == JsonValueKind.False), "SARIF producer execution failed.");
            var driver = Property(Property(run, "tool"), "driver");
            Require(driver.ValueKind == JsonValueKind.Object && Text(driver, "name") is { Length: > 0 }, "Missing SARIF driver.");
            var tool = Text(driver, "name") ?? producer;
            var rules = Array(driver, "rules").ToArray();
            var artifacts = Array(run, "artifacts").ToArray();
            foreach (var result in Array(run, "results", true))
            {
                ct.ThrowIfCancellationRequested();
                var ruleId = Text(result, "ruleId");
                JsonElement rule = default;
                if (Number(result, "ruleIndex") is { } index)
                {
                    Require(index >= 0 && index < rules.Length, "Invalid rule index.");
                    rule = rules[index];
                    ruleId ??= Text(rule, "id");
                }
                ruleId ??= Text(Property(result, "rule"), "id");
                Require(!string.IsNullOrWhiteSpace(ruleId), "Missing SARIF rule identity.");
                // Trivy's "fs --scanners vuln,misconfig,secret" interleaves dependency-CVE results
                // into the same SARIF run as its misconfiguration/secret findings, with the CVE or
                // GHSA advisory ID itself as the ruleId -- an unbounded, non-enumerable ID space,
                // unlike every cataloged rule this importer otherwise handles. Those findings are
                // not dropped: the SCA scan also produces a CycloneDX SBOM (parsed by CycloneDx()
                // below into ImportedVulnerability, matched to real package/component data the
                // SARIF result lacks), which is the actual source of the SCA Dep. Vulns dimension.
                // Importing them here too just left every one unclassified ("UNKNOWN" issues),
                // since a RuleCatalog can't enumerate CVE/GHSA IDs the way it does AZU-*/CKV_*.
                if (tool.Equals("trivy", StringComparison.OrdinalIgnoreCase) && IsDependencyAdvisoryId(ruleId!)) continue;
                if (rule.ValueKind == JsonValueKind.Undefined) rule = rules.FirstOrDefault(x => Text(x, "id") == ruleId);
                var severity = Text(result, "level") ?? Text(Property(rule, "defaultConfiguration"), "level") ?? "warning";
                var normalized = severity switch { "error" => "high", "warning" => "medium", "note" => "low", "none" => "info", _ => "unknown" };
                var message = Text(Property(result, "message"), "text") ?? Text(Property(result, "message"), "markdown") ?? Text(Property(result, "message"), "id");
                Require(message is not null, "Missing SARIF message.");
                // ponytail: no guessed rule taxonomy; a versioned rule mapping can classify known producers later.
                var fingerprints = Property(result, "fingerprints");
                if (fingerprints.ValueKind == JsonValueKind.Undefined) fingerprints = Property(result, "partialFingerprints");
                string? fingerprint = fingerprints.ValueKind == JsonValueKind.Object && fingerprints.EnumerateObject().Any()
                    ? JsonSerializer.Serialize(fingerprints.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Name, x => x.Value.GetString())) : null;
                var locations = Array(result, "locations").ToArray();
                var evidence = FindingEvidenceLinks.FromSarif(driver, Text(rule, "id") == ruleId ? rule : default, result, artifacts, sourceRoot);
                if (locations.Length == 0) Add(report.Findings, new ImportedFinding(tool, ruleId!, normalized, severity, "unknown", message!, null, null, fingerprint) { Evidence = evidence });
                foreach (var location in locations)
                {
                    var physical = Property(location, "physicalLocation");
                    var artifact = Property(physical, "artifactLocation");
                    if (Text(artifact, "uri") is null && Number(artifact, "index") is { } artifactIndex)
                    {
                        Require(artifactIndex >= 0 && artifactIndex < artifacts.Length, "Invalid artifact index.");
                        artifact = Property(artifacts[artifactIndex], "location");
                    }
                    var path = Text(artifact, "uri");
                    var line = Number(Property(physical, "region"), "startLine");
                    // Some producers report startLine <= 0 for a diagnostic with no precise
                    // source span (assembly/project-level, generated code); treat that as "no
                    // line" rather than failing the whole report over one location entry.
                    if (line is <= 0) line = null;
                    Add(report.Findings, new ImportedFinding(tool, ruleId!, normalized, severity, "unknown", message!, path is null ? null : SourcePaths.Relative(path, sourceRoot), line, fingerprint) { Evidence = evidence });
                }
            }
        }
    }

    private static async Task CoberturaAsync(
        Stream stream,
        ImportedReport report,
        string sourceRoot,
        CancellationToken ct
    )
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
        string? path = null;
        string? classIdentity = null;
        var classIndex = 0;
        var classDepth = -1;
        var linesDepth = -1;
        var rootSeen = false;
        var packagesSeen = false;
        long? declaredConditions = null;
        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            Require(reader.Depth <= 64, "XML nesting is too deep.");
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == classDepth)
                {
                    path = null;
                    classIdentity = null;
                    classDepth = -1;
                }
                if (reader.Depth == linesDepth) linesDepth = -1;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            Require(reader.NamespaceURI.Length == 0, "Cobertura namespaces are not supported.");
            if (!rootSeen)
            {
                Require(reader.Name == "coverage", "Missing Cobertura coverage root.");
                rootSeen = true;
                report.CodeData.ConditionsMeasured = reader.GetAttribute("branches-valid") is not null;
                report.CodeData.BranchesMeasured = report.CodeData.ConditionsMeasured;
                if (reader.GetAttribute("branches-valid") is { } declared)
                {
                    declaredConditions = long.Parse(declared, CultureInfo.InvariantCulture);
                    Require(declaredConditions >= 0, "Invalid Cobertura branch total.");
                }
            }
            if (reader.Name == "packages" && reader.Depth == 1) packagesSeen = true;
            if (reader.Name == "class")
            {
                // Merging coverage across multiple projects/drives (e.g. reportgenerator
                // combining several dotnet test runs) can emit a class whose filename falls
                // outside the declared source_root; that one class's coverage is unmeasured,
                // not a reason to fail the whole report.
                try
                {
                    path = SafePath(
                        SourcePaths.Relative(reader.GetAttribute("filename") ?? "", sourceRoot)
                    );
                }
                catch (InvalidDataException)
                {
                    path = null;
                }
                classIdentity = path is null ? null : $"{reader.GetAttribute("name") ?? path}#{++classIndex}";
                if (path is not null)
                {
                    Add(report.CodeData.SourceFiles, path);
                    // dotnet-coverage's cyclomatic complexity is undefined for some classes (e.g.
                    // records/property-only types with no branches) and comes out as complexity="NaN"
                    // rather than being omitted; that one class's complexity is unmeasured, not a
                    // reason to fail the whole report.
                    if (reader.GetAttribute("complexity") is { } complexity
                        && double.Parse(complexity, CultureInfo.InvariantCulture) is { } value
                        && double.IsFinite(value) && value >= 0)
                        Add(report.CodeData.Metrics, new CodeFileMetric(path, "class_complexity", value, reader.GetAttribute("name") ?? path));
                }
                classDepth = reader.IsEmptyElement ? -1 : reader.Depth;
            }
            else if (reader.Name == "lines" && classDepth >= 0 && reader.Depth == classDepth + 1)
                linesDepth = reader.IsEmptyElement ? -1 : reader.Depth;
            else if (reader.Name == "line" && linesDepth >= 0 && reader.Depth == linesDepth + 1)
            {
                var number = int.Parse(reader.GetAttribute("number") ?? "", CultureInfo.InvariantCulture);
                var hits = long.Parse(reader.GetAttribute("hits") ?? "", CultureInfo.InvariantCulture);
                Require(number > 0 && hits >= 0, "Invalid coverage line.");
                if (path is not null) Add(report.Coverage, new ImportedCoverage(path, number, hits > 0));
                var branch = reader.GetAttribute("branch");
                var isBranch = false;
                Require(
                    branch is null || bool.TryParse(branch, out isBranch),
                    "Invalid Cobertura branch flag."
                );
                if (isBranch)
                {
                    report.CodeData.ConditionsMeasured = true;
                    var condition = reader.GetAttribute("condition-coverage") ?? throw new InvalidDataException("Missing Cobertura condition counts.");
                    var start = condition.IndexOf('('); var end = condition.IndexOf(')');
                    Require(start >= 0 && end > start, "Missing Cobertura condition numerator/denominator.");
                    var counts = condition[(start + 1)..end].Split('/');
                    Require(counts.Length == 2, "Invalid Cobertura condition counts.");
                    var covered = int.Parse(counts[0], CultureInfo.InvariantCulture); var total = int.Parse(counts[1], CultureInfo.InvariantCulture);
                    Require(total > 0 && covered >= 0 && covered <= total, "Invalid Cobertura condition counts.");
                    if (path is not null)
                    {
                        Add(
                            report.CodeData.Branches,
                            new CodeBranch(path, number, "conditions", classIdentity!, covered, total)
                        );
                        Add(
                            report.CodeData.Branches,
                            new CodeBranch(path, number, "branches", classIdentity!, covered, total)
                        );
                    }
                }
            }
        }
        Require(rootSeen && packagesSeen, "Missing Cobertura packages.");
        if (declaredConditions is { } expected)
        {
            var observed = report.CodeData.Branches
                .Where(x => x.Kind == "conditions")
                .Sum(x => (long)x.Total);
            if (observed != expected)
            {
                // Cobertura groups coverage by <class> (one per type), not by physical
                // file/line: a file with several types (partial classes, source-generated
                // members) gets one <class> per type, and overlapping branch lines get
                // summed once per type here — while the root branches-valid total counts
                // each physical branch once. Confirmed in production (this repo's own
                // apps/api/ApiJson.cs alone has 4 <class> entries) as a consistent 2x
                // overcount; docs/integration.md also documents a 0-branch-detail export
                // shape for executable-lines-only Cobertura. Neither is data corruption
                // worth failing the whole report over — branches/conditions are genuinely
                // unmeasured whenever the two disagree, for any reason.
                report.CodeData.ConditionsMeasured = false;
                report.CodeData.BranchesMeasured = false;
            }
        }
    }

    private static async Task LcovAsync(
        Stream stream,
        ImportedReport report,
        string sourceRoot,
        CancellationToken ct
    )
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        string? path = null;
        var records = 0;
        long? declaredBranches = null;
        var branchIdentities = new HashSet<(int Line, string Block, string Branch)>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            Require(line.Length <= 65536, "LCOV line too long.");
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                Require(path is null, "Unterminated LCOV record.");
                path = SafePath(SourcePaths.Relative(line[3..], sourceRoot));
                Add(report.CodeData.SourceFiles, path);
                declaredBranches = null;
                branchIdentities.Clear();
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal))
            {
                var values = line[3..].Split(',');
                Require(path is not null && values.Length is 2 or 3, "Invalid LCOV line.");
                var number = int.Parse(values[0], CultureInfo.InvariantCulture);
                var hits = long.Parse(values[1], CultureInfo.InvariantCulture);
                Require(number > 0 && hits >= 0, "Invalid coverage line.");
                Add(report.Coverage, new ImportedCoverage(path!, number, hits > 0));
            }
            else if (line.StartsWith("BRDA:", StringComparison.Ordinal))
            {
                report.CodeData.BranchesMeasured = true;
                var values = line[5..].Split(',');
                Require(path is not null && values.Length == 4, "Invalid LCOV branch.");
                var number = int.Parse(values[0], CultureInfo.InvariantCulture);
                Require(number > 0 && values[1].Length is > 0 and <= 100 && values[2].Length is > 0 and <= 100, "Invalid LCOV branch identity.");
                var hits = values[3] == "-" ? 0 : long.Parse(values[3], CultureInfo.InvariantCulture);
                Require(hits >= 0, "Invalid LCOV branch hits.");
                Add(report.CodeData.Branches, new CodeBranch(path!, number, "branches", values[1] + ":" + values[2], hits > 0 ? 1 : 0, 1));
                branchIdentities.Add((number, values[1], values[2]));
            }
            else if (line.StartsWith("BRF:", StringComparison.Ordinal))
            {
                var total = long.Parse(line[4..], CultureInfo.InvariantCulture);
                Require(path is not null && total >= 0, "Invalid LCOV branch total.");
                report.CodeData.BranchesMeasured = true;
                declaredBranches = total;
            }
            else if (line == "end_of_record")
            {
                Require(path is not null, "Missing LCOV source file.");
                Require(declaredBranches is null || declaredBranches == branchIdentities.Count, "LCOV branch details do not match declared total.");
                path = null;
                records++;
            }
            else Require(line.Length == 0 || LcovRecordPrefixes.Any(line.StartsWith), "Unknown LCOV record.");
        }
        Require(path is null && records > 0, "Incomplete or empty LCOV document.");
    }

    private static void CycloneDx(JsonElement root, ImportedReport report, string version, CancellationToken ct)
    {
        Require(Text(root, "bomFormat") == "CycloneDX" && Text(root, "specVersion") == version, "CycloneDX version mismatch.");
        var rootComponent = Property(Property(root, "metadata"), "component");
        string? rootRef = null;
        if (rootComponent.ValueKind == JsonValueKind.Object) rootRef = AddComponent(rootComponent);
        foreach (var component in Array(root, "components")) AddComponent(component);
        var known = report.Components.Select(x => x.Reference).ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in Array(root, "dependencies"))
        {
            var from = RequiredText(dependency, "ref");
            foreach (var target in Array(dependency, "dependsOn"))
            {
                var to = target.GetString() ?? throw new InvalidDataException("Invalid dependency reference.");
                Add(report.Dependencies, new ImportedDependency(from, to, "depends_on"));
            }
        }
        ClassifyRelationships(report, rootRef is null ? [] : [rootRef]);
        foreach (var vulnerability in Array(root, "vulnerabilities"))
        {
            var id = RequiredText(vulnerability, "id");
            var source = Text(Property(vulnerability, "source"), "name") ?? Text(Property(vulnerability, "source"), "url") ?? "report";
            var severity = Array(vulnerability, "ratings").Select(x => Text(x, "severity")).FirstOrDefault(x => x is not null);
            var dateText = Text(vulnerability, "published");
            DateTimeOffset? date = dateText is null ? null : DateTimeOffset.Parse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            foreach (var affect in Array(vulnerability, "affects"))
            {
                var reference = RequiredText(affect, "ref");
                Require(known.Contains(reference), "Vulnerability references an unknown component.");
                Add(report.Vulnerabilities, new ImportedVulnerability(reference, id, severity, source, date));
            }
        }

        string AddComponent(JsonElement component)
        {
            ct.ThrowIfCancellationRequested();
            var reference = Text(component, "bom-ref") ?? $"anonymous:{report.Components.Count}";
            var licenses = Array(component, "licenses").Select(x => Text(x, "expression") ?? Text(Property(x, "license"), "id") ?? Text(Property(x, "license"), "name") ?? throw new InvalidDataException("Invalid license.")).ToList();
            var purl = Text(component, "purl");
            Add(report.Components, new ImportedComponent(reference, RequiredText(component, "name"), Text(component, "version"), purl, Ecosystem(purl), licenses, "unknown"));
            foreach (var child in Array(component, "components")) AddComponent(child);
            return reference;
        }
    }

    private static void Spdx(JsonElement root, ImportedReport report, CancellationToken ct)
    {
        Require(Text(root, "spdxVersion") == "SPDX-2.3", "SPDX version mismatch.");
        var documentId = RequiredText(root, "SPDXID");
        foreach (var package in Array(root, "packages", true))
        {
            ct.ThrowIfCancellationRequested();
            var purl = Array(package, "externalRefs").Where(x => Text(x, "referenceType") == "purl").Select(x => Text(x, "referenceLocator")).FirstOrDefault();
            var license = Text(package, "licenseDeclared");
            Add(report.Components, new ImportedComponent(RequiredText(package, "SPDXID"), RequiredText(package, "name"), Text(package, "versionInfo"), purl, Ecosystem(purl), license is null ? [] : [license], "unknown"));
        }
        var roots = Array(root, "documentDescribes").Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var relationship in Array(root, "relationships"))
        {
            var from = RequiredText(relationship, "spdxElementId");
            var to = RequiredText(relationship, "relatedSpdxElement");
            var type = RequiredText(relationship, "relationshipType");
            if (type == "DESCRIBES" && from == documentId) roots.Add(to);
            if (type == "DESCRIBED_BY" && to == documentId) roots.Add(from);
            if (type == "DEPENDENCY_OF") (from, to) = (to, from);
            Add(report.Dependencies, new ImportedDependency(from, to, type is "DEPENDS_ON" or "DEPENDENCY_OF" ? "depends_on" : type.ToLowerInvariant()));
        }
        ClassifyRelationships(report, roots);
    }

    private static void ClassifyRelationships(ImportedReport report, IEnumerable<string> roots)
    {
        var rootSet = roots.ToHashSet(StringComparer.Ordinal);
        var edges = report.Dependencies.Where(x => x.Relationship == "depends_on").ToLookup(x => x.From, x => x.To, StringComparer.Ordinal);
        var direct = rootSet.SelectMany(x => edges[x]).ToHashSet(StringComparer.Ordinal);
        var reached = new HashSet<string>(rootSet, StringComparer.Ordinal);
        var queue = new Queue<string>(rootSet);
        while (queue.TryDequeue(out var current))
            foreach (var child in edges[current]) if (reached.Add(child)) queue.Enqueue(child);
        for (var i = 0; i < report.Components.Count; i++)
        {
            var component = report.Components[i];
            report.Components[i] = component with { Relationship = rootSet.Contains(component.Reference) ? "root" : direct.Contains(component.Reference) ? "direct" : reached.Contains(component.Reference) ? "transitive" : "unknown" };
        }
    }

    private static string SafePath(string path)
    {
        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        Require(path.Length is > 0 and <= 4096 && !path.StartsWith('/') && !path.Contains(':') && !path.Any(char.IsControl), "Expected a repository-relative path.");
        var parts = path.Split('/').Where(x => x != "." && x.Length > 0).ToArray();
        Require(parts.Length > 0 && !parts.Contains(".."), "Path traversal is not allowed.");
        return string.Join('/', parts);
    }

    private static string? Ecosystem(string? purl)
    {
        if (purl is null) return null;
        Require(purl.StartsWith("pkg:", StringComparison.Ordinal) && purl.IndexOf('/') > 4, "Invalid package URL.");
        return purl[4..purl.IndexOf('/')];
    }

    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    private static string? Text(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.String } child ? child.GetString() : null;
    private static string RequiredText(JsonElement value, string key) => Text(value, key) is { Length: > 0 } text ? text : throw new InvalidDataException($"Missing {key}.");
    private static int? Number(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.Number } child ? child.GetInt32() : null;
    private static JsonElement.ArrayEnumerator Array(JsonElement value, string key, bool required = false)
    {
        var array = Property(value, key);
        if (!required && array.ValueKind == JsonValueKind.Undefined) return EmptyArray.EnumerateArray();
        Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= MaxEntries, $"Invalid {key} array.");
        return array.EnumerateArray();
    }
    private static void Add<T>(List<T> list, T value)
    {
        Require(list.Count < MaxEntries, "Report has too many normalized entries.");
        list.Add(value);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
