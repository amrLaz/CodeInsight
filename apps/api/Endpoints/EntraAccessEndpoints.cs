using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Endpoints;

public static class EntraAccessEndpoints
{
    public sealed record AccessPrincipalRequest
    {
        public string Role { get; init; } = "default";
        public bool IsActive { get; init; } = true;
        public string[] OrganizationIds { get; init; } = [];
        public string[] ProjectKeys { get; init; } = [];
    }

    public static IEndpointRouteBuilder MapEntraAccessEndpoints(
        this IEndpointRouteBuilder endpoints
    )
    {
        var group = endpoints.MapGroup("/api/v1/auth").RequireAuthorization();

        group.MapGet(
            "/me/photo",
            async (
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                IEntraGraphService graph,
                CancellationToken ct
            ) =>
            {
                var principal = await auth.RequireAsync(context, "user", ct);
                if (principal.IsPersonalToken)
                    throw new ApiException(403, "This action requires an interactive session");
                var user = await db.Set<PlatformUser>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.UserId == principal.Sub, ct);
                if (
                    user?.AuthProvider != "entra"
                    || string.IsNullOrWhiteSpace(user.EntraObjectId)
                )
                    return Results.NotFound();
                var photo = await graph.GetPhotoAsync(user.EntraObjectId, ct);
                if (photo is null)
                    return Results.NotFound();
                context.Response.Headers.CacheControl = "private, max-age=300";
                return Results.File(photo.Content, photo.ContentType);
            }
        );

        group.MapGet(
            "/directory-principals",
            async (
                string? kind,
                string search,
                string? cursor,
                HttpContext context,
                AuthService auth,
                IEntraGraphService graph,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return await graph.SearchAsync(kind ?? "all", search, cursor, ct);
            }
        );

        group.MapGet(
            "/access-principals/entra-user/{object_id}/photo",
            async (
                string object_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                IEntraGraphService graph,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var photo = await GetConfiguredUserPhotoAsync(db, graph, object_id, ct);
                if (photo is null)
                    return Results.NotFound();
                context.Response.Headers.CacheControl = "private, max-age=300";
                return Results.File(photo.Content, photo.ContentType);
            }
        );

        group.MapGet(
            "/access-principals",
            async (
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                IOptions<BootstrapOptions> bootstrap,
                IOptions<CodeInsights.Api.Auth.SessionOptions> session,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var users = await db.Set<PlatformUser>()
                    .AsNoTracking()
                    .OrderBy(x => x.FullName)
                    .ToListAsync(ct);
                var userGrants = await db.Set<UserGrant>().AsNoTracking().ToListAsync(ct);
                var groups = await db.Set<EntraAccessGroup>()
                    .AsNoTracking()
                    .OrderBy(x => x.DisplayName)
                    .ToListAsync(ct);
                var groupGrants = await db.Set<EntraGroupGrant>()
                    .AsNoTracking()
                    .ToListAsync(ct);
                var bootstrapOnly = session.Value.LocalAccountsMode == "bootstrap-only";
                return users
                    .Where(user =>
                        !bootstrapOnly
                        || user.AuthProvider == "entra"
                        || string.Equals(
                            user.Email,
                            bootstrap.Value.AdminEmail,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(user => (object)new
                    {
                        kind = user.AuthProvider == "entra" ? "entra-user" : "local",
                        principal_id = user.UserId,
                        object_id = user.EntraObjectId,
                        display_name = user.FullName,
                        email = user.Email,
                        role = user.Role,
                        is_active = user.IsActive,
                        read_only = bootstrapOnly
                            && string.Equals(
                                user.Email,
                                bootstrap.Value.AdminEmail,
                                StringComparison.OrdinalIgnoreCase
                            ),
                        created_at = ApiJson.Iso(user.CreatedAt),
                        last_login_at = user.LastLoginAt.HasValue
                            ? ApiJson.Iso(user.LastLoginAt.Value)
                            : null,
                        grants = userGrants
                            .Where(x => x.UserId == user.UserId)
                            .Select(x => new { x.ScopeType, x.ScopeId }),
                    })
                    .Concat(
                        groups.Select(accessGroup => (object)new
                        {
                            kind = "entra-group",
                            principal_id = accessGroup.Id,
                            object_id = accessGroup.ObjectId,
                            display_name = accessGroup.DisplayName,
                            email = (string?)null,
                            role = accessGroup.Role,
                            is_active = accessGroup.IsActive,
                            read_only = false,
                            created_at = ApiJson.Iso(accessGroup.CreatedAt),
                            last_login_at = (string?)null,
                            grants = groupGrants
                                .Where(x => x.GroupId == accessGroup.Id)
                                .Select(x => new { x.ScopeType, x.ScopeId }),
                        })
                    );
            }
        );

        group
            .MapPut(
                "/access-principals/{kind}/{object_id}",
                async (
                    string kind,
                    string object_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IEntraGraphService graph,
                    CancellationToken ct
                ) =>
                {
                    var caller = await auth.RequireAsync(context, "admin", ct);
                    ValidateKind(kind);
                    var request = await ApiJson.ReadAsync<AccessPrincipalRequest>(context, ct);
                    ValidateRole(request.Role);
                    var (organizations, projects) = await ValidateGrantsAsync(db, request, ct);
                    var directory = await graph.GetPrincipalAsync(kind, object_id, ct);
                    if (
                        !string.Equals(
                            directory.ObjectId,
                            object_id,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new ApiException(502, "Microsoft Graph returned another principal");
                    if (kind == "entra-group" && directory.HiddenMembership)
                        throw new ApiException(
                            422,
                            "Hidden-membership groups require Member.Read.Hidden"
                        );
                    var tenantId = await TenantIdAsync(db, ct);
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    string principalId;
                    bool isActive;
                    if (kind == "entra-user")
                    {
                        if (string.IsNullOrWhiteSpace(directory.Email))
                            throw new ApiException(422, "Entra user has no usable email address");
                        var email = directory.Email.Trim().ToLowerInvariant();
                        var user = await db.Set<PlatformUser>()
                            .FirstOrDefaultAsync(
                                x => x.EntraTenantId == tenantId && x.EntraObjectId == object_id,
                                ct
                            );
                        if (user is null)
                        {
                            if (await db.Set<PlatformUser>().AnyAsync(x => x.Email == email, ct))
                                throw new ApiException(
                                    409,
                                    "An account with this email already exists and is not linked to this Entra identity"
                                );
                            user = new PlatformUser
                            {
                                UserId = Guid.NewGuid().ToString(),
                                AuthProvider = "entra",
                                EntraTenantId = tenantId,
                                EntraObjectId = object_id,
                                CreatedAt = DateTime.UtcNow,
                            };
                            db.Add(user);
                        }
                        user.Email = email;
                        user.FullName = directory.DisplayName;
                        user.Role = request.Role;
                        user.IsActive = request.IsActive && directory.IsActive;
                        isActive = user.IsActive;
                        principalId = user.UserId;
                        await db.Set<UserGrant>()
                            .Where(x => x.UserId == principalId)
                            .ExecuteDeleteAsync(ct);
                        AddUserGrants(db, principalId, organizations, projects);
                    }
                    else
                    {
                        var accessGroup = await db.Set<EntraAccessGroup>()
                            .FirstOrDefaultAsync(
                                x => x.TenantId == tenantId && x.ObjectId == object_id,
                                ct
                            );
                        if (accessGroup is null)
                        {
                            accessGroup = new EntraAccessGroup
                            {
                                Id = Guid.NewGuid().ToString(),
                                TenantId = tenantId,
                                ObjectId = object_id,
                                CreatedAt = DateTime.UtcNow,
                            };
                            db.Add(accessGroup);
                        }
                        accessGroup.DisplayName = directory.DisplayName;
                        accessGroup.Role = request.Role;
                        accessGroup.IsActive = request.IsActive;
                        isActive = accessGroup.IsActive;
                        accessGroup.UpdatedAt = DateTime.UtcNow;
                        principalId = accessGroup.Id;
                        await db.Set<EntraGroupGrant>()
                            .Where(x => x.GroupId == principalId)
                            .ExecuteDeleteAsync(ct);
                        AddGroupGrants(db, principalId, organizations, projects);
                    }
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "directory.access.replace",
                        "directory_principal",
                        principalId,
                        directory.DisplayName,
                        new
                        {
                            kind,
                            object_id,
                            request.Role,
                            request.IsActive,
                            organization_ids = organizations,
                            project_keys = projects,
                        }
                    );
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Results.Ok(
                        new
                        {
                            kind,
                            principal_id = principalId,
                            object_id,
                            display_name = directory.DisplayName,
                            email = directory.Email,
                            role = request.Role,
                            is_active = isActive,
                            organization_ids = organizations,
                            project_keys = projects,
                        }
                    );
                }
            )
            .Accepts<AccessPrincipalRequest>("application/json");

        group.MapDelete(
            "/access-principals/{kind}/{object_id}",
            async (
                string kind,
                string object_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                IOptions<BootstrapOptions> bootstrap,
                CancellationToken ct
            ) =>
            {
                var caller = await auth.RequireAsync(context, "admin", ct);
                ValidateKind(kind);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (kind == "entra-user")
                {
                    var user = await db.Set<PlatformUser>()
                        .FirstOrDefaultAsync(x => x.EntraObjectId == object_id, ct)
                        ?? throw new ApiException(404, "Entra user access assignment not found");
                    if (caller.Sub == user.UserId)
                        throw new ApiException(400, "Cannot delete your own account");
                    if (
                        string.Equals(
                            user.Email,
                            bootstrap.Value.AdminEmail,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new ApiException(403, "The bootstrap account cannot be deleted");
                    await db.Set<UserGrant>()
                        .Where(x => x.UserId == user.UserId)
                        .ExecuteDeleteAsync(ct);
                    db.Remove(user);
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "directory.access.delete",
                        "user",
                        user.UserId,
                        user.Email
                    );
                }
                else
                {
                    var accessGroup = await db.Set<EntraAccessGroup>()
                        .FirstOrDefaultAsync(x => x.ObjectId == object_id, ct)
                        ?? throw new ApiException(404, "Entra group access assignment not found");
                    db.Remove(accessGroup);
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "directory.access.delete",
                        "group",
                        accessGroup.Id,
                        accessGroup.DisplayName
                    );
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
        return endpoints;
    }

    internal static async Task<EntraPhoto?> GetConfiguredUserPhotoAsync(
        AppDbContext db,
        IEntraGraphService graph,
        string objectId,
        CancellationToken ct = default
    )
    {
        var configured = await db.Set<PlatformUser>()
            .AsNoTracking()
            .AnyAsync(
                user => user.AuthProvider == "entra" && user.EntraObjectId == objectId,
                ct
            );
        return configured ? await graph.GetPhotoAsync(objectId, ct) : null;
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("entra-user" or "entra-group"))
            throw new ApiException(422, "Access principal kind must be entra-user or entra-group");
    }

    private static void ValidateRole(string role)
    {
        if (role is not ("admin" or "regular" or "default"))
            throw new ApiException(422, "Role must be admin, regular, or default");
    }

    private static async Task<(string[] Organizations, string[] Projects)> ValidateGrantsAsync(
        AppDbContext db,
        AccessPrincipalRequest request,
        CancellationToken ct
    )
    {
        if (
            request.OrganizationIds is null
            || request.ProjectKeys is null
            || request.OrganizationIds.Any(string.IsNullOrWhiteSpace)
            || request.ProjectKeys.Any(string.IsNullOrWhiteSpace)
        )
            throw new ApiException(422, "Grants must be arrays of non-empty strings");
        var organizations = request.OrganizationIds.Distinct().Order().ToArray();
        var projects = request.ProjectKeys.Distinct().Order().ToArray();
        var knownOrganizations = await db.Set<Organization>()
            .Where(x => organizations.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(ct);
        var knownProjects = await db.Set<Project>()
            .Where(x => projects.Contains(x.Key))
            .Select(x => x.Key)
            .ToListAsync(ct);
        if (
            organizations.Except(knownOrganizations).Any()
            || projects.Except(knownProjects).Any()
        )
            throw new ApiException(422, "Unknown organization or project in grants");
        return (organizations, projects);
    }

    private static async Task<string> TenantIdAsync(AppDbContext db, CancellationToken ct)
    {
        var config = await db.Set<AuthProviderConfig>()
            .AsNoTracking()
            .Where(x => x.ProviderType == "azure-ad" && x.Enabled)
            .Select(x => x.Config)
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiException(503, "Entra ID authentication is not enabled");
        try
        {
            using var payload = JsonDocument.Parse(config);
            if (
                payload.RootElement.TryGetProperty("tenant_id", out var tenant)
                && tenant.GetString() is { Length: > 0 } tenantId
            )
                return tenantId;
        }
        catch (JsonException) { }
        throw new ApiException(503, "Entra ID configuration is incomplete");
    }

    private static void AddUserGrants(
        AppDbContext db,
        string userId,
        IEnumerable<string> organizations,
        IEnumerable<string> projects
    )
    {
        if (!organizations.Any() && !projects.Any())
            db.Add(
                new UserGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    ScopeType = AccessGrantScopes.AllProjects,
                    ScopeId = AccessGrantScopes.AllProjectsId,
                }
            );
        foreach (var id in organizations)
            db.Add(
                new UserGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    ScopeType = "organization",
                    ScopeId = id,
                }
            );
        foreach (var key in projects)
            db.Add(
                new UserGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    ScopeType = "project",
                    ScopeId = key,
                }
            );
    }

    private static void AddGroupGrants(
        AppDbContext db,
        string groupId,
        IEnumerable<string> organizations,
        IEnumerable<string> projects
    )
    {
        if (!organizations.Any() && !projects.Any())
            db.Add(
                new EntraGroupGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    GroupId = groupId,
                    ScopeType = AccessGrantScopes.AllProjects,
                    ScopeId = AccessGrantScopes.AllProjectsId,
                }
            );
        foreach (var id in organizations)
            db.Add(
                new EntraGroupGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    GroupId = groupId,
                    ScopeType = "organization",
                    ScopeId = id,
                }
            );
        foreach (var key in projects)
            db.Add(
                new EntraGroupGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    GroupId = groupId,
                    ScopeType = "project",
                    ScopeId = key,
                }
            );
    }
}
