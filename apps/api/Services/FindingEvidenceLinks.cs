using System.Text;
using System.Text.Json;

namespace CodeInsights.Api.Services;

/// <summary>Producer-supplied evidence, without source snippets or invented rule documentation.</summary>
public sealed record FindingEvidence(
    string? DriverVersion,
    string? DocumentationUrl,
    string DocumentationSource,
    string? DocumentationVersion,
    string DocumentationStatus,
    EvidenceLocation[] Locations,
    bool LocationsTruncated
);

/// <summary>A physical SARIF location; flow and thread indexes preserve trace grouping.</summary>
public sealed record EvidenceLocation(
    string Kind,
    int? Flow,
    int? Thread,
    int Step,
    string? Path,
    int? Line,
    int? EndLine,
    int? Column,
    int? EndColumn,
    bool RepositoryRelative
);

/// <summary>Extracts bounded optional metadata and builds commit-pinned Azure DevOps links without network calls.</summary>
public static class FindingEvidenceLinks
{
    /// <summary>Serializes evidence within one cumulative UTF-8 budget shared by every occurrence of an ingestion.</summary>
    public static string Serialize(
        FindingEvidence? evidence,
        ref long totalBytes,
        long maxBytes = 50 * 1024 * 1024
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        var json = evidence is null ? "{}" : JsonSerializer.Serialize(evidence, ApiJson.Options);
        var bytes = Encoding.UTF8.GetByteCount(json);
        // ponytail: a shared ingestion budget bounds repeated multi-location results; use normalized child rows if larger evidence is needed.
        if (totalBytes > maxBytes || bytes > maxBytes - totalBytes)
            throw new InvalidDataException(
                "Normalized finding evidence exceeds the ingestion UTF-8 byte budget."
            );
        totalBytes += bytes;
        return json;
    }

    /// <summary>Accepts only credential-free HTTPS links; availability and publisher ownership are not inferred.</summary>
    public static string? SafeHttps(string? value) =>
        value is { Length: > 0 and <= 2048 }
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : null;

    /// <summary>Links a relative path to a full immutable commit in a recognized Azure DevOps repository.</summary>
    public static string? AzureDevOpsSource(
        string? repositoryUrl,
        string? commitSha,
        string? path,
        int? line,
        int? endLine = null
    )
    {
        if (
            SafeHttps(repositoryUrl) is not { } safe
            || commitSha is not { Length: 40 or 64 }
            || !commitSha.All(Uri.IsHexDigit)
            || !RelativePath(path)
        )
            return null;
        var uri = new Uri(safe);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
            return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var modern =
            uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            && segments.Length == 4
            && segments[2] == "_git";
        var classic =
            uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            && segments.Length == 3
            && segments[1] == "_git";
        if (!modern && !classic)
            return null;
        var query = $"?path={Uri.EscapeDataString('/' + path!)}&version=GC{commitSha}&_a=contents";
        if (line is > 0 and < int.MaxValue)
        {
            var end = endLine is >= 1 and < int.MaxValue && endLine >= line ? endLine.Value : line.Value;
            query +=
                $"&line={line}&lineEnd={end + 1}&lineStartColumn=1&lineEndColumn=1&lineStyle=plain";
        }
        return safe.TrimEnd('/') + query;
    }

    /// <summary>Reads SARIF metadata defensively; optional documentation never determines ingestion success.</summary>
    public static FindingEvidence FromSarif(
        JsonElement driver,
        JsonElement rule,
        JsonElement result,
        JsonElement[] artifacts,
        string sourceRoot = ""
    )
    {
        var locations = new List<EvidenceLocation>();
        var truncated = false;
        void AddLocation(JsonElement location, string kind, int? flow, int? thread, int step)
        {
            // ponytail: retain at most 200 locations per result; raw SARIF remains the source for longer traces.
            if (locations.Count >= 200)
            {
                truncated = true;
                return;
            }
            var physical = Property(location, "physicalLocation");
            var artifact = Property(physical, "artifactLocation");
            if (
                Text(artifact, "uri") is null
                && Integer(artifact, "index") is { } index
                && index >= 0
                && index < artifacts.Length
            )
                artifact = Property(artifacts[index], "location");
            var path = Text(artifact, "uri");
            var absolute =
                path is not null
                && (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'));
            if (path is not null)
            {
                try
                {
                    path = SourcePaths.Relative(path, sourceRoot);
                }
                catch (InvalidDataException)
                {
                    path = null;
                }
            }
            var relative =
                RelativePath(path)
                && (Text(artifact, "uriBaseId") is null || absolute && sourceRoot.Length > 0);
            // Absolute paths may disclose build-agent directories or URI credentials; never expose them as evidence.
            if (!RelativePath(path))
                path = null;
            var region = Property(physical, "region");
            int? Positive(string name) =>
                Integer(region, name) is > 0 and var number ? number : null;
            locations.Add(
                new(
                    kind,
                    flow,
                    thread,
                    step,
                    path,
                    Positive("startLine"),
                    Positive("endLine"),
                    Positive("startColumn"),
                    Positive("endColumn"),
                    relative
                )
            );
        }
        foreach (var location in Items(result, "locations"))
            AddLocation(location, "primary", null, null, locations.Count);
        foreach (var location in Items(result, "relatedLocations"))
            AddLocation(location, "related", null, null, locations.Count);
        var flowIndex = 0;
        foreach (var flow in Items(result, "codeFlows"))
        {
            var threadIndex = 0;
            foreach (var thread in Items(flow, "threadFlows"))
            {
                var step = 0;
                foreach (var location in Items(thread, "locations"))
                    AddLocation(
                        Property(location, "location"),
                        "flow",
                        flowIndex,
                        threadIndex,
                        step++
                    );
                threadIndex++;
            }
            flowIndex++;
        }
        // A driver descriptor must not be attributed to an extension's rule with the same identifier.
        var fromExtension =
            Property(Property(result, "rule"), "toolComponent").ValueKind
            != JsonValueKind.Undefined;
        var documentation = fromExtension ? null : SafeHttps(Text(rule, "helpUri"));
        var driverVersion = Text(driver, "semanticVersion") ?? Text(driver, "version");
        if (driverVersion is { Length: > 200 })
            driverVersion = null;
        return new(
            driverVersion,
            documentation,
            "sarif.tool.driver.rules.helpUri",
            null,
            documentation is null ? "unavailable" : "producer_link_unverified",
            locations.ToArray(),
            truncated
        );
    }

    private static bool RelativePath(string? path) =>
        path is { Length: > 0 and <= 4096 }
        && !path.StartsWith('/')
        && !path.Contains(':')
        && !path.Contains('\\')
        && !path.Any(char.IsControl)
        && !path.Split('/').Any(segment => segment is ".." or "." or "");

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child)
            ? child
            : default;

    private static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } child
            ? child.GetString()
            : null;

    private static int? Integer(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } child
        && child.TryGetInt32(out var number)
            ? number
            : null;

    private static readonly JsonElement EmptyArray = JsonElement.Parse("[]");

    private static JsonElement.ArrayEnumerator Items(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Array } child
            ? child.EnumerateArray()
            : EmptyArray.EnumerateArray();
}
