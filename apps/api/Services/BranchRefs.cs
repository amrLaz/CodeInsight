namespace CodeInsights.Api.Services;

/// <summary>
/// Canonicalizes branch identities. CI providers report the full ref
/// (<c>Build.SourceBranch</c> = <c>refs/heads/x</c>) while Git clients and humans use the
/// short name; the two used to be stored and compared as different branches, so a report
/// ingested by the pipeline was invisible to a client asking for <c>x</c>.
/// The full ref is the stored form — it is what the platform already holds and what the
/// providers send — and reads accept either spelling.
/// </summary>
public static class BranchRefs
{
    /// <summary>Stored form of a branch: the full ref, with a bare name promoted to <c>refs/heads/</c>.</summary>
    public static string Canonical(string? value)
    {
        var input = (value ?? "").Trim().Trim('/');
        if (input.Length == 0)
            return "";
        return input.StartsWith("refs/", StringComparison.Ordinal) ? input : $"refs/heads/{input}";
    }

    /// <summary>Display form: the ref without its <c>refs/heads/</c> or <c>refs/remotes/&lt;remote&gt;/</c> prefix.</summary>
    public static string Short(string? value)
    {
        var input = (value ?? "").Trim().Trim('/');
        if (input.StartsWith("refs/heads/", StringComparison.Ordinal))
            return input["refs/heads/".Length..];
        if (input.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            var rest = input["refs/remotes/".Length..];
            var slash = rest.IndexOf('/');
            // A remote without a branch after it is not a branch name; keep the ref as is.
            if (slash > 0 && slash < rest.Length - 1)
                return rest[(slash + 1)..];
        }
        return input;
    }

    /// <summary>
    /// Spellings a stored row may legitimately carry for this branch. Rows written before
    /// normalization keep the short form, so a read matches on both rather than reporting
    /// an analysed branch as never analysed.
    /// </summary>
    public static string[] Candidates(string? value)
    {
        var canonical = Canonical(value);
        if (canonical.Length == 0)
            return [];
        var shortName = Short(canonical);
        return shortName.Length == 0 || shortName == canonical ? [canonical] : [canonical, shortName];
    }
}
