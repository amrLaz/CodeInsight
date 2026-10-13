using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class LicenseTests
{
    private static readonly HashSet<string> Allowed = new(["MIT", "Apache-2.0"], StringComparer.Ordinal);
    private static readonly HashSet<string> Denied = new(["GPL-3.0-only"], StringComparer.Ordinal);

    [Theory]
    [InlineData(new[] { "MIT" }, "allowed")]
    [InlineData(new[] { "MIT", "Apache-2.0" }, "allowed")]
    [InlineData(new[] { "MIT", "GPL-3.0-only" }, "denied")]
    [InlineData(new[] { "mit" }, "review")]
    [InlineData(new[] { "MIT OR Apache-2.0" }, "review")]
    [InlineData(new[] { "Apache License 2.0" }, "review")]
    [InlineData(new string[0], "review")]
    public void Matching_is_exact_and_a_component_without_licence_needs_review(string[] licenses, string expected) =>
        Assert.Equal(expected, LicensePolicies.Decide(licenses, Allowed, Denied));

    [Fact]
    public void Initial_classification_is_disjoint_and_keeps_weak_copyleft_allowed()
    {
        Assert.Empty(LicensePolicy.InitialAllowed.Intersect(LicensePolicy.InitialDenied));
        Assert.Contains("LGPL-2.1-only", LicensePolicy.InitialAllowed);
        Assert.Contains("MPL-2.0", LicensePolicy.InitialAllowed);
        Assert.Contains("AGPL-3.0-only", LicensePolicy.InitialDenied);
        Assert.Contains("GPL-3.0", LicensePolicy.InitialDenied);
    }

    [Fact]
    public void Entries_are_trimmed_deduplicated_and_bounded()
    {
        Assert.Equal(["MIT", "ISC"], LicensePolicies.Normalize([" MIT ", "MIT", "", "ISC"], "allowed_licenses"));
        Assert.Throws<ApiException>(() => LicensePolicies.Normalize(["MIT\nISC"], "allowed_licenses"));
        Assert.Throws<ApiException>(() => LicensePolicies.Normalize([new string('x', 201)], "allowed_licenses"));
    }

    [Fact]
    public void An_ingestion_created_before_the_policy_keeps_its_profile_lists()
    {
        var profile = new ProfileDefinition { AllowedLicenses = ["MIT"] };
        var legacy = LicensePolicies.Effective(null, profile);
        Assert.Equal("profile", legacy.Source);
        Assert.Equal(["MIT"], legacy.Allowed);
        var frozen = JsonSerializer.Serialize(new LicensePolicies.Snapshot("organization", 3, ["ISC"], ["SSPL-1.0"]), ApiJson.Options);
        var effective = LicensePolicies.Effective(frozen, profile);
        Assert.Equal(("organization", 3, "ISC", "SSPL-1.0"), (effective.Source, effective.Version, effective.Allowed.Single(), effective.Denied.Single()));
    }

    [Fact]
    public async Task Capture_precedence_and_unclassified_inventory()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_DB to run licence PostgreSQL tests.");
        var isolated = new NpgsqlConnectionStringBuilder(connection) { Database = "codeinsights_licenses_" + Guid.NewGuid().ToString("N")[..12], Pooling = false };
        await ExecuteAsync(connection, $"CREATE DATABASE \"{isolated.Database}\"", ct);
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options);
            await db.Database.MigrateAsync(ct);

            // The seeded organization policy applies unless the profile carries its own lists.
            var organization = await LicensePolicies.CaptureAsync(db, new ProfileDefinition(), ct);
            Assert.Equal(("organization", 1), (organization.Source, organization.Version));
            Assert.Contains("MIT", organization.Allowed);
            var own = await LicensePolicies.CaptureAsync(db, new ProfileDefinition { DeniedLicenses = ["MIT"] }, ct);
            Assert.Equal(("profile", "MIT"), (own.Source, own.Denied.Single()));

            // Inventory reads the latest completed, non-purged ingestion of each project only.
            var now = DateTime.UtcNow;
            Ingestion(db, "i-old", "alpha", now.AddDays(-2), ["LicenseRef-old"]);
            Ingestion(db, "i-alpha", "alpha", now.AddDays(-1), ["MIT"], ["MIT OR Apache-2.0"], ["Apache License 2.0"], []);
            Ingestion(db, "i-beta", "beta", now.AddDays(-1), ["MIT OR Apache-2.0"], []);
            Ingestion(db, "i-purged", "gamma", now, ["LicenseRef-purged"], purged: true);
            Ingestion(db, "i-running", "beta", now, ["LicenseRef-running"], state: "processing");
            await db.SaveChangesAsync(ct);
            var policy = await db.Set<LicensePolicy>().AsNoTracking().SingleAsync(ct);
            var inventory = JsonSerializer.SerializeToElement(await LicenseEndpoints.InventoryAsync(db, policy, ct), ApiJson.Options);
            Assert.Equal((2, 6, 2, 2), (inventory.GetProperty("projects").GetInt32(), inventory.GetProperty("components").GetInt32(),
                inventory.GetProperty("unlicensed_components").GetInt32(), inventory.GetProperty("unlicensed_projects").GetInt32()));
            var licenses = inventory.GetProperty("licenses").EnumerateArray()
                .Select(x => (x.GetProperty("license").GetString(), x.GetProperty("components").GetInt32(), x.GetProperty("projects").GetInt32())).ToList();
            Assert.Equal([("MIT OR Apache-2.0", 2, 2), ("Apache License 2.0", 1, 1)], licenses);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{isolated.Database}\" WITH (FORCE)", ct);
        }
    }

    private static void Ingestion(AppDbContext db, string id, string project, DateTime at, params string[][] components) =>
        Ingestion(db, id, project, at, false, "completed", components);

    private static void Ingestion(AppDbContext db, string id, string project, DateTime at, string[] licenses, bool purged = false, string state = "completed") =>
        Ingestion(db, id, project, at, purged, state, [licenses]);

    private static void Ingestion(AppDbContext db, string id, string project, DateTime at, bool purged, string state, string[][] components)
    {
        db.Add(new IngestionRecord { Id = id, ProjectKey = project, AnalysisId = id + "-a", BuildKey = id, ManifestHash = id, State = state, DetailsPurged = purged, CreatedAt = at });
        foreach (var (licenses, index) in components.Select((x, i) => (x, i)))
            db.Add(new SbomComponent { IngestionId = id, AnalysisId = id + "-a", ReportId = "sbom", Reference = $"{id}-{index}", Name = "lib" + index, Licenses = JsonSerializer.Serialize(licenses) });
    }

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }
}
