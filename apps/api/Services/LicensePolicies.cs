using System.Text.Json;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

/// <summary>
/// Resolves the licence lists an ingestion is judged against and classifies SBOM components. Matching is
/// exact and case-sensitive on the strings the SBOM declares: compound SPDX expressions are not evaluated.
/// </summary>
public static class LicensePolicies
{
    public const int MaxEntries = 1000;
    public const int MaxLength = 200;

    /// <summary>What an ingestion was judged against: profile lists when the profile has any, else the organization policy.</summary>
    public sealed record Snapshot(string Source, int? Version, string[] Allowed, string[] Denied);

    public static async Task<Snapshot> CaptureAsync(AppDbContext db, ProfileDefinition profile, CancellationToken ct)
    {
        if (profile.AllowedLicenses.Length > 0 || profile.DeniedLicenses.Length > 0)
            return new Snapshot("profile", null, profile.AllowedLicenses, profile.DeniedLicenses);
        var policy = await db.Set<LicensePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == LicensePolicy.GlobalKey, ct);
        return policy is null
            ? new Snapshot("none", null, [], [])
            : new Snapshot("organization", policy.Version, Read(policy.AllowedLicenses), Read(policy.DeniedLicenses));
    }

    /// <summary>Ingestions created before the organization policy keep being judged by their profile only.</summary>
    public static Snapshot Effective(string? snapshot, ProfileDefinition profile) =>
        snapshot is null
            ? new Snapshot("profile", null, profile.AllowedLicenses, profile.DeniedLicenses)
            : JsonSerializer.Deserialize<Snapshot>(snapshot, ApiJson.Options)!;

    /// <summary>denied if any licence is denied; allowed if it declares licences and all are allowed; review otherwise.</summary>
    public static string Decide(IReadOnlyCollection<string> licenses, IReadOnlySet<string> allowed, IReadOnlySet<string> denied) =>
        licenses.Any(denied.Contains) ? "denied"
        : licenses.Count > 0 && licenses.All(allowed.Contains) ? "allowed"
        : "review";

    public static string[] Read(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    /// <summary>Trims, removes duplicates and rejects an entry that cannot be an exact licence string.</summary>
    public static string[] Normalize(IEnumerable<string>? values, string name)
    {
        var list = (values ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length > MaxEntries || list.Any(x => x.Length > MaxLength || x.Any(char.IsControl)))
            throw new ApiException(422, $"{name} accepts at most {MaxEntries} entries of {MaxLength} characters without control characters");
        return list;
    }
}
