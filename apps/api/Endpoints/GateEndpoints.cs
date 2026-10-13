using System.ComponentModel.DataAnnotations;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class GateEndpoints
{
    public static void MapGateEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/gates").RequireAuthorization();
        routes.MapGet(
            "",
            async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                var p = await auth.RequireAsync(c, ct: ct);
                auth.AssertCiPermission(p);
                var gates = await db.Set<QualityGateConfig>()
                    .OrderByDescending(g => g.IsDefault)
                    .ThenBy(g => g.Name)
                    .ToListAsync(ct);
                var keys = await auth.AllowedProjectKeysAsync(p, ct: ct);
                var assignments = await db.Set<ProjectGateAssignment>()
                    .Where(a => keys.Contains(a.ProjectKey))
                    .ToDictionaryAsync(a => a.ProjectKey, a => a.GateId, ct);
                var defaultId =
                    (gates.FirstOrDefault(g => g.IsDefault) ?? gates.FirstOrDefault())?.Id
                    ?? "default";
                return gates.Select(g =>
                    Response(
                        g,
                        assignments.Values.Count(id => id == g.Id)
                            + (g.Id == defaultId ? keys.Count(k => !assignments.ContainsKey(k)) : 0)
                    )
                );
            }
        );
        routes.MapGet(
            "/default",
            async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                auth.AssertCiPermission(await auth.RequireAsync(c, ct: ct));
                return Response(await DefaultAsync(db, ct));
            }
        );
        routes.MapGet(
            "/assignments",
            async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                var visible = await auth.AllowedProjectKeysAsync(
                    await auth.RequireAsync(c, ct: ct),
                    ct: ct
                );
                return await db.Set<ProjectGateAssignment>()
                    .Where(a => visible.Contains(a.ProjectKey))
                    .ToDictionaryAsync(a => a.ProjectKey, a => a.GateId, ct);
            }
        );
        routes.MapGet(
            "/{gate_id}",
            async (
                string gate_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                auth.AssertCiPermission(await auth.RequireAsync(c, ct: ct));
                return Response(await FindAsync(db, gate_id, ct));
            }
        );
        routes
            .MapPost(
                "",
                async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<GateRequest>(c, ct);
                    var gate = new QualityGateConfig { Id = Guid.NewGuid().ToString() };
                    Apply(gate, body);
                    db.Add(gate);
                    await db.SaveChangesAsync(ct);
                    return Response(gate);
                }
            )
            .Accepts<GateRequest>("application/json");
        routes
            .MapPut(
                "/{gate_id}",
                async (
                    string gate_id,
                    HttpContext c,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<GateRequest>(c, ct);
                    var gate = await FindAsync(db, gate_id, ct);
                    Apply(gate, body);
                    await db.SaveChangesAsync(ct);
                    return Response(gate);
                }
            )
            .Accepts<GateRequest>("application/json");
        routes.MapDelete(
            "/{gate_id}",
            async (
                string gate_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(c, "admin", ct);
                var gate = await FindAsync(db, gate_id, ct);
                if (gate.IsDefault)
                    throw new ApiException(400, "Cannot delete the default gate");
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Set<ProjectGateAssignment>()
                    .Where(a => a.GateId == gate_id)
                    .ExecuteDeleteAsync(ct);
                db.Remove(gate);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new { status = "deleted" };
            }
        );
        routes
            .MapPut(
                "/assignments/{project_key}",
                async (
                    string project_key,
                    HttpContext c,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<AssignRequest>(c, ct);
                    var gate = await FindAsync(db, body.GateId, ct);
                    var assignment = await db.Set<ProjectGateAssignment>()
                        .FindAsync([project_key], ct);
                    if (assignment is null)
                    {
                        assignment = new ProjectGateAssignment { ProjectKey = project_key };
                        db.Add(assignment);
                    }
                    assignment.GateId = gate.Id;
                    assignment.AssignedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return new
                    {
                        project_key,
                        gate_id = gate.Id,
                        gate_name = gate.Name,
                    };
                }
            )
            .Accepts<AssignRequest>("application/json");
        routes.MapDelete(
            "/assignments/{project_key}",
            async (
                string project_key,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(c, "admin", ct);
                await db.Set<ProjectGateAssignment>()
                    .Where(a => a.ProjectKey == project_key)
                    .ExecuteDeleteAsync(ct);
                var gate = await DefaultAsync(db, ct);
                return new
                {
                    project_key,
                    gate_id = gate.Id,
                    gate_name = gate.Name,
                };
            }
        );
    }

    public static async Task<QualityGateConfig> DefaultAsync(AppDbContext db, CancellationToken ct)
    {
        var gate =
            await db.Set<QualityGateConfig>().FirstOrDefaultAsync(g => g.IsDefault, ct)
            ?? await db.Set<QualityGateConfig>().FindAsync(["default"], ct);
        // ponytail: callers own persistence; a read never changes the database.
        return gate
            ?? new QualityGateConfig
            {
                Id = "default",
                Name = "Default Gate",
                IsDefault = true,
            };
    }

    private static async Task<QualityGateConfig> FindAsync(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<QualityGateConfig>().FindAsync([id], ct)
        ?? throw new ApiException(404, "Gate not found");

    private static object Response(QualityGateConfig g, int count = 0) =>
        new
        {
            gate_id = g.Id,
            g.Name,
            g.IsDefault,
            g.MinMaintainabilityScore,
            g.MinSecurityScore,
            g.MinReliabilityScore,
            g.MaxVulnerabilities,
            g.MaxSecurityHotspots,
            g.MaxCodeSmells,
            g.MaxBugs,
            project_count = count,
        };

    private static void Apply(QualityGateConfig g, GateRequest r)
    {
        g.Name = r.Name.Trim();
        g.MinMaintainabilityScore = r.MinMaintainabilityScore;
        g.MinSecurityScore = r.MinSecurityScore;
        g.MinReliabilityScore = r.MinReliabilityScore;
        g.MaxVulnerabilities = r.MaxVulnerabilities;
        g.MaxSecurityHotspots = r.MaxSecurityHotspots;
        g.MaxCodeSmells = r.MaxCodeSmells;
        g.MaxBugs = r.MaxBugs;
    }
}

public sealed class GateRequest
{
    [Required(AllowEmptyStrings = true), StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Range(0, 100)]
    public int MinMaintainabilityScore { get; set; } = 50;

    [Range(0, 100)]
    public int MinSecurityScore { get; set; } = 50;

    [Range(0, 100)]
    public int MinReliabilityScore { get; set; } = 50;

    [Range(0, 10000)]
    public int MaxVulnerabilities { get; set; } = 10;

    [Range(0, 10000)]
    public int MaxSecurityHotspots { get; set; } = 10;

    [Range(0, 100000)]
    public int MaxCodeSmells { get; set; } = 100;

    [Range(0, 10000)]
    public int MaxBugs { get; set; } = 10;
}

public sealed class AssignRequest
{
    [Required(AllowEmptyStrings = true)]
    public string GateId { get; set; } = null!;
}
