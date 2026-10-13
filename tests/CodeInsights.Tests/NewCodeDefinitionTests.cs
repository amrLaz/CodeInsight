using CodeInsights.Api.Data;
using CodeInsights.Api.Services;

public sealed class NewCodeDefinitionTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        Assert.True(
            NewCode.ConventionalMainBranch("main") == "refs/heads/main",
            "main is detected as a conventional main branch"
        );
        Assert.True(
            NewCode.ConventionalMainBranch("refs/heads/master") == "refs/heads/master",
            "master is detected as a conventional main branch"
        );
        Assert.True(
            NewCode.ConventionalMainBranch("feature/main") is null,
            "feature branches are not guessed as the main branch"
        );
        var expected = new Dictionary<string, string[]>
        {
            ["global"] = ["previous_version", "number_of_days"],
            ["project"] = ["previous_version", "number_of_days", "reference_branch"],
            ["branch"] = ["previous_version", "number_of_days", "reference_branch", "specific_analysis"],
        };
        foreach (var level in expected.Keys)
            foreach (var mode in new[] { "previous_version", "number_of_days", "reference_branch", "specific_analysis" })
                Assert.True(NewCode.ModeAllowed(level, mode) == expected[level].Contains(mode), $"{level} x {mode} matrix");

        var definitions = new[]
        {
            new NewCodeDefinition { Level = "global", Mode = "number_of_days", NumberOfDays = 30 },
            new NewCodeDefinition { Level = "project", Mode = "reference_branch", ReferenceBranch = "refs/heads/develop" },
            new NewCodeDefinition { Level = "branch", Branch = "refs/heads/feature", Mode = "specific_analysis", AnalysisId = "analysis" },
        };
        var branch = NewCode.SelectEffective(definitions, "feature", null, null);
        Assert.True(branch.Source == "branch" && branch.Definition.Mode == "specific_analysis", "branch override precedence");
        var project = NewCode.SelectEffective(definitions, "other", null, null);
        Assert.True(project.Source == "project" && project.Definition.ReferenceBranch == "refs/heads/develop", "project override precedence");
        var manifest = NewCode.SelectEffective(definitions, "feature", null, "release");
        Assert.True(manifest.Source == "manifest_override" && manifest.Definition.ReferenceBranch == "refs/heads/release", "manifest override precedence");
        var pullRequest = NewCode.SelectEffective(definitions, "feature", "main", "release");
        Assert.True(pullRequest.Source == "pull_request_target" && pullRequest.Definition.ReferenceBranch == "refs/heads/main", "pull request target precedence");
        Console.WriteLine("New Code definition checks passed.");
    }

}
