using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;

public sealed class PullRequestDecorationTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        var manifest = Manifest([2, 5, 9]);
        var selection = PullRequestIssues.Select(
            manifest,
            [
                Finding(1, "medium", "src/app.cs", 2),
                Finding(2, "high", "src/app.cs", 5),
                Finding(3, "info", "src\\app.cs", 9),
                Finding(4, "critical", "src/app.cs", 4),
                Finding(5, "blocker", null, 5),
                Finding(6, "warning", "src/app.cs", null),
                Finding(7, "error", "src/other.cs", 5),
                Finding(8, "error", "src/app.cs", 5, excepted: true),
            ]
        );
        Assert.True(
            selection.EligibleCount == 3
                && selection.Issues.Select(x => x.Id).SequenceEqual([2L, 1L, 3L]),
            "only added-line findings are ordered by severity then location"
        );

        foreach (var count in new[] { 0, 49, 50, 75 })
        {
            var boundary = PullRequestIssues.Select(
                Manifest(Enumerable.Range(1, count).ToArray()),
                Enumerable.Range(1, count).Select(id => Finding(id, "warning", "src/app.cs", id))
            );
            Assert.True(
                boundary.Issues.Count == Math.Min(49, count)
                    && boundary.EligibleCount == count
                    && boundary.OmittedCount == Math.Max(0, count - 49),
                $"decoration boundary {count}"
            );
            var azureComments = boundary.Issues.Count + (boundary.OmittedCount > 0 ? 1 : 0);
            Assert.True(azureComments <= 50, $"Azure comment cap {count}");
        }

        var truncated = PullRequestIssues.Select(
            Manifest(Enumerable.Range(1, 50).ToArray()),
            Enumerable.Range(1, 50).Select(id => Finding(id, "warning", "src/app.cs", id))
        );
        var url = PullRequestDetails.Url("https://codeinsights.example/", "analysis/id ?");
        var azureCta = PullRequestDetails.Truncation(truncated, "shown here", url);
        var githubCta = PullRequestDetails.Truncation(truncated, "annotated", url);
        Assert.True(
            url == "https://codeinsights.example/?analysis_id=analysis%2Fid%20%3F"
                && azureCta.Contains("Only 49 of 50")
                && azureCta.Contains("1 additional finding is available.")
                && azureCta.Contains(url)
                && githubCta.Contains("are annotated"),
            "provider truncation messages use the encoded analysis link"
        );
        Assert.True(
            GitHubDecorator.PendingAnnotations(Enumerable.Range(1, 49).ToList(), 47)
                .SequenceEqual([48, 49])
                && GitHubDecorator.PendingAnnotations(Enumerable.Range(1, 49).ToList(), 49)
                    .Count == 0
                && GitHubDecorator.PendingAnnotations(Enumerable.Range(1, 49).ToList(), 50)
                    .Count == 0,
            "GitHub annotation resume is idempotent"
        );

        Assert.True(
            PullRequestIssues.Select(Manifest([2]) with { GitDiff = new() }, [Finding(1, "high", "src/app.cs", 2)]).EligibleCount == 0,
            "unavailable diff decorates no findings"
        );
        Assert.True(
            PullRequestIssues.Select(
                Manifest([2]) with
                {
                    GitDiff = new()
                    {
                        State = "available",
                        BaseSha = "aaaaaa",
                        HeadSha = "bbbbbb",
                        Files = null!,
                    },
                },
                [Finding(1, "high", "src/app.cs", 2)]
            ).EligibleCount == 0,
            "incomplete diff decorates no findings"
        );
        Assert.True(
            PullRequestIssues.Select(Manifest([2], baseSha: "cccccc"), [Finding(1, "high", "src/app.cs", 2)]).EligibleCount == 0,
            "incoherent base decorates no findings"
        );
        Assert.True(
            PullRequestIssues.Select(Manifest([2], headSha: "dddddd"), [Finding(1, "high", "src/app.cs", 2)]).EligibleCount == 0,
            "incoherent head decorates no findings"
        );
        Assert.True(
            PullRequestIssues.Select(null, [Finding(1, "high", "src/app.cs", 2)]).EligibleCount == 0,
            "historical analysis without manifest decorates no findings"
        );

        Console.WriteLine("Pull request decoration checks passed.");
    }

    private static IngestionManifest Manifest(
        int[] addedLines,
        string baseSha = "aaaaaa",
        string headSha = "bbbbbb"
    ) =>
        new()
        {
            CommitSha = "bbbbbb",
            TargetSha = "aaaaaa",
            GitDiff = new()
            {
                State = "available",
                BaseSha = baseSha,
                HeadSha = headSha,
                Files = [new("src/app.cs", null, addedLines, [])],
            },
        };

    private static FindingOccurrence Finding(
        long id,
        string severity,
        string? path,
        int? line,
        bool excepted = false
    ) =>
        new()
        {
            Id = id,
            Severity = severity,
            Path = path,
            Line = line,
            Excepted = excepted,
        };

}
