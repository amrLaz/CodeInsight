using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public sealed record SlaPolicyRequest(int Version, int? BlockerDays, int? HighDays, int? MediumDays, int? LowDays, int? InfoDays);

public static class SlaEndpoints
{
    public static IEndpointRouteBuilder MapSlaEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/sla").RequireAuthorization();
        routes.MapGet("/policy", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            await auth.RequireAsync(context, "user", ct);
            var row = await db.Set<SlaPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == "global", ct);
            return Results.Ok(row is null ? null : Response(row));
        });
        routes.MapPut("/policy", async (HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            var body = await ApiJson.ReadAsync<SlaPolicyRequest>(context, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await SaveAsync(db, null, body, actor, ct);
            audit.Log(actor.Email, actor.Role, "sla.policy.update", "sla_policy", row.ScopeKey, "global", Response(row));
            await db.SaveChangesAsync(ct);
            await FindingSla.InvalidateAsync(db, null, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Response(row));
        });
        routes.MapGet("/projects/{projectKey}", async (string projectKey, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            await auth.AssertProjectAccessAsync(actor, projectKey, ct: ct);
            var project = await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x => x.Key == projectKey, ct)
                ?? throw new ApiException(404, "Project not found");
            var overrideRow = await db.Set<SlaPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == FindingSla.ScopeKey(projectKey), ct);
            var effective = overrideRow ?? await db.Set<SlaPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == "global", ct);
            var now = DateTime.UtcNow;
            var findings = db.Set<DurableFinding>().AsNoTracking().Where(x => x.ProjectKey == projectKey);
            var counts = new Dictionary<string, int>();
            foreach (var status in FindingSla.Statuses)
                counts[status] = await FindingSla.FilterStatus(findings, status, now).CountAsync(ct);
            return Results.Ok(new
            {
                project_key = projectKey,
                main_branch = project.MainBranch,
                @override = overrideRow is null ? null : Response(overrideRow),
                effective = effective is null ? null : Response(effective),
                counts,
            });
        });
        routes.MapPut("/projects/{projectKey}", async (string projectKey, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            await auth.AssertProjectAccessAsync(actor, projectKey, ct: ct);
            if (!await db.Set<Project>().AnyAsync(x => x.Key == projectKey, ct)) throw new ApiException(404, "Project not found");
            var body = await ApiJson.ReadAsync<SlaPolicyRequest>(context, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await SaveAsync(db, projectKey, body, actor, ct);
            audit.Log(actor.Email, actor.Role, "sla.policy.override", "project", null, projectKey, Response(row));
            await db.SaveChangesAsync(ct);
            await FindingSla.InvalidateAsync(db, projectKey, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Response(row));
        });
        routes.MapDelete("/projects/{projectKey}", async (string projectKey, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            await auth.AssertProjectAccessAsync(actor, projectKey, ct: ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Set<SlaPolicy>().SingleOrDefaultAsync(x => x.ScopeKey == FindingSla.ScopeKey(projectKey), ct);
            if (row is null) return Results.NoContent();
            db.Remove(row);
            audit.Log(actor.Email, actor.Role, "sla.policy.override.delete", "project", null, projectKey);
            await db.SaveChangesAsync(ct);
            await FindingSla.InvalidateAsync(db, projectKey, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
        return app;
    }

    /// <summary>Optimistic concurrency: version 0 creates an override, any other value must match the stored one.</summary>
    private static async Task<SlaPolicy> SaveAsync(AppDbContext db, string? projectKey, SlaPolicyRequest body, Principal actor, CancellationToken ct)
    {
        int?[] days = [body.BlockerDays, body.HighDays, body.MediumDays, body.LowDays, body.InfoDays];
        if (days.Any(x => x is < 1 or > FindingSla.MaxDays))
            throw new ApiException(422, $"Delays must be between 1 and {FindingSla.MaxDays} days, or null for no deadline");
        var key = FindingSla.ScopeKey(projectKey);
        var row = await db.Set<SlaPolicy>().SingleOrDefaultAsync(x => x.ScopeKey == key, ct);
        if (row is null)
        {
            if (body.Version != 0) throw new ApiException(409, "SLA policy changed; reload before saving");
            row = new SlaPolicy { ScopeKey = key, ProjectKey = projectKey, Version = 0 };
            db.Add(row);
        }
        else if (row.Version != body.Version) throw new ApiException(409, "SLA policy changed; reload before saving");
        row.BlockerDays = body.BlockerDays; row.HighDays = body.HighDays; row.MediumDays = body.MediumDays;
        row.LowDays = body.LowDays; row.InfoDays = body.InfoDays;
        row.Version++; row.UpdatedBy = actor.Email; row.UpdatedAt = DateTime.UtcNow;
        return row;
    }

    private static object Response(SlaPolicy row) => new
    {
        row.ScopeKey,
        row.ProjectKey,
        row.BlockerDays,
        row.HighDays,
        row.MediumDays,
        row.LowDays,
        row.InfoDays,
        row.Version,
        row.UpdatedBy,
        row.UpdatedAt,
    };
}
