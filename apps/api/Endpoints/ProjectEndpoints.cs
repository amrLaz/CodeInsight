using System.ComponentModel.DataAnnotations;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/v1/projects").RequireAuthorization();
        routes.MapGet(
            "/analysis-profiles/catalog",
            async (HttpContext c, AuthService auth, CancellationToken ct) =>
            {
                auth.AssertCiPermission(await auth.RequireAsync(c, ct: ct));
                return GateEvaluation.BuiltInProfiles.Select(p => new
                {
                    profile_id = p.Id,
                    p.Profile.Version,
                    p.Profile.FormulaVersions,
                    p.Profile.RequiredMetrics,
                    p.Profile.RequiredTools,
                });
            }
        );
        routes.MapGet(
            "/organizations/catalog",
            async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                await auth.RequireAsync(c, "admin", ct);
                return await db.Set<Organization>()
                    .OrderBy(o => o.Name)
                    .Select(o => new
                    {
                        o.Id,
                        o.Key,
                        o.Name,
                    })
                    .ToListAsync(ct);
            }
        );
        routes
            .MapPost(
                "/organizations/catalog",
                async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<OrganizationRequest>(c, ct);
                    if (await db.Set<Organization>().AnyAsync(o => o.Key == body.Key, ct))
                        throw new ApiException(409, "Organization key already exists");
                    var o = new Organization
                    {
                        Id = Guid.NewGuid().ToString(),
                        Key = body.Key,
                        Name = body.Name,
                    };
                    db.Add(o);
                    await db.SaveChangesAsync(ct);
                    return new
                    {
                        o.Id,
                        o.Key,
                        o.Name,
                    };
                }
            )
            .Accepts<OrganizationRequest>("application/json");
        routes.MapGet(
            "",
            async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                var p = await auth.RequireAsync(c, ct: ct);
                var visible = await auth.AllowedProjectKeysAsync(p, ct: ct);
                var query = db.Set<Project>().Where(x => visible.Contains(x.Key));
                if (c.Request.Query.TryGetValue("archived", out var raw))
                {
                    var archived = raw.ToString().ToLowerInvariant() switch
                    {
                        "true" or "1" or "yes" or "on" => true,
                        "false" or "0" or "no" or "off" => false,
                        _ => throw new ApiException(422, "Invalid archived filter"),
                    };
                    query = query.Where(x => x.Archived == archived);
                }
                var projects = await query.ToListAsync(ct);
                if (c.Request.Query.TryGetValue("repo_url", out var repository))
                {
                    // Workstations know their clone URL, not the project key. Clone URLs differ by
                    // scheme, credentials and SSH form, so both sides are compared canonically.
                    var identity = RepositoryUrls.Identity(repository.ToString());
                    if (identity.Length == 0)
                        throw new ApiException(422, "Invalid repo_url filter");
                    var matched = await db.Set<ProjectIntegration>()
                        .AsNoTracking()
                        .Where(x => visible.Contains(x.ProjectKey) && x.RepoUrl != "")
                        .Select(x => new { x.ProjectKey, x.RepoUrl })
                        .ToListAsync(ct);
                    var keys = matched
                        .Where(x => RepositoryUrls.Identity(x.RepoUrl) == identity)
                        .Select(x => x.ProjectKey)
                        .ToHashSet();
                    projects = projects.Where(x => keys.Contains(x.Key)).ToList();
                }
                var result = new List<ProjectResponse>();
                foreach (var project in projects)
                    result.Add(await ResponseAsync(project, p.IsAdmin, db, ct));
                return result;
            }
        );
        routes
            .MapPost(
                "",
                async (HttpContext c, AuthService auth, AppDbContext db, CancellationToken ct) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<ProjectCreate>(c, ct);
                    if (await db.Set<Project>().AnyAsync(p => p.Key == body.Key, ct))
                        throw new ApiException(409, $"Project key '{body.Key}' already exists");
                    if (
                        !string.IsNullOrEmpty(body.OrganizationId)
                        && !await db.Set<Organization>()
                            .AnyAsync(o => o.Id == body.OrganizationId, ct)
                    )
                        throw new ApiException(422, "Unknown organization");
                    ValidateProfile(body.AnalysisProfileId, body.AnalysisProfileVersion);
                    var project = new Project
                    {
                        Id = Guid.NewGuid().ToString(),
                        Key = body.Key,
                        Name = body.Name,
                        Description = body.Description,
                        OrganizationId = body.OrganizationId,
                        AnalysisProfileId = body.AnalysisProfileId,
                        AnalysisProfileVersion = body.AnalysisProfileVersion,
                    };
                    db.Add(project);
                    await db.SaveChangesAsync(ct);
                    return await ResponseAsync(project, true, db, ct);
                }
            )
            .Accepts<ProjectCreate>("application/json");
        routes.MapGet(
            "/{project_id}",
            async (
                string project_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                var p = await auth.RequireAsync(c, ct: ct);
                var project = await FindAsync(db, project_id, ct);
                await auth.AssertProjectAccessAsync(p, project.Key, ct: ct);
                return await ResponseAsync(project, p.IsAdmin, db, ct);
            }
        );
        routes
            .MapPatch(
                "/{project_id}",
                async (
                    string project_id,
                    HttpContext c,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<ProjectUpdate>(c, ct);
                    var project = await FindAsync(db, project_id, ct);
                    if (body.Name is not null)
                        project.Name = body.Name;
                    if (body.Description is not null)
                        project.Description = body.Description;
                    if (body.Archived is not null)
                        project.Archived = body.Archived.Value;
                    if (body.OrganizationId is not null)
                    {
                        if (
                            !await db.Set<Organization>()
                                .AnyAsync(o => o.Id == body.OrganizationId, ct)
                        )
                            throw new ApiException(422, "Unknown organization");
                        project.OrganizationId = body.OrganizationId;
                    }
                    if (
                        body.AnalysisProfileId is not null
                        || body.AnalysisProfileVersion is not null
                    )
                    {
                        ValidateProfile(
                            body.AnalysisProfileId ?? project.AnalysisProfileId,
                            body.AnalysisProfileVersion ?? project.AnalysisProfileVersion
                        );
                        project.AnalysisProfileId =
                            body.AnalysisProfileId ?? project.AnalysisProfileId;
                        project.AnalysisProfileVersion =
                            body.AnalysisProfileVersion ?? project.AnalysisProfileVersion;
                    }
                    project.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return await ResponseAsync(project, true, db, ct);
                }
            )
            .Accepts<ProjectUpdate>("application/json");
        routes.MapDelete(
            "/{project_id}",
            async (
                string project_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(c, "admin", ct);
                var project = await FindAsync(db, project_id, ct);
                project.Archived = true;
                project.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return new { status = "archived", project_id };
            }
        );
        routes.MapGet(
            "/{project_id}/branches",
            async (
                string project_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                var p = await auth.RequireAsync(c, ct: ct);
                var project = await FindAsync(db, project_id, ct);
                await auth.AssertProjectAccessAsync(p, project.Key, ct: ct);
                var branches = await db.Set<Branch>()
                    .Where(b => b.ProjectId == project_id)
                    .OrderByDescending(b => b.IsDefault)
                    .ThenBy(b => b.Name)
                    .ToListAsync(ct);
                var names = branches.Select(b => b.Name).ToArray();
                var latest = await db.Set<AnalysisRecord>()
                    .Where(a => a.ProjectKey == project.Key && names.Contains(a.Branch))
                    .GroupBy(a => a.Branch)
                    .Select(g => g.OrderByDescending(a => a.CreatedAt).First())
                    .ToListAsync(ct);
                var byBranch = latest.ToDictionary(a => a.Branch);
                return branches.Select(b =>
                {
                    byBranch.TryGetValue(b.Name, out var a);
                    return new BranchMetrics(
                        b.Name,
                        a?.AnalysisId,
                        a?.MaintainabilityScore,
                        a?.SecurityScore,
                        a?.ReliabilityScore,
                        a?.SecurityRating,
                        a?.ReliabilityRating,
                        a?.MaintainabilityRating,
                        a?.SecurityReviewRating,
                        a?.Coverage,
                        a?.Duplication,
                        a?.CodeSmells,
                        a?.Vulnerabilities,
                        a?.Bugs,
                        a?.QualityGate,
                        a?.CreatedAt
                    );
                });
            }
        );
        routes
            .MapPost(
                "/{project_id}/retention-policy",
                async (
                    string project_id,
                    HttpContext c,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(c, "admin", ct);
                    var body = await ApiJson.ReadAsync<RetentionPolicyRequest>(c, ct);
                    await FindAsync(db, project_id, ct);
                    var policy = await db.Set<RetentionPolicy>()
                        .FirstOrDefaultAsync(p => p.ProjectId == project_id, ct);
                    if (policy is null)
                    {
                        policy = new RetentionPolicy
                        {
                            Id = Guid.NewGuid().ToString(),
                            ProjectId = project_id,
                        };
                        db.Add(policy);
                    }
                    policy.KeepAnalysesDays = body.KeepAnalysesDays;
                    policy.KeepFailedAnalysesDays = body.KeepFailedAnalysesDays;
                    policy.KeepMasterBranchDays = body.KeepMasterBranchDays;
                    policy.AutoDeleteEnabled = body.AutoDeleteEnabled;
                    policy.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return PolicyResponse(policy);
                }
            )
            .Accepts<RetentionPolicyRequest>("application/json");
        routes.MapGet(
            "/{project_id}/retention-policy",
            async (
                string project_id,
                HttpContext c,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                var p = await auth.RequireAsync(c, ct: ct);
                var project = await FindAsync(db, project_id, ct);
                await auth.AssertProjectAccessAsync(p, project.Key, ct: ct);
                var policy =
                    await db.Set<RetentionPolicy>()
                        .FirstOrDefaultAsync(p => p.ProjectId == project_id, ct)
                    ?? throw new ApiException(404, "Retention policy not found");
                return PolicyResponse(policy);
            }
        );
    }

    public static void ValidateProfile(string? id, string? version)
    {
        if (id is null && version is null)
            return;
        if (!GateEvaluation.TryGetProfile(id ?? "", version ?? "", out _) )
            throw new ApiException(422, "Unknown analysis profile or version");
    }

    private static async Task<Project> FindAsync(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<Project>().FindAsync([id], ct)
        ?? throw new ApiException(404, "Project not found");

    private static async Task<ProjectResponse> ResponseAsync(
        Project p,
        bool manage,
        AppDbContext db,
        CancellationToken ct
    ) =>
        new(
            p.Id,
            p.Key,
            p.Name,
            p.Description,
            p.Archived,
            ApiJson.Iso(p.CreatedAt),
            ApiJson.Iso(p.UpdatedAt),
            await db.Set<Branch>().CountAsync(b => b.ProjectId == p.Id, ct),
            await db.Set<AnalysisRecord>().CountAsync(a => a.ProjectKey == p.Key, ct),
            p.OrganizationId,
            new Dictionary<string, bool> { ["read"] = true, ["manage"] = manage },
            p.AnalysisProfileId,
            p.AnalysisProfileVersion,
            p.MainBranch
        );

    private static object PolicyResponse(RetentionPolicy p) =>
        new
        {
            p.Id,
            p.ProjectId,
            p.KeepAnalysesDays,
            p.KeepFailedAnalysesDays,
            p.KeepMasterBranchDays,
            p.AutoDeleteEnabled,
            last_cleanup_at = p.LastCleanupAt.HasValue ? ApiJson.Iso(p.LastCleanupAt.Value) : null,
        };
}

public sealed class OrganizationRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Key { get; set; } = "";

    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";
}

public sealed class ProjectCreate
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Key { get; set; } = "";

    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? OrganizationId { get; set; }
    public string? AnalysisProfileId { get; set; }
    public string? AnalysisProfileVersion { get; set; }
}

public sealed class ProjectUpdate
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? Archived { get; set; }
    public string? OrganizationId { get; set; }
    public string? AnalysisProfileId { get; set; }
    public string? AnalysisProfileVersion { get; set; }
}

public sealed class RetentionPolicyRequest
{
    [Range(1, 3650)]
    public int KeepAnalysesDays { get; set; } = 90;

    [Range(1, 365)]
    public int KeepFailedAnalysesDays { get; set; } = 30;

    [Range(1, 3650)]
    public int KeepMasterBranchDays { get; set; } = 365;
    public bool AutoDeleteEnabled { get; set; } = true;
}

public sealed record ProjectResponse(
    string Id,
    string Key,
    string Name,
    string? Description,
    bool Archived,
    string CreatedAt,
    string UpdatedAt,
    int BranchCount,
    int AnalysisCount,
    string? OrganizationId,
    Dictionary<string, bool> Permissions,
    string? AnalysisProfileId,
    string? AnalysisProfileVersion,
    string? MainBranch
);

public sealed record BranchMetrics(
    string BranchName,
    string? LatestAnalysisId,
    int? MaintainabilityScore,
    int? SecurityScore,
    int? ReliabilityScore,
    string? SecurityRating,
    string? ReliabilityRating,
    string? MaintainabilityRating,
    string? SecurityReviewRating,
    int? Coverage,
    int? Duplication,
    int? CodeSmells,
    int? Vulnerabilities,
    int? Bugs,
    string? QualityGate,
    string? AnalysisDate
);
