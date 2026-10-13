using System.Text;
using System.Text.Json;
using CodeInsights.Api.Services;

public sealed class FindingEvidenceTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        const string report = """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"scanner","semanticVersion":"2.3.0","rules":[{"id":"R1","helpUri":"https://docs.example.org/rules/R1"}]}},"results":[{"ruleId":"R1","message":{"text":"Original message"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/a.cs"},"region":{"startLine":4,"endLine":5,"startColumn":2,"snippet":{"text":"DO NOT RETAIN"}}}}],"relatedLocations":[{"physicalLocation":{"artifactLocation":{"uri":"src/b.cs"},"region":{"startLine":9}}}],"codeFlows":[{"threadFlows":[{"locations":[{"location":{"physicalLocation":{"artifactLocation":{"uri":"src/c.cs"},"region":{"startLine":12}}}}]}]}]}]}]}
            """;
        var parsed = await Parse(report);
        var evidence = parsed.Findings.Single().Evidence!;
        Assert.True(
            evidence.DriverVersion == "2.3.0" && evidence.DocumentationVersion is null,
            "Analyzer version does not invent documentation version"
        );
        Assert.True(
            evidence.DocumentationUrl == "https://docs.example.org/rules/R1"
                && evidence.DocumentationStatus == "producer_link_unverified",
            "Documentation attributed without claiming availability"
        );
        Assert.True(
            evidence.Locations
                is [
                    { Kind: "primary", Line: 4, EndLine: 5, Column: 2 },
                    { Kind: "related", Line: 9 },
                    { Kind: "flow", Flow: 0, Thread: 0, Step: 0, Line: 12 },
                ],
            "SARIF regions, related locations and flow grouping retained"
        );
        Assert.True(
            !JsonSerializer.Serialize(evidence).Contains("DO NOT RETAIN"),
            "Source snippets never retained"
        );
        var unknown = await Parse(report.Replace("\"id\":\"R1\"", "\"id\":\"OTHER\""));
        Assert.True(
            unknown.Findings.Single().Evidence!.DocumentationStatus == "unavailable",
            "Unknown rules have no invented documentation"
        );
        var extension = await Parse(
            report.Replace(
                "\"ruleId\":\"R1\"",
                "\"ruleId\":\"R1\",\"rule\":{\"id\":\"R1\",\"toolComponent\":{\"index\":0}}"
            )
        );
        Assert.True(
            extension.Findings.Single().Evidence!.DocumentationUrl is null,
            "Extension rule must not inherit driver documentation by matching identifier"
        );
        foreach (
            var url in new[]
            {
                "javascript:alert(1)",
                "http://docs.example.org/rule",
                "https://secret:password@docs.example.org/rule",
            }
        )
        {
            var unsafeLink = await Parse(report.Replace("https://docs.example.org/rules/R1", url));
            Assert.True(
                unsafeLink.Findings.Single().Evidence!.DocumentationUrl is null,
                "Unsafe documentation does not break ingestion"
            );
        }
        var commit = new string('a', 40);
        const string repository = "https://dev.azure.com/org/project/_git/repo";
        var source = FindingEvidenceLinks.AzureDevOpsSource(repository, commit, "src/a b.cs", 4);
        Assert.True(
            source is not null
                && source.Contains("version=GC" + commit)
                && source.Contains("path=%2Fsrc%2Fa%20b.cs")
                && source.Contains("line=4&lineEnd=5"),
            "ADO source pinned to exact commit and line"
        );
        Assert.True(
            FindingEvidenceLinks.AzureDevOpsSource(repository, "abcdef", "src/a.cs", 4) is null,
            "Abbreviated commit must not create source link"
        );
        foreach (
            var path in new[] { "../secrets", "/absolute", "https://other/path", "src/../secrets" }
        )
            Assert.True(
                FindingEvidenceLinks.AzureDevOpsSource(repository, commit, path, 4) is null,
                "Unsafe source path rejected"
            );
        Assert.True(
            FindingEvidenceLinks.AzureDevOpsSource(
                "https://dev.azure.com.evil.org/org/project/_git/repo",
                commit,
                "a.cs",
                1
            )
                is null,
            "Lookalike ADO host rejected"
        );
        Assert.True(
            FindingEvidenceLinks.AzureDevOpsSource(repository + "?token=secret", commit, "a.cs", 1)
                is null,
            "Repository query not copied into source link"
        );
        var basePath = await Parse(
            report.Replace("\"uri\":\"src/a.cs\"", "\"uri\":\"src/a.cs\",\"uriBaseId\":\"SOURCE\"")
        );
        Assert.True(
            !basePath.Findings.Single().Evidence!.Locations[0].RepositoryRelative,
            "Unresolved URI base must not claim repository mapping"
        );
        using var many = JsonDocument.Parse(
            "{\"relatedLocations\":[" + string.Join(',', Enumerable.Repeat("{}", 201)) + "]}"
        );
        var bounded = FindingEvidenceLinks.FromSarif(default, default, many.RootElement, []);
        Assert.True(
            bounded.Locations.Length == 200 && bounded.LocationsTruncated,
            "Long traces bounded and truncation explicit"
        );
        var oversizedVersion = await Parse(report.Replace("2.3.0", new string('v', 201)));
        Assert.True(
            oversizedVersion.Findings.Single().Evidence!.DriverVersion is null,
            "Oversized producer version not persisted"
        );
        long used = 0;
        var serialized = FindingEvidenceLinks.Serialize(evidence, ref used);
        Assert.True(
            used == Encoding.UTF8.GetByteCount(serialized),
            "Evidence budget counts persisted UTF-8 JSON"
        );
        var firstSize = used;
        try
        {
            FindingEvidenceLinks.Serialize(evidence, ref used, firstSize * 2 - 1);
            throw new InvalidOperationException("Expected cumulative evidence budget rejection");
        }
        catch (InvalidDataException) { }
        Assert.True(used == firstSize, "Rejected evidence leaves budget unchanged");
        FindingEvidenceLinks.Serialize(evidence, ref used, firstSize * 2);
        Assert.True(used == firstSize * 2, "Exact cumulative limit remains valid");
        foreach (
            var (root, absolutePath) in new[]
            {
                ("C:/agent/source", "file:///C:/agent/source/src/a.cs"),
                ("C:\\agent\\source", "C:/agent/source/src/a.cs"),
                ("file:///C:/agent/source", "file:///c:/AGENT/source/src/a.cs"),
                ("/agent/source", "file:///agent/source/src/a.cs"),
                ("file:///agent/source", "/agent/source/src/a.cs"),
            }
        )
        {
            var rooted = await Parse(report.Replace("src/a.cs", absolutePath), root);
            Assert.True(
                rooted.Findings.Single().Path == "src/a.cs"
                    && rooted.Findings.Single().Evidence!.Locations[0].RepositoryRelative
                    && rooted.Findings.Single().Evidence!.Locations[0].Path == "src/a.cs",
                "Explicit source root maps native URI and proves relative evidence"
            );
        }
        foreach (
            var absolutePath in new[]
            {
                "file:///C:/agent/source-other/a.cs",
                "file:///C:/agent/source/../a.cs",
                "file:///C:/agent/source/%2e%2e/a.cs",
                "file:///C:/agent/source/%252e%252e/a.cs",
                "file://server/share/a.cs",
                "file:///C:/agent/source/a.cs?token=x",
                "file:///C:/agent/source/a.cs%23secret",
                "C:/other/a.cs",
                "C:/agent/source",
            }
        )
        {
            try
            {
                await Parse(report.Replace("src/a.cs", absolutePath), "C:/agent/source");
                throw new InvalidOperationException(
                    "Expected invalid rooted source path rejection"
                );
            }
            catch (InvalidDataException) { }
        }
        try
        {
            await Parse(report.Replace("src/a.cs", "file:///C:/agent/source/src/a.cs"));
            throw new InvalidOperationException(
                "Expected absolute path rejection without source root"
            );
        }
        catch (InvalidDataException) { }
        foreach (
            var root in new[]
            {
                "relative/root",
                "//server/share",
                "file://server/share",
                "/root/../other",
                "C:/root?query",
                "https://host/root",
            }
        )
        {
            try
            {
                SourcePaths.ValidateRoot(root);
                throw new InvalidOperationException("Expected invalid source root rejection");
            }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("Finding evidence checks passed.");
    }

    private static Task<ImportedReport> Parse(string report, string sourceRoot = "") =>
        ReportImports.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(report)),
            "sarif",
            "2.1.0",
            "scanner",
            CancellationToken.None,
            sourceRoot
        );

}
