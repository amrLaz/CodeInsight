namespace CodeInsights.Api.Services;

/// <summary>Maps producer paths lexically under an explicit root without accessing the filesystem.</summary>
public static class SourcePaths
{
    /// <summary>Validates an optional absolute Windows/POSIX root or local file URI.</summary>
    public static void ValidateRoot(string? sourceRoot)
    {
        if (sourceRoot is null) throw new InvalidDataException("source_root must be a string.");
        if (sourceRoot.Length > 0) Absolute(sourceRoot);
    }

    /// <summary>Preserves relative paths and maps absolute paths only when strictly inside the supplied root.</summary>
    public static string Relative(string value, string sourceRoot = "")
    {
        var decoded = Decode(value);
        if (decoded.StartsWith('/') || decoded.Contains(':'))
        {
            if (sourceRoot.Length == 0) throw new InvalidDataException("Absolute report paths require source_root.");
            var path = Absolute(value);
            var root = Absolute(sourceRoot);
            var prefix = root == "/" ? "/" : root.TrimEnd('/') + "/";
            var comparison = root.Length > 1 && root[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(prefix, comparison) || path.Length <= prefix.Length)
                throw new InvalidDataException("Report path is outside source_root.");
            return path[prefix.Length..];
        }
        var parts = decoded.Split('/').Where(x => x.Length > 0 && x != ".").ToArray();
        if (parts.Length == 0 || parts.Contains("..")) throw new InvalidDataException("Path traversal is not allowed.");
        return string.Join('/', parts);
    }

    private static string Decode(string value)
    {
        if (value.Length is 0 or > 4096) throw new InvalidDataException("Invalid report path length.");
        var path = Uri.UnescapeDataString(value).Replace('\\', '/');
        if (path.Any(char.IsControl) || path.IndexOfAny(['?', '#', '%']) >= 0)
            throw new InvalidDataException("Report paths cannot contain queries, fragments or nested encoding.");
        return path;
    }

    private static string Absolute(string value)
    {
        var path = Decode(value);
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only local file URIs are allowed.");
            path = path[7..];
            if (path.Length > 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' && path[3] == '/') path = path[1..];
        }
        var windows = path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        if (path.StartsWith("//", StringComparison.Ordinal) || !(windows || path.StartsWith('/'))
            || (windows ? path[2..] : path).Contains(':')) throw new InvalidDataException("Expected an absolute local source root or path.");
        var segments = (windows ? path[3..] : path[1..]).TrimEnd('/').Split('/');
        if (segments.Any(x => x is "." or "..") || segments.Length > 1 && segments.Any(x => x.Length == 0))
            throw new InvalidDataException("Absolute source paths cannot contain traversal or empty segments.");
        return path == "/" ? path : path.TrimEnd('/');
    }
}
