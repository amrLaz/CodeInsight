using CodeInsights.Api.Services;

public sealed class BranchRefTests
{
    [Fact]
    public void All() => Run();

    private static void Run()
    {
        // Providers report the full ref; a bare name is promoted to it rather than stored apart.
        Assert.True(BranchRefs.Canonical("refs/heads/develop") == "refs/heads/develop", "a ref is left alone");
        Assert.True(BranchRefs.Canonical("develop") == "refs/heads/develop", "a bare name becomes a ref");
        Assert.True(BranchRefs.Canonical(" feature/gct/x ") == "refs/heads/feature/gct/x", "a slashed name keeps its slashes");
        Assert.True(BranchRefs.Canonical("refs/pull/12/merge") == "refs/pull/12/merge", "a pull ref is not a head");
        Assert.True(BranchRefs.Canonical(null) == "", "an absent branch stays absent");
        Assert.True(BranchRefs.Canonical("   ") == "", "a blank branch stays absent");

        Assert.True(BranchRefs.Short("refs/heads/develop") == "develop", "heads prefix is dropped");
        Assert.True(BranchRefs.Short("refs/remotes/origin/develop") == "develop", "remote and its prefix are dropped");
        Assert.True(BranchRefs.Short("refs/remotes/origin") == "refs/remotes/origin", "a remote without a branch is not a branch");
        Assert.True(BranchRefs.Short("refs/pull/12/merge") == "refs/pull/12/merge", "an unknown ref shape is kept whole");
        Assert.True(BranchRefs.Short("develop") == "develop", "a bare name is already short");

        // Reads accept either spelling: rows written before normalization keep the short one.
        Assert.True(
            BranchRefs.Candidates("develop") is ["refs/heads/develop", "develop"],
            "a bare name matches both spellings"
        );
        Assert.True(
            BranchRefs.Candidates("refs/heads/develop") is ["refs/heads/develop", "develop"],
            "a ref matches both spellings"
        );
        Assert.True(BranchRefs.Candidates("refs/pull/12/merge") is ["refs/pull/12/merge"], "a pull ref has one spelling");
        Assert.True(BranchRefs.Candidates("") is [], "no branch filters nothing");
        Console.WriteLine("Branch ref checks passed.");
    }

}
