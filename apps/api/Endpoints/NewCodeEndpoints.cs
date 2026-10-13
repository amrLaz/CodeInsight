using CodeInsights.Api.Auth;
using CodeInsights.Api.Contracts;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class NewCodeEndpoints
{
    public static void MapNewCodeEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/new-code").RequireAuthorization();
        routes.MapGet("/definitions", ListAsync);
        routes.MapPut("/definitions/{level}/{**scopeKey}", PutAsync);
        routes.MapDelete("/definitions/{level}/{**scopeKey}", DeleteAsync);
        routes.MapGet("/projects/{projectKey}", ProjectAsync);
        routes.MapPut("/projects/{projectKey}/main-branch", PutMainBranchAsync);
        routes.MapPost("/resolve", ResolveAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        AuthService auth,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var principal = await auth.RequireAsync(context, ct: ct);
        var projectKey = context.Request.Query["project_key"].ToString();
        if (projectKey.Length == 0)
            await auth.RequireAsync(context, "admin", ct);
        else
            await auth.AssertProjectAccessAsync(principal, projectKey, ct: ct);
        var query = db.Set<NewCodeDefinition>().AsNoTracking();
        if (projectKey.Length > 0)
            query = query.Where(x => x.Level == "global" || x.ProjectKey == projectKey);
        return Results.Ok((await query.OrderBy(x => x.Level).ThenBy(x => x.ScopeKey).ToListAsync(ct)).Select(Response));
    }

    private static async Task<IResult> PutAsync(
        string level,
        string scopeKey,
        HttpContext context,
        AuthService auth,
        AuditService audit,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var actor = await auth.RequireAsync(context, "admin", ct);
        var body = await ApiJson.ReadAsync<NewCodeDefinitionRequest>(context, ct);
        var definition = await NewCode.ValidateAsync(db, body, ct);
        if (definition.Level != level || definition.ScopeKey != Uri.UnescapeDataString(scopeKey))
            throw new ApiException(422, "Route scope does not match New Code definition body");
        var row = await db.Set<NewCodeDefinition>().FindAsync([definition.ScopeKey], ct);
        if (row is null)
            db.Add(definition);
        else
        {
            row.Level = definition.Level;
            row.ProjectKey = definition.ProjectKey;
            row.Branch = definition.Branch;
            row.Mode = definition.Mode;
            row.NumberOfDays = definition.NumberOfDays;
            row.ReferenceBranch = definition.ReferenceBranch;
            row.AnalysisId = definition.AnalysisId;
            row.UpdatedAt = DateTime.UtcNow;
        }
        audit.Log(actor.Email, actor.Role, "new_code_definition.upsert", "new_code_definition", definition.ScopeKey, definition.ScopeKey, details: Response(definition));
        await db.SaveChangesAsync(ct);
        return Results.Ok(Response(row ?? definition));
    }

    private static async Task<IResult> DeleteAsync(
        string level,
        string scopeKey,
        HttpContext context,
        AuthService auth,
        AuditService audit,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var actor = await auth.RequireAsync(context, "admin", ct);
        var key = Uri.UnescapeDataString(scopeKey);
        var row = await db.Set<NewCodeDefinition>().FindAsync([key], ct)
            ?? throw new ApiException(404, "New Code definition not found");
        if (row.Level != level)
            throw new ApiException(404, "New Code definition not found");
        audit.Log(actor.Email, actor.Role, "new_code_definition.delete", "new_code_definition", row.ScopeKey, row.ScopeKey, details: Response(row));
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ProjectAsync(
        string projectKey,
        HttpContext context,
        AuthService auth,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var principal = await auth.RequireAsync(context, ct: ct);
        await auth.AssertProjectAccessAsync(principal, projectKey, ct: ct);
        var project = await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x => x.Key == projectKey, ct)
            ?? throw new ApiException(404, "Project not found");
        var definitions = await db.Set<NewCodeDefinition>().AsNoTracking()
            .Where(x => x.Level == "global" || x.ProjectKey == projectKey)
            .OrderBy(x => x.Level).ThenBy(x => x.ScopeKey)
            .ToListAsync(ct);
        return Results.Ok(new
        {
            project_key = projectKey,
            main_branch = project.MainBranch ?? await NewCode.ResolveMainBranchAsync(db, projectKey, ct),
            definitions = definitions.Select(Response),
        });
    }

    private static async Task<IResult> PutMainBranchAsync(
        string projectKey,
        HttpContext context,
        AuthService auth,
        AuditService audit,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var actor = await auth.RequireAsync(context, "admin", ct);
        var body = await ApiJson.ReadAsync<MainBranchRequest>(context, ct);
        if (string.IsNullOrWhiteSpace(body.MainBranch) || body.MainBranch.Length > 500 || body.MainBranch.Any(char.IsControl))
            throw new ApiException(422, "Invalid main_branch");
        var branch = BranchRefs.Canonical(body.MainBranch);
        var project = await db.Set<Project>().SingleOrDefaultAsync(x => x.Key == projectKey, ct)
            ?? throw new ApiException(404, "Project not found");
        if (await db.Set<NewCodeDefinition>().AnyAsync(x => x.Level == "project" && x.ProjectKey == projectKey && x.Mode == "reference_branch" && x.ReferenceBranch == branch, ct))
            throw new ApiException(422, "The project New Code reference cannot be its main branch");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        project.MainBranch = branch;
        audit.Log(actor.Email, actor.Role, "new_code.main_branch.assign", "project", project.Id, projectKey, details: new { main_branch = branch });
        await db.SaveChangesAsync(ct);
        // Deadlines only apply to the main branch: moving it changes which findings carry one.
        await FindingSla.InvalidateAsync(db, projectKey, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { project_key = projectKey, main_branch = branch });
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext context,
        AuthService auth,
        AppDbContext db,
        CancellationToken ct
    )
    {
        var principal = await auth.RequireAsync(context, ct: ct);
        var body = await ApiJson.ReadAsync<NewCodeResolveRequest>(context, ct);
        if (string.IsNullOrWhiteSpace(body.ProjectKey) || string.IsNullOrWhiteSpace(body.RepositoryUrl) || string.IsNullOrWhiteSpace(body.Branch))
            throw new ApiException(422, "project_key, repository_url and branch are required");
        if (
            principal.Kind == "ci"
            && (principal.Scopes ?? []).Contains("project:register")
            && !await db.Set<Project>().AnyAsync(x => x.Key == body.ProjectKey, ct)
        )
            await IngestionEndpoints.AutoRegisterAsync(
                db,
                principal,
                body.ProjectKey,
                body.ProfileId,
                body.ProfileVersion,
                body.CiProvider,
                body.RepositoryUrl,
                body.Branch,
                ct
            );
        await auth.AssertProjectAccessAsync(principal, body.ProjectKey, "analysis:write", ct);
        return Results.Ok(await NewCode.ResolveAsync(
            db,
            body.ProjectKey,
            body.RepositoryUrl,
            body.Branch,
            body.ProjectVersion,
            body.PrTargetBranch,
            body.NewCodeReferenceBranch,
            DateTime.UtcNow,
            ct
        ));
    }

    private static object Response(NewCodeDefinition definition) => new
    {
        definition.ScopeKey,
        definition.Level,
        definition.ProjectKey,
        definition.Branch,
        definition.Mode,
        definition.NumberOfDays,
        definition.ReferenceBranch,
        definition.AnalysisId,
        definition.UpdatedAt,
    };
}
