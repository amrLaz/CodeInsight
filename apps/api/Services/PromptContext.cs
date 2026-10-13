namespace CodeInsights.Api.Services;

public static class PromptContext
{
    public static string Extract(string text, int line, int radius)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        line = Math.Clamp(line, 1, Math.Max(1, lines.Length));
        var start = Math.Max(1, line - Math.Max(0, radius));
        var end = Math.Min(lines.Length, line + Math.Max(0, radius));
        return string.Join('\n', lines[(start - 1)..end].Select((value, index) => $"{start + index,5}: {value}"));
    }
}
