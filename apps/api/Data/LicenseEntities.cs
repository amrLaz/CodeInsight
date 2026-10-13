using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

/// <summary>
/// Organization-wide licence policy: exact licence strings as SBOMs declare them. A profile that carries
/// its own lists takes precedence; the policy is frozen into each ingestion when it is created.
/// </summary>
public sealed class LicensePolicy
{
    public const string GlobalKey = "global";

    // Initial classification, to be confirmed by legal review: permissive and weak-copyleft licences are
    // allowed, strong copyleft and restrictive licences are denied. Deprecated SPDX identifiers are listed
    // because some producers still emit them.
    public static readonly string[] InitialAllowed =
    [
        "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "Zlib", "BSL-1.0", "Unlicense", "CC0-1.0",
        "PostgreSQL", "Python-2.0", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later",
        "LGPL-2.1", "LGPL-2.1+", "LGPL-3.0", "LGPL-3.0+", "MPL-2.0", "EPL-2.0",
    ];
    public static readonly string[] InitialDenied =
    [
        "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "AGPL-3.0-only", "AGPL-3.0-or-later",
        "GPL-2.0", "GPL-2.0+", "GPL-3.0", "GPL-3.0+", "AGPL-3.0", "SSPL-1.0", "BUSL-1.1", "CC-BY-NC-4.0",
    ];

    public string ScopeKey { get; set; } = GlobalKey;
    /// <summary>JSON array of exact licence strings.</summary>
    public string AllowedLicenses { get; set; } = "[]";
    public string DeniedLicenses { get; set; } = "[]";
    public int Version { get; set; } = 1;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class LicenseModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<LicensePolicy>().ToTable("license_policies").HasKey(x => x.ScopeKey);
        model.Entity<LicensePolicy>().HasData(new LicensePolicy
        {
            ScopeKey = LicensePolicy.GlobalKey,
            AllowedLicenses = JsonSerializer.Serialize(LicensePolicy.InitialAllowed),
            DeniedLicenses = JsonSerializer.Serialize(LicensePolicy.InitialDenied),
            UpdatedBy = "migration",
            UpdatedAt = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        foreach (var property in model.Entity<LicensePolicy>().Metadata.GetProperties())
            property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
