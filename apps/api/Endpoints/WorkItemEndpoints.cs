using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public sealed record WorkItemSettingsRequest(
    int Version,
    string WorkItemType,
    string? AreaPath,
    string? IterationPath,
    string? AutoMinSeverity,
    bool AutoOnSlaBreach,
    int AutoMaxPerRun
);

public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var findings = app.MapGroup("/api/v1/collaboration/findings/{id}/work-item").RequireAuthorization();
        findings.MapGet("", async (string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var finding = await FindingAsync(id, context, auth, db, ct);
            var row = await db.Set<FindingWorkItem>().AsNoTracking().SingleOrDefaultAsync(x => x.FindingId == finding.Id, ct);
            // No link yet is a normal state: null, not 404.
            return row is null ? Results.Text("null", "application/json") : Results.Json(FindingWorkItems.View(row), ApiJson.Options);
        });
        findings.MapPost("", async (string id, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            var finding = await FindingAsync(id, context, auth, db, ct);
            var (target, reason) = await FindingWorkItems.ResolveAsync(db, finding.ProjectKey, ct);
            if (target is null) throw new ApiException(422, reason!);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await FindingCollaboration.LockAsync(db, finding.ProjectKey, finding.Branch, ct);
            var existing = await db.Set<FindingWorkItem>().AsNoTracking().SingleOrDefaultAsync(x => x.FindingId == finding.Id, ct);
            var row = await FindingWorkItems.RequestAsync(db, finding, target, existing?.Origin is { } origin && existing.State == "failed" ? origin : "manual", actor.Email, ct);
            if (existing?.State is not ("pending" or "created"))
                audit.Log(actor.Email, actor.Role, existing is null ? "work_item.request" : "work_item.retry", "finding", finding.Id, finding.ProjectKey);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Accepted($"/api/v1/collaboration/findings/{finding.Id}/work-item", FindingWorkItems.View(row));
        });

        var settings = app.MapGroup("/api/v1/work-items/projects/{projectKey}").RequireAuthorization();
        settings.MapGet("", async (string projectKey, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            await auth.AssertProjectAccessAsync(actor, projectKey, ct: ct);
            if (!await db.Set<Project>().AnyAsync(x => x.Key == projectKey, ct)) throw new ApiException(404, "Project not found");
            var row = await db.Set<WorkItemSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.ProjectKey == projectKey, ct);
            var (target, reason) = await FindingWorkItems.ResolveAsync(db, projectKey, ct);
            return Results.Ok(new
            {
                settings = Response(row ?? new WorkItemSettings { ProjectKey = projectKey, Version = 0 }),
                availability = new { enabled = target is not null, reason, organization_url = target?.OrganizationUrl, ado_project = target?.Project },
            });
        });
        settings.MapPut("", async (string projectKey, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            await auth.AssertProjectAccessAsync(actor, projectKey, ct: ct);
            if (!await db.Set<Project>().AnyAsync(x => x.Key == projectKey, ct)) throw new ApiException(404, "Project not found");
            var body = await ApiJson.ReadAsync<WorkItemSettingsRequest>(context, ct);
            Validate(body);
            var row = await db.Set<WorkItemSettings>().SingleOrDefaultAsync(x => x.ProjectKey == projectKey, ct);
            if (row is null)
            {
                if (body.Version != 0) throw new ApiException(409, "Work item settings changed; reload before saving");
                row = new WorkItemSettings { ProjectKey = projectKey, Version = 0 };
                db.Add(row);
            }
            else if (row.Version != body.Version) throw new ApiException(409, "Work item settings changed; reload before saving");
            var wasAutomatic = row.AutoSince is not null;
            var automatic = body.AutoMinSeverity is not null || body.AutoOnSlaBreach;
            row.WorkItemType = body.WorkItemType.Trim();
            row.AreaPath = Blank(body.AreaPath);
            row.IterationPath = Blank(body.IterationPath);
            row.AutoMinSeverity = body.AutoMinSeverity;
            row.AutoOnSlaBreach = body.AutoOnSlaBreach;
            row.AutoMaxPerRun = body.AutoMaxPerRun;
            // Enabling rules starts a new window: history detected before it never floods the board.
            row.AutoSince = automatic ? (wasAutomatic ? row.AutoSince : DateTime.UtcNow) : null;
            row.Version++;
            row.UpdatedBy = actor.Email;
            row.UpdatedAt = DateTime.UtcNow;
            audit.Log(actor.Email, actor.Role, "work_item.settings.update", "project", null, projectKey, Response(row));
            await db.SaveChangesAsync(ct);
            return Results.Ok(Response(row));
        });
        return app;
    }

    private static void Validate(WorkItemSettingsRequest body)
    {
        static bool Text(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);
        if (string.IsNullOrWhiteSpace(body.WorkItemType) || !Text(body.WorkItemType, 128))
            throw new ApiException(422, "work_item_type is required, 128 characters at most");
        if (!Text(body.AreaPath, 256) || !Text(body.IterationPath, 256))
            throw new ApiException(422, "area_path and iteration_path are 256 characters at most");
        if (body.AutoMinSeverity is not null && !FindingWorkItems.SeverityOrder.Contains(body.AutoMinSeverity))
            throw new ApiException(422, "auto_min_severity must be blocker, high, medium, low, info or null");
        if (body.AutoMaxPerRun is < 1 or > 100)
            throw new ApiException(422, "auto_max_per_run must be between 1 and 100");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<DurableFinding> FindingAsync(string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var actor = await auth.RequireAsync(context, "user", ct);
        var finding = await db.Set<DurableFinding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiException(404, "Finding not found");
        await auth.AssertProjectAccessAsync(actor, finding.ProjectKey, ct: ct);
        return finding;
    }

    private static object Response(WorkItemSettings row) => new
    {
        row.ProjectKey,
        row.WorkItemType,
        row.AreaPath,
        row.IterationPath,
        row.AutoMinSeverity,
        row.AutoOnSlaBreach,
        row.AutoMaxPerRun,
        row.AutoSince,
        row.Version,
        row.UpdatedBy,
        row.UpdatedAt,
    };
}
