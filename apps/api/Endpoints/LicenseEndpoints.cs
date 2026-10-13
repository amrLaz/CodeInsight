using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public sealed record LicensePolicyRequest(int Version, string[] AllowedLicenses, string[] DeniedLicenses);

public static class LicenseEndpoints
{
    public const int InventoryLimit = 200;

    public static IEndpointRouteBuilder MapLicenseEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/licenses").RequireAuthorization();
        routes.MapGet("/policy", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            await auth.RequireAsync(context, "user", ct);
            var row = await db.Set<LicensePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == LicensePolicy.GlobalKey, ct);
            return Results.Ok(row is null ? null : Response(row));
        });
        routes.MapPut("/policy", async (HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            var body = await ApiJson.ReadAsync<LicensePolicyRequest>(context, ct);
            var allowed = LicensePolicies.Normalize(body.AllowedLicenses, "allowed_licenses");
            var denied = LicensePolicies.Normalize(body.DeniedLicenses, "denied_licenses");
            var overlap = allowed.Intersect(denied, StringComparer.Ordinal).ToArray();
            if (overlap.Length > 0) throw new ApiException(422, "A licence cannot be both allowed and denied: " + string.Join(", ", overlap.Take(10)));
            var row = await db.Set<LicensePolicy>().SingleOrDefaultAsync(x => x.ScopeKey == LicensePolicy.GlobalKey, ct);
            if (row is null)
            {
                if (body.Version != 0) throw new ApiException(409, "Licence policy changed; reload before saving");
                row = new LicensePolicy { Version = 0 };
                db.Add(row);
            }
            else if (row.Version != body.Version) throw new ApiException(409, "Licence policy changed; reload before saving");
            row.AllowedLicenses = JsonSerializer.Serialize(allowed);
            row.DeniedLicenses = JsonSerializer.Serialize(denied);
            row.Version++;
            row.UpdatedBy = actor.Email;
            row.UpdatedAt = DateTime.UtcNow;
            audit.Log(actor.Email, actor.Role, "license.policy.update", "license_policy", row.ScopeKey, "global", new { row.Version, allowed = allowed.Length, denied = denied.Length });
            await db.SaveChangesAsync(ct);
            return Results.Ok(Response(row));
        });
        routes.MapGet("/unclassified", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            await auth.RequireAsync(context, "admin", ct);
            var policy = await db.Set<LicensePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == LicensePolicy.GlobalKey, ct);
            return Results.Ok(await InventoryAsync(db, policy, ct));
        });
        return app;
    }

    /// <summary>
    /// Licence strings of the latest completed ingestion of each project that the organization policy does not
    /// classify, most frequent first. Components declaring no licence are counted apart: no list can classify them.
    /// </summary>
    internal static async Task<object> InventoryAsync(AppDbContext db, LicensePolicy? policy, CancellationToken ct)
    {
        var classified = (policy is null ? [] : LicensePolicies.Read(policy.AllowedLicenses).Concat(LicensePolicies.Read(policy.DeniedLicenses)))
            .ToHashSet(StringComparer.Ordinal);
        var completed = db.Set<IngestionRecord>().AsNoTracking().Where(x => x.State == "completed" && !x.DetailsPurged);
        var latest = completed.GroupBy(x => x.ProjectKey).Select(g => new { ProjectKey = g.Key, CreatedAt = g.Max(x => x.CreatedAt) });
        var ingestions = await (from row in completed join last in latest on new { row.ProjectKey, row.CreatedAt } equals new { last.ProjectKey, last.CreatedAt } select new { row.Id, row.ProjectKey })
            .ToListAsync(ct);
        var projectOf = ingestions.ToDictionary(x => x.Id, x => x.ProjectKey);
        var ids = projectOf.Keys.ToArray();
        var components = await db.Set<SbomComponent>().AsNoTracking().Where(x => ids.Contains(x.IngestionId))
            .Select(x => new { x.IngestionId, x.Licenses }).ToListAsync(ct);
        var unclassified = new Dictionary<string, Usage>(StringComparer.Ordinal);
        var unlicensed = 0;
        var unlicensedProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            var project = projectOf[component.IngestionId];
            var licenses = LicensePolicies.Read(component.Licenses);
            if (licenses.Length == 0)
            {
                unlicensed++;
                unlicensedProjects.Add(project);
                continue;
            }
            foreach (var license in licenses.Where(x => !classified.Contains(x)).Distinct(StringComparer.Ordinal))
            {
                if (!unclassified.TryGetValue(license, out var usage)) unclassified[license] = usage = new Usage();
                usage.Components++;
                usage.Projects.Add(project);
            }
        }
        return new
        {
            projects = projectOf.Values.Distinct().Count(),
            components = components.Count,
            unlicensed_components = unlicensed,
            unlicensed_projects = unlicensedProjects.Count,
            licenses = unclassified.OrderByDescending(x => x.Value.Components).ThenBy(x => x.Key, StringComparer.Ordinal).Take(InventoryLimit)
                .Select(x => new { license = x.Key, components = x.Value.Components, projects = x.Value.Projects.Count }),
            truncated = unclassified.Count > InventoryLimit,
        };
    }

    private sealed class Usage
    {
        public int Components { get; set; }
        public HashSet<string> Projects { get; } = new(StringComparer.Ordinal);
    }

    private static object Response(LicensePolicy row) => new
    {
        allowed_licenses = LicensePolicies.Read(row.AllowedLicenses),
        denied_licenses = LicensePolicies.Read(row.DeniedLicenses),
        row.Version,
        row.UpdatedBy,
        row.UpdatedAt,
    };
}
