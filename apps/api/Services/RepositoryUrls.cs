namespace CodeInsights.Api.Services;

/// <summary>Canonicalizes repository identities received from CI providers.</summary>
public static partial class RepositoryUrls
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:[^@/\\\s]+@)?([^@:/\\\s]+):(?!//)(.+)$")]
    private static partial System.Text.RegularExpressions.Regex ScpLikeUrl();

    [System.Text.RegularExpressions.GeneratedRegex(@"\.git$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GitSuffix();

    /// <summary>Removes the harmless Azure Repos clone username while rejecting credential-bearing or ambiguous URLs.</summary>
    public static string Normalize(string provider, string? value)
    {
        var input = value?.Trim() ?? "";
        if (input.Length == 0 || provider != "azure-devops")
            return input;
        if (
            !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
            throw new ApiException(
                422,
                "Azure DevOps repository_url must be an HTTPS dev.azure.com repository URL without query or fragment"
            );

        string[] segments;
        string userInfo;
        try
        {
            segments = Uri.UnescapeDataString(uri.AbsolutePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            userInfo = Uri.UnescapeDataString(uri.UserInfo);
        }
        catch (UriFormatException)
        {
            throw new ApiException(422, "Azure DevOps repository_url contains invalid escaping");
        }

        if (
            segments.Length != 4
            || !segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase)
            || userInfo.Contains(':')
            || userInfo.Length > 0
                && !userInfo.Equals(segments[0], StringComparison.OrdinalIgnoreCase)
        )
            throw new ApiException(
                422,
                "Azure DevOps repository_url must identify /organization/project/_git/repository and must not contain credentials"
            );

        return "https://dev.azure.com/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Builds a comparison key for a repository URL supplied by a workstation. Clone URLs of the
    /// same repository differ by scheme, credentials, SSH form and the <c>.git</c> suffix, so an
    /// exact string match would silently fail to resolve the project.
    /// </summary>
    public static string Identity(string? value)
    {
        var input = (value ?? "").Trim();
        if (input.Length == 0)
            return "";
        var scp = ScpLikeUrl().Match(input);
        if (scp.Success)
            input = "ssh://" + scp.Groups[1].Value + "/" + scp.Groups[2].Value;
        else if (!input.Contains("://", StringComparison.Ordinal))
            input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return "";
        string[] segments;
        try
        {
            segments = Uri.UnescapeDataString(uri.AbsolutePath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (UriFormatException)
        {
            return "";
        }
        if (segments.Length == 0)
            return "";
        segments[^1] = GitSuffix().Replace(segments[^1], "");
        var host = uri.Host.ToLowerInvariant();
        // Azure Repos publishes SSH clone URLs as ssh.dev.azure.com/v3/org/project/repository
        // while the integration stores the HTTPS /org/project/_git/repository form.
        if (host == "ssh.dev.azure.com" && segments.Length == 4 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
        {
            host = "dev.azure.com";
            segments = [segments[1], segments[2], "_git", segments[3]];
        }
        return host + "/" + string.Join('/', segments).ToLowerInvariant();
    }
}
