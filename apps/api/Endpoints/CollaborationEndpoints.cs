using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public sealed record TeamRequest([property: Required, MaxLength(200)] string ProjectKey, [property: Required, MaxLength(100)] string Name, string? OwnerUserId = null);
public sealed record OwnershipRequest(string? TeamId = null, string? OwnerUserId = null);
public sealed record FindingUpdate(int Version, string State, string HotspotReview, string? AssigneeUserId = null, string? TeamId = null, string Reason = "", DateTime? ExpiresAt = null);
public sealed record FindingComment(int Version, [property: Required, MaxLength(4000)] string Reason);

public static class CollaborationEndpoints
{
    public static IEndpointRouteBuilder MapCollaborationEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/collaboration").RequireAuthorization();
        routes.MapGet("/teams", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            var keys = await auth.AllowedProjectKeysAsync(actor, ct: ct);
            var key = context.Request.Query["project_key"].ToString();
            if (key.Length > 0) await auth.AssertProjectAccessAsync(actor, key, ct: ct);
            var query = db.Set<CollaborationTeam>().AsNoTracking().Where(x => keys.Contains(x.ProjectKey) && (key == "" || x.ProjectKey == key));
            var cursor = InsightEndpoints.DecodeCursor(context.Request, 1)?[0];
            var size = InsightEndpoints.PageSize(context.Request);
            var rows = await query.Where(x => cursor == null || string.Compare(x.Id, cursor) > 0).OrderBy(x => x.Id).Take(size + 1)
                .Select(x => new { x.Id, x.ProjectKey, x.Name, x.OwnerUserId }).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(rows[^1].Id)); }
            return Results.Ok(rows);
        });
        routes.MapPost("/teams", async (HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct);
            var body = await ApiJson.ReadAsync<TeamRequest>(context, ct);
            await auth.AssertProjectAccessAsync(actor, body.ProjectKey, ct: ct);
            await ValidateAssignmentAsync(db, auth, body.ProjectKey, body.OwnerUserId, null, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await FindingCollaboration.LockAsync(db, body.ProjectKey, "teams", ct);
            if (await db.Set<CollaborationTeam>().AnyAsync(x => x.ProjectKey == body.ProjectKey && x.Name == body.Name.Trim(), ct)) throw new ApiException(409, "Team already exists");
            var row = new CollaborationTeam { ProjectKey = body.ProjectKey, Name = body.Name.Trim(), OwnerUserId = body.OwnerUserId };
            db.Add(row); audit.Log(actor.Email, actor.Role, "team.create", "project", resourceLabel: body.ProjectKey, details: body);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created("/api/v1/collaboration/teams", new { row.Id, row.ProjectKey, row.Name, row.OwnerUserId });
        });
        routes.MapGet("/people", async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            var key = context.Request.Query["project_key"].ToString();
            if (key.Length == 0) throw new ApiException(422, "project_key is required");
            await auth.AssertProjectAccessAsync(actor, key, ct: ct);
            var organization = await db.Set<Project>().Where(x => x.Key == key).Select(x => x.OrganizationId).SingleAsync(ct);
            var query = db.Set<PlatformUser>().AsNoTracking().Where(x => x.IsActive && (x.Role == "admin" || db.Set<UserGrant>().Any(g => g.UserId == x.UserId &&
                (g.ScopeType == AccessGrantScopes.AllProjects && g.ScopeId == AccessGrantScopes.AllProjectsId || g.ScopeType == "project" && g.ScopeId == key || organization != null && g.ScopeType == "organization" && g.ScopeId == organization))));
            var cursor = InsightEndpoints.DecodeCursor(context.Request, 1)?[0]; var size = InsightEndpoints.PageSize(context.Request);
            var rows = await query.Where(x => cursor == null || string.Compare(x.UserId, cursor) > 0).OrderBy(x => x.UserId).Take(size + 1)
                .Select(x => new { x.UserId, x.FullName, x.Email }).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(rows[^1].UserId)); }
            return Results.Ok(rows);
        });
        routes.MapGet("/ownership/{key}", async (string key, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct); await auth.AssertProjectAccessAsync(actor, key, ct: ct);
            var row = await db.Set<ProjectOwnership>().FindAsync([key], ct);
            return Results.Ok(new OwnershipRequest(row?.TeamId, row?.OwnerUserId));
        });
        routes.MapPut("/ownership/{key}", async (string key, HttpContext context, AuthService auth, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "admin", ct); await auth.AssertProjectAccessAsync(actor, key, ct: ct);
            var body = await ApiJson.ReadAsync<OwnershipRequest>(context, ct);
            await ValidateAssignmentAsync(db, auth, key, body.OwnerUserId, body.TeamId, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct); await FindingCollaboration.LockAsync(db, key, "ownership", ct);
            var row = await db.Set<ProjectOwnership>().FindAsync([key], ct);
            if (row is null) { row = new ProjectOwnership { ProjectKey = key }; db.Add(row); }
            row.OwnerUserId = body.OwnerUserId; row.TeamId = body.TeamId;
            audit.Log(actor.Email, actor.Role, "ownership.update", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(body);
        });
        routes.MapGet("/findings", ListAsync);
        routes.MapGet("/findings/{id}", async (string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
            Results.Ok(FindingCollaboration.View(await FindAsync(id, context, auth, db, ct))));
        routes.MapPost("/occurrences/{id:long}", async (long id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            var occurrence = await db.Set<FindingOccurrence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "Occurrence not found");
            var analysis = await db.Set<AnalysisRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.AnalysisId == occurrence.AnalysisId, ct) ?? throw new ApiException(410, "Analysis details purged");
            await auth.AssertProjectAccessAsync(actor, analysis.ProjectKey, ct: ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await FindingCollaboration.LockAsync(db, analysis.ProjectKey, analysis.Branch, ct);
            // An old analysis is adopted once; subsequent reads must never repeat an automatic reopening.
            var items = await db.Set<FindingOccurrence>().Where(x => x.AnalysisId == analysis.AnalysisId && x.IngestionId == occurrence.IngestionId).ToListAsync(ct);
            if (items.Count == 0) throw new ApiException(410, "Analysis details purged");
            // On a pull request build, opening an occurrence creates its source-branch finding only for this occurrence.
            if (items.Any(x => x.FindingId == null)) await FindingCollaboration.ObserveAsync(db, analysis, items, ct, adoptOccurrenceId: id);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            var findingId = items.Single(x => x.Id == id).FindingId!;
            return Results.Ok(FindingCollaboration.View(await db.Set<DurableFinding>().SingleAsync(x => x.Id == findingId, ct)));
        });
        routes.MapGet("/findings/{id}/decisions", async (string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            await FindAsync(id, context, auth, db, ct);
            var cursor = InsightEndpoints.DecodeCursor(context.Request, 2); var size = InsightEndpoints.PageSize(context.Request);
            var query = db.Set<FindingDecision>().AsNoTracking().Where(x => x.FindingId == id);
            if (cursor is not null)
            {
                if (!DateTimeOffset.TryParse(cursor[0], out var parsed)) throw new ApiException(422, "Invalid cursor");
                var at = parsed.UtcDateTime; var lastId = cursor[1];
                query = query.Where(x => x.CreatedAt < at || x.CreatedAt == at && string.Compare(x.Id, lastId) < 0);
            }
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1)
                .Select(x => new { x.Id, x.ActorEmail, x.Action, x.Reason, x.Snapshot, x.CreatedAt }).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(ApiJson.Iso(rows[^1].CreatedAt), rows[^1].Id)); }
            return Results.Ok(rows.Select(x => new { x.Id, x.ActorEmail, x.Action, x.Reason, snapshot = JsonSerializer.Deserialize<JsonElement>(x.Snapshot), x.CreatedAt }));
        });
        routes.MapPut("/findings/{id}", UpdateAsync);
        routes.MapPost("/findings/{id}/comments", async (string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var actor = await auth.RequireAsync(context, "user", ct);
            var body = await ApiJson.ReadAsync<FindingComment>(context, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var finding = await LockedAsync(id, actor, auth, db, ct);
            if (finding.Version != body.Version) throw new ApiException(409, "Finding changed; reload before commenting");
            finding.Version++;
            await FindingCollaboration.DecisionAsync(db, finding, actor, "comment", body.Reason.Trim(), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(FindingCollaboration.View(finding));
        });
        return app;
    }

    public static async Task ValidateAssignmentAsync(AppDbContext db, AuthService auth, string project, string? userId, string? teamId, CancellationToken ct)
    {
        if (teamId is not null && !await db.Set<CollaborationTeam>().AnyAsync(x => x.Id == teamId && x.ProjectKey == project, ct)) throw new ApiException(422, "Team is outside the project");
        if (userId is null) return;
        var user = await db.Set<PlatformUser>().SingleOrDefaultAsync(x => x.UserId == userId && x.IsActive, ct);
        if (user is null || !(await auth.AllowedProjectKeysAsync(new Principal("user", user.UserId, user.Email, user.FullName, user.Role), ct: ct)).Contains(project))
            throw new ApiException(422, "Assignee must be an active project member");
    }

    private static async Task<DurableFinding> FindAsync(string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var actor = await auth.RequireAsync(context, "user", ct);
        var finding = await db.Set<DurableFinding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "Finding not found");
        await auth.AssertProjectAccessAsync(actor, finding.ProjectKey, ct: ct); return finding;
    }

    private static async Task<DurableFinding> LockedAsync(string id, Principal actor, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var finding = await db.Set<DurableFinding>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException(404, "Finding not found");
        await auth.AssertProjectAccessAsync(actor, finding.ProjectKey, ct: ct);
        await FindingCollaboration.LockAsync(db, finding.ProjectKey, finding.Branch, ct);
        return await db.Set<DurableFinding>().SingleAsync(x => x.Id == id, ct);
    }

    private static async Task<IResult> ListAsync(HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var actor = await auth.RequireAsync(context, "user", ct); var keys = await auth.AllowedProjectKeysAsync(actor, ct: ct);
        var key = context.Request.Query["project_key"].ToString();
        if (key.Length > 0) await auth.AssertProjectAccessAsync(actor, key, ct: ct);
        var branch = context.Request.Query["branch"].ToString(); var team = context.Request.Query["team_id"].ToString();
        var branchRefs = BranchRefs.Candidates(branch);
        var query = db.Set<DurableFinding>().AsNoTracking().Where(x => keys.Contains(x.ProjectKey) && (key == "" || x.ProjectKey == key) && (branch == "" || branchRefs.Contains(x.Branch)) && (team == "" || x.TeamId == team));
        if (context.Request.Query["mine"] == "true") query = query.Where(x => x.AssigneeUserId == actor.Sub);
        query = FilterState(query, context.Request.Query["state"].ToString());
        query = FindingSla.FilterStatus(query, context.Request.Query["sla_status"].ToString(), DateTime.UtcNow);
        var cursor = InsightEndpoints.DecodeCursor(context.Request, 1)?[0]; var size = InsightEndpoints.PageSize(context.Request);
        var rows = await query.Where(x => cursor == null || string.Compare(x.Id, cursor) > 0).OrderBy(x => x.Id).Take(size + 1).ToListAsync(ct);
        if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, InsightEndpoints.EncodeCursor(rows[^1].Id)); }
        return Results.Ok(rows.Select(FindingCollaboration.View));
    }

    public static IQueryable<DurableFinding> FilterState(IQueryable<DurableFinding> query, string state)
    {
        if (state.Length == 0 || state == "all") return query;
        if (state is not ("open" or "confirmed" or "resolved" or "false_positive" or "accepted_risk")) throw new ApiException(422, "Invalid finding state");
        var now = DateTime.UtcNow;
        return state == "open" ? query.Where(x => x.State == "open" || (x.State == "false_positive" || x.State == "accepted_risk") && x.ExceptionExpiresAt <= now)
            : query.Where(x => x.State == state && (state != "false_positive" && state != "accepted_risk" || x.ExceptionExpiresAt > now));
    }

    private static async Task<IResult> UpdateAsync(string id, HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        var actor = await auth.RequireAsync(context, "user", ct); var body = await ApiJson.ReadAsync<FindingUpdate>(context, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockedAsync(id, actor, auth, db, ct);
        if (body.Version != row.Version) throw new ApiException(409, "Finding changed; reload before updating");
        if (body.State is not ("open" or "confirmed" or "resolved" or "false_positive" or "accepted_risk") || body.HotspotReview is not ("not_reviewed" or "safe" or "to_fix")
            || body.Reason is null || body.Reason.Length > 4000 || body.HotspotReview != "not_reviewed" && row.Category != "security_hotspot") throw new ApiException(422, "Invalid state, review or reason");
        var exceptionChange = (row.State is "false_positive" or "accepted_risk" || body.State is "false_positive" or "accepted_risk") &&
            (body.State != row.State || body.ExpiresAt != row.ExceptionExpiresAt || body.Reason != row.ExceptionReason);
        if (exceptionChange && !actor.IsAdmin) throw new ApiException(403, "Exceptions require administrator review");
        if ((body.State != row.State || body.HotspotReview != row.HotspotReview || exceptionChange) && string.IsNullOrWhiteSpace(body.Reason)) throw new ApiException(422, "A review reason is required");
        if (body.State is "false_positive" or "accepted_risk" && (body.ExpiresAt is null || body.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow || body.ExpiresAt.Value.ToUniversalTime() > DateTime.UtcNow.AddYears(1)))
            throw new ApiException(422, "Exception requires an expiry within one year");
        await ValidateAssignmentAsync(db, auth, row.ProjectKey, body.AssigneeUserId, body.TeamId, ct);
        var expiry = body.State is "false_positive" or "accepted_risk" ? body.ExpiresAt?.ToUniversalTime() : null;
        var reason = expiry is null ? null : body.Reason.Trim();
        if (row.State == body.State && row.HotspotReview == body.HotspotReview && row.AssigneeUserId == body.AssigneeUserId && row.TeamId == body.TeamId && row.ExceptionExpiresAt == expiry && row.ExceptionReason == reason)
            return Results.Ok(FindingCollaboration.View(row));
        var resolving = row.State != "resolved" && body.State == "resolved";
        row.State = body.State; row.HotspotReview = body.HotspotReview; row.AssigneeUserId = body.AssigneeUserId; row.TeamId = body.TeamId;
        row.ExceptionExpiresAt = expiry; row.ExceptionReason = reason; row.Version++;
        await FindingCollaboration.DecisionAsync(db, row, actor, "review", body.Reason.Trim(), ct);
        // The linked work item learns about the resolution; it is never closed by CodeInsights.
        if (resolving && await db.Set<FindingWorkItem>().AnyAsync(x => x.FindingId == row.Id && x.State != "failed", ct))
            await FindingWorkItems.EnqueueAsync(db, "work-item-comment", $"{row.Id}|{row.Version}", ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(FindingCollaboration.View(row));
    }
}
