namespace CodeInsights.Api.Data;

/// <summary>
/// Builds PostgreSQL <c>ILIKE</c> patterns from user input for <c>EF.Functions.ILike</c>:
/// <c>StringComparison</c> overloads are not translated to SQL, and an unescaped
/// <c>%</c> or <c>_</c> would act as a wildcard.
/// </summary>
internal static class SqlLike
{
    public const string EscapeCharacter = "\\";

    /// <summary>Matches the value exactly, ignoring case.</summary>
    public static string Exact(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Matches any text containing the value, ignoring case.</summary>
    public static string Containing(string value) => "%" + Exact(value) + "%";
}
