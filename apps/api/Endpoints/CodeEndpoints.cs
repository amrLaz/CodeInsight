using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

/// <summary>Versioned code scopes and tracked branches used by ingestion and New Code evaluation.</summary>
public static class CodeEndpoints
{
    public static IEndpointRouteBuilder MapCodeEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/code/projects").RequireAuthorization();
        routes.MapGet("/{key}/configuration", async (string key, HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
        {
            await Access(key, context, auth, false, ct);
            var snapshot = await CodeScopes.CaptureAsync(db, key, ct);
            return Results.Ok(new
            {
                snapshot.Definition,
                version = snapshot.Definition?.Version,
                repository_url = snapshot.Definition?.RepositoryUrl,
                snapshot.BaselineSha,
                snapshot.BaselineAnalysisId,
                snapshot.Reason
            });
        });
        routes.MapGet("/{key}/versions", async (string key, HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
        {
            await Access(key, context, auth, false, ct);
            var size = InsightEndpoints.PageSize(context.Request);
            var cursor = context.Request.Query["cursor"].ToString();
            var rows = await db.Set<CodeScopeVersion>().AsNoTracking().Where(x => x.ProjectKey == key && string.Compare(x.Version, cursor) > 0)
                .OrderBy(x => x.Version).Take(size + 1).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, rows[^1].Version); }
            return Results.Ok(rows.Select(x => JsonSerializer.Deserialize<CodeScopeDefinition>(x.Definition, ApiJson.Options)));
        });
        routes.MapPost("/{key}/versions", async (string key, HttpContext context, AppDbContext db, AuthService auth, AuditService audit, CancellationToken ct) =>
        {
            var actor = await Access(key, context, auth, true, ct);
            var body = await ApiJson.ReadAsync<CodeScopeDefinition>(context, ct);
            CodeScopes.Validate(body);
            if (body.ReferenceKind == "analysis" && !await db.Set<AnalysisRecord>().AnyAsync(x => x.AnalysisId == body.ReferenceAnalysisId && x.ProjectKey == key, ct))
                throw new ApiException(422, "Reference analysis must belong to this project");
            if (await db.Set<CodeScopeVersion>().AnyAsync(x => x.ProjectKey == key && x.Version == body.Version, ct)) throw new ApiException(409, "Code scope versions are immutable");
            db.Add(new CodeScopeVersion { ProjectKey = key, Version = body.Version, Definition = JsonSerializer.Serialize(body, ApiJson.Options) });
            audit.Log(actor.Email, actor.Role, "code_scope.publish", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/code/projects/{Uri.EscapeDataString(key)}/versions", body);
        });
        routes.MapPut("/{key}/configuration", async (string key, HttpContext context, AppDbContext db, AuthService auth, AuditService audit, CancellationToken ct) =>
        {
            var actor = await Access(key, context, auth, true, ct);
            var body = await ApiJson.ReadAsync<Contracts.CodeScopeAssignment>(context, ct);
            if (!await db.Set<CodeScopeVersion>().AnyAsync(x => x.ProjectKey == key && x.Version == body.Version, ct)) throw new ApiException(422, "Publish the code scope version first");
            var row = await db.Set<Data.CodeScopeAssignment>().FindAsync([key], ct);
            if (row is null) { row = new Data.CodeScopeAssignment { ProjectKey = key }; db.Add(row); }
            row.Version = body.Version;
            audit.Log(actor.Email, actor.Role, "code_scope.assign", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(body);
        });
        routes.MapGet("/{key}/branches", async (string key, HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
        {
            await Access(key, context, auth, false, ct);
            var cursor = context.Request.Query["cursor"].ToString();
            var size = InsightEndpoints.PageSize(context.Request);
            var rows = await db.Set<AnalysisRecord>().Where(x => x.ProjectKey == key).Select(x => x.Branch).Distinct()
                .Where(x => string.Compare(x, cursor) > 0).OrderBy(x => x).Take(size + 1).ToListAsync(ct);
            if (rows.Count > size) { rows.RemoveAt(size); InsightEndpoints.Continuation(context, rows[^1]); }
            var archived = await db.Set<ArchivedCodeBranch>().Where(x => x.ProjectKey == key && rows.Contains(x.Branch) && x.Archived).Select(x => x.Branch).ToListAsync(ct);
            var mainBranch = await db.Set<Project>().Where(x => x.Key == key).Select(x => x.MainBranch).SingleOrDefaultAsync(ct);
            if (mainBranch is null)
                mainBranch = (await CodeScopes.CaptureAsync(db, key, ct)).Definition?.MainBranch;
            return Results.Ok(rows.Select(branch => new { branch, archived = archived.Contains(branch), is_main = branch == mainBranch }));
        });
        routes.MapPut("/{key}/branches", async (string key, HttpContext context, AppDbContext db, AuthService auth, AuditService audit, CancellationToken ct) =>
        {
            var actor = await Access(key, context, auth, true, ct);
            var body = await ApiJson.ReadAsync<BranchArchiveRequest>(context, ct);
            if (!CodeScopes.Branch(body.Branch)) throw new ApiException(422, "Invalid branch");
            var branch = BranchRefs.Canonical(body.Branch);
            var row = await db.Set<ArchivedCodeBranch>().FindAsync([key, branch], ct);
            if (row is null) { row = new ArchivedCodeBranch { ProjectKey = key, Branch = branch }; db.Add(row); }
            row.Archived = body.Archived;
            audit.Log(actor.Email, actor.Role, "code_branch.archive", "project", resourceLabel: key, details: body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(body);
        });
        return app;
    }

    private static async Task<Principal> Access(string key, HttpContext context, AuthService auth, bool write, CancellationToken ct)
    {
        var user = await auth.RequireAsync(context, write ? "admin" : "platform", ct);
        await auth.AssertProjectAccessAsync(user, key, write ? "analysis:write" : "read", ct);
        return user;
    }
}
