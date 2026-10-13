using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeInsights.Api.Services;

public static partial class AnalysisFindings
{
    public static string SeverityBucket(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "critical" or "error" or "blocker" => "blocker",
            "high" => "high",
            "warning" or "medium" => "medium",
            "low" or "convention" => "low",
            _ => "info",
        };

    public static readonly string[] ScaPrefixes =
    [
        "trivy/",
        "dotnet-audit/",
        "composer-audit/",
        "npm-audit/",
        "pip-audit/",
    ];

    public static string Text(JsonElement element, string key, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : fallback;

    public static int? Line(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
        && number != 0
            ? number
            : null;

    public static IEnumerable<(string Path, JsonElement Issue)> Enumerate(string? raw)
    {
        foreach (var (path, data) in GateEvaluation.Parse<Dictionary<string, JsonElement>>(raw, []))
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var issue in data.EnumerateArray())
                    if (issue.ValueKind == JsonValueKind.Object)
                        yield return (path, issue);
    }

    [GeneratedRegex(@"^https://[^/@]+@")]
    private static partial Regex Credentials();

    [GeneratedRegex(@"/_git/([^/?#]+)")]
    private static partial Regex RepoName();

    [GeneratedRegex(@"^(?:/home/vsts/work|/mnt/vss/_work|/agent/_work)/\d+/[^/]+/")]
    private static partial Regex AgentPrefix();

    [GeneratedRegex(@"^\d+>")]
    private static partial Regex BuildPrefix();

    public static string? RepoFileUrl(
        string provider,
        string repo,
        string commit,
        string path,
        int? start,
        int? end
    )
    {
        if (repo.Length == 0 || provider != "azure-devops")
            return null;
        repo = Credentials().Replace(repo, "https://");
        if (
            !Uri.TryCreate(repo, UriKind.Absolute, out var repository)
            || repository.Scheme != Uri.UriSchemeHttps
            || !repository.IsDefaultPort
            || repository.Query.Length > 0
            || repository.Fragment.Length > 0
            || !(
                repository.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                || repository.Host.EndsWith(
                    ".visualstudio.com",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
            return null;
        var segments = repository.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (
            repository.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                ? segments.Length != 4 || segments[2] != "_git"
                : segments.Length != 3 || segments[1] != "_git"
        )
            return null;
        var match = RepoName().Match(repo);
        var separator = match.Success
            ? Regex.Match(path, @"[\\/]" + Regex.Escape(match.Groups[1].Value) + @"[\\/]")
            : Match.Empty;
        var relative = separator.Success
            ? path[(separator.Index + separator.Length)..]
            : AgentPrefix().Replace(path, "");
        relative = BuildPrefix().Replace(relative, "");
        var query = new Dictionary<string, string>
        {
            ["path"] = "/" + relative.TrimStart('/', '\\'),
        };
        if (commit.Length > 0)
            query["version"] = "GC" + commit;
        if (start is > 0)
        {
            query["line"] = start.Value.ToString();
            query["lineEnd"] = ((end ?? start.Value) + 1).ToString();
            query["lineStartColumn"] = "1";
            query["lineEndColumn"] = "1";
            query["lineStyle"] = "plain";
        }
        query["_a"] = "contents";
        return repo.TrimEnd('/')
            + "?"
            + string.Join(
                '&',
                query.Select(x =>
                    System.Net.WebUtility.UrlEncode(x.Key)
                    + "="
                    + System.Net.WebUtility.UrlEncode(x.Value)
                )
            );
    }
}
