using System.Text;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SessionConfiguration = CodeInsights.Api.Auth.SessionOptions;

namespace CodeInsights.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Email, string Password);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record CreateUserRequest(
        string Email,
        string FullName,
        string Role = "default",
        string? Password = null
    );

    public sealed record UpdateUserRequest(
        string? Role = null,
        bool? IsActive = null,
        string? FullName = null
    );

    public sealed record UserGrantsRequest
    {
        public string[] OrganizationIds { get; init; } = [];
        public string[] ProjectKeys { get; init; } = [];
    }

    public sealed record ResetPasswordRequest(string NewPassword);

    public sealed record PersonalTokenRequest(string Name, int TtlDays, string[]? Scopes = null);

    private static void Required(string? value, string field)
    {
        if (value is null)
            throw new ApiException(
                422,
                new[]
                {
                    new
                    {
                        type = "missing",
                        loc = new[] { "body", field },
                        msg = "Field required",
                    },
                }
            );
    }

    private static void ValidRole(
        string role,
        string detail = "Role must be admin, regular, or default"
    )
    {
        if (role is not ("admin" or "regular" or "default"))
            throw new ApiException(422, detail);
    }

    private static async Task<PlatformUser> FindUser(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<PlatformUser>().FindAsync([id], ct)
        ?? throw new ApiException(404, "User not found");

    private static async Task<Principal> Self(
        AuthService auth,
        HttpContext context,
        CancellationToken ct
    )
    {
        var principal = await auth.RequireAsync(context, "user", ct);
        if (principal.Role is not ("admin" or "regular" or "default"))
            throw new ApiException(403, "This action requires one of: admin, regular, default");
        return principal;
    }

    /// <summary>Same identity check, but refuses a personal access token acting on the tokens themselves.</summary>
    private static async Task<Principal> InteractiveSelf(
        AuthService auth,
        HttpContext context,
        CancellationToken ct
    )
    {
        var principal = await Self(auth, context, ct);
        if (principal.IsPersonalToken)
            throw new ApiException(403, "This action requires an interactive session");
        return principal;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").RequireAuthorization();
        group
            .MapGet(
                "/methods",
                async (
                    AppDbContext db,
                    IOptions<SessionConfiguration> configured,
                    CancellationToken ct
                ) =>
                {
                    var entraEnabled = await db.Set<AuthProviderConfig>()
                        .AnyAsync(x => x.ProviderType == "azure-ad" && x.Enabled, ct);
                    return new
                    {
                        entra_enabled = entraEnabled,
                        auto_entra_sign_in = entraEnabled && configured.Value.AutoEntraSignIn,
                        local_login_visible = configured.Value.LocalLoginVisible,
                    };
                }
            )
            .AllowAnonymous();
        group
            .MapPost(
                "/login",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IOptions<SessionConfiguration> configured,
                    IOptions<BootstrapOptions> bootstrap,
                    CancellationToken ct
                ) =>
                {
                    var request = await ApiJson.ReadAsync<LoginRequest>(context, ct);
                    Required(request.Email, "email");
                    Required(request.Password, "password");
                    var email = request.Email.Trim().ToLowerInvariant();
                    if (
                        configured.Value.LocalAccountsMode == "bootstrap-only"
                        && !string.Equals(
                            email,
                            bootstrap.Value.AdminEmail.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new ApiException(401, "Invalid credentials");
                    var user = await db.Set<PlatformUser>()
                        .FirstOrDefaultAsync(
                            x => x.Email == email && x.AuthProvider == "local" && x.IsActive,
                            ct
                        );
                    if (
                        user?.HashedPassword is null
                        || !AuthService.VerifyPassword(request.Password, user.HashedPassword)
                    )
                        throw new ApiException(401, "Invalid credentials");
                    user.LastLoginAt = DateTime.UtcNow;
                    audit.Log(
                        user.Email,
                        user.Role,
                        "user.login",
                        "session",
                        resourceLabel: user.Email,
                        details: new { provider = "local" }
                    );
                    await db.SaveChangesAsync(ct);
                    return new
                    {
                        token = await auth.SignSessionTokenAsync(user, ct),
                        user = new
                        {
                            user.UserId,
                            user.Email,
                            user.FullName,
                            user.Role,
                        },
                    };
                }
            )
            .Accepts<LoginRequest>("application/json")
            .AllowAnonymous();
        group.MapGet(
            "/me",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                var principal = await Self(auth, context, ct);
                var user = await FindUser(db, principal.Sub, ct);
                return new
                {
                    user.UserId,
                    user.Email,
                    user.FullName,
                    Role = principal.Role,
                    user.AuthProvider,
                    last_login_at = user.LastLoginAt.HasValue
                        ? ApiJson.Iso(user.LastLoginAt.Value)
                        : null,
                    directory_access_status = user.AuthProvider == "entra"
                        ? principal.DirectoryAccessAvailable ? "available" : "unavailable"
                        : "not_applicable",
                    permissions = new
                    {
                        is_admin = principal.IsAdmin,
                        project_keys = (
                            await auth.AllowedProjectKeysAsync(principal, ct: ct)
                        ).Order(StringComparer.Ordinal),
                    },
                };
            }
        );
        group
            .MapPut(
                "/me/password",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    var principal = await Self(auth, context, ct);
                    var request = await ApiJson.ReadAsync<ChangePasswordRequest>(context, ct);
                    Required(request.CurrentPassword, "current_password");
                    Required(request.NewPassword, "new_password");
                    var user = await FindUser(db, principal.Sub, ct);
                    if (user.AuthProvider != "local" || user.HashedPassword is null)
                        throw new ApiException(
                            400,
                            "Password change not available for SSO accounts"
                        );
                    if (!AuthService.VerifyPassword(request.CurrentPassword, user.HashedPassword))
                        throw new ApiException(401, "Current password is incorrect");
                    if (request.NewPassword.EnumerateRunes().Count() < 8)
                        throw new ApiException(422, "New password must be at least 8 characters");
                    user.HashedPassword = AuthService.HashPassword(request.NewPassword);
                    await db.SaveChangesAsync(ct);
                    return new { status = "password updated" };
                }
            )
            .Accepts<ChangePasswordRequest>("application/json");
        group
            .MapPost(
                "/me/tokens",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    CancellationToken ct
                ) =>
                {
                    // Minting a credential is an interactive act: a token can never mint another one.
                    var principal = await InteractiveSelf(auth, context, ct);
                    var request = await ApiJson.ReadAsync<PersonalTokenRequest>(context, ct);
                    var name = request.Name.Trim();
                    if (name.Length is 0 or > 255)
                        throw new ApiException(422, "Token name must contain 1 to 255 characters");
                    if (request.TtlDays is < 1 or > 365)
                        throw new ApiException(422, "Token expiration must be between 1 and 365 days");
                    var scopes = (request.Scopes ?? [PersonalTokenScopes.Read])
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .Append(PersonalTokenScopes.Read)
                        .Distinct()
                        .Order()
                        .ToArray();
                    if (scopes.Except(PersonalTokenScopes.Allowed).Any())
                        throw new ApiException(422, "Invalid personal access token scope");
                    if (
                        await db.Set<PlatformApiToken>()
                            .CountAsync(x => x.OwnerUserId == principal.Sub && !x.Revoked, ct) >= 10
                    )
                        throw new ApiException(409, "Revoke an existing personal access token first");
                    var plaintext =
                        "ci_pat_"
                        + AuthService.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    var now = DateTime.UtcNow;
                    var token = new PlatformApiToken
                    {
                        TokenId = Guid.NewGuid().ToString(),
                        TokenName = name,
                        TokenHash = AuthService.Sha256(plaintext),
                        TokenPreview = "..." + plaintext[^4..],
                        CreatedAt = now,
                        ExpiresAt = now.AddDays(request.TtlDays),
                        IsCurrent = true,
                        Revoked = false,
                        Scopes = System.Text.Json.JsonSerializer.Serialize(scopes),
                        OwnerUserId = principal.Sub,
                    };
                    db.Add(token);
                    audit.Log(
                        principal.Email,
                        principal.Role,
                        "token.personal.create",
                        "api_token",
                        token.TokenId,
                        token.TokenName,
                        new
                        {
                            preview = token.TokenPreview,
                            expires_at = ApiJson.Iso(token.ExpiresAt!.Value),
                            scopes,
                        }
                    );
                    await db.SaveChangesAsync(ct);
                    // The plaintext is returned once; only its SHA-256 fingerprint is persisted.
                    return Results.Json(
                        new
                        {
                            token_id = token.TokenId,
                            token = plaintext,
                            name = token.TokenName,
                            scopes,
                            created_at = ApiJson.Iso(now),
                            expires_at = ApiJson.Iso(token.ExpiresAt.Value),
                        },
                        ApiJson.Options,
                        statusCode: 201
                    );
                }
            )
            .Accepts<PersonalTokenRequest>("application/json");
        group.MapGet(
            "/me/tokens",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                var principal = await Self(auth, context, ct);
                return await db.Set<PlatformApiToken>()
                    .AsNoTracking()
                    .Where(x => x.OwnerUserId == principal.Sub)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new
                    {
                        token_id = x.TokenId,
                        name = x.TokenName,
                        preview = x.TokenPreview,
                        created_at = x.CreatedAt,
                        expires_at = x.ExpiresAt,
                        revoked = x.Revoked,
                    })
                    .ToListAsync(ct);
            }
        );
        group.MapDelete(
            "/me/tokens/{token_id}",
            async (
                string token_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                CancellationToken ct
            ) =>
            {
                var principal = await InteractiveSelf(auth, context, ct);
                var token = await db.Set<PlatformApiToken>()
                    .FirstOrDefaultAsync(x => x.TokenId == token_id && x.OwnerUserId == principal.Sub, ct);
                if (token is null)
                    throw new ApiException(404, "Personal access token not found");
                // Revocation is idempotent: a client retrying after a lost response must not fail.
                if (token.Revoked)
                    return Results.NoContent();
                token.Revoked = true;
                token.IsCurrent = false;
                audit.Log(
                    principal.Email,
                    principal.Role,
                    "token.personal.revoke",
                    "api_token",
                    token.TokenId,
                    token.TokenName
                );
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapPut(
            "/me/favorite-projects/{project_key}",
            async (
                string project_key,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
            {
                var principal = await Self(auth, context, ct);
                var visible = await auth.AllowedProjectKeysAsync(principal, ct: ct);
                var project = await db.Set<Project>()
                    .Where(p => p.Key == project_key && visible.Contains(p.Key))
                    .Where(p => db.Set<AnalysisRecord>().Any(a => a.ProjectKey == p.Key))
                    .Select(p => new { p.Id })
                    .SingleOrDefaultAsync(ct);
                if (project is null)
                    throw new ApiException(404, "Authorized analyzed project not found");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO user_project_favorites (user_id, project_id, created_at) VALUES ({principal.Sub}, {project.Id}, {DateTime.UtcNow}) ON CONFLICT (user_id, project_id) DO NOTHING",
                    ct
                );
                return Results.NoContent();
            }
        );
        group.MapDelete(
            "/me/favorite-projects/{project_key}",
            async (
                string project_key,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
            {
                var principal = await Self(auth, context, ct);
                await db.Set<UserProjectFavorite>()
                    .Where(x => x.UserId == principal.Sub)
                    .Where(x => db.Set<Project>().Any(p => p.Id == x.ProjectId && p.Key == project_key))
                    .ExecuteDeleteAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapGet(
            "/users",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var users = await db.Set<PlatformUser>().OrderBy(x => x.CreatedAt).ToListAsync(ct);
                var grants = await db.Set<UserGrant>().ToListAsync(ct);
                return users.Select(user => new
                {
                    user.UserId,
                    user.Email,
                    user.FullName,
                    user.Role,
                    user.AuthProvider,
                    user.IsActive,
                    created_at = ApiJson.Iso(user.CreatedAt),
                    last_login_at = user.LastLoginAt.HasValue
                        ? ApiJson.Iso(user.LastLoginAt.Value)
                        : null,
                    grants = grants
                        .Where(g => g.UserId == user.UserId)
                        .Select(g => new { g.ScopeType, g.ScopeId }),
                });
            }
        );
        group
            .MapPost(
                "/users",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IOptions<SessionConfiguration> configured,
                    CancellationToken ct
                ) =>
                {
                    var caller = await auth.RequireAsync(context, "admin", ct);
                    if (configured.Value.LocalAccountsMode == "bootstrap-only")
                        throw new ApiException(
                            403,
                            "Local user creation is disabled in bootstrap-only mode"
                        );
                    var request = await ApiJson.ReadAsync<CreateUserRequest>(context, ct);
                    Required(request.Email, "email");
                    Required(request.FullName, "full_name");
                    Required(request.Role, "role");
                    var email = request.Email.Trim().ToLowerInvariant();
                    if (await db.Set<PlatformUser>().AnyAsync(x => x.Email == email, ct))
                        throw new ApiException(409, "A user with this email already exists");
                    ValidRole(request.Role);
                    if (!string.IsNullOrEmpty(request.Password) && request.Password.EnumerateRunes().Count() < 8)
                        throw new ApiException(422, "Password must be at least 8 characters");
                    var user = new PlatformUser
                    {
                        UserId = Guid.NewGuid().ToString(),
                        Email = email,
                        FullName = request.FullName.Trim(),
                        Role = request.Role,
                        HashedPassword = string.IsNullOrEmpty(request.Password)
                            ? null
                            : AuthService.HashPassword(request.Password),
                        AuthProvider = "local",
                        IsActive = true,
                    };
                    db.Add(user);
                    db.Add(
                        new UserGrant
                        {
                            Id = Guid.NewGuid().ToString(),
                            UserId = user.UserId,
                            ScopeType = AccessGrantScopes.AllProjects,
                            ScopeId = AccessGrantScopes.AllProjectsId,
                        }
                    );
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "user.create",
                        "user",
                        user.UserId,
                        email,
                        new { role = request.Role }
                    );
                    await db.SaveChangesAsync(ct);
                    return new
                    {
                        user.UserId,
                        user.Email,
                        user.Role,
                    };
                }
            )
            .Accepts<CreateUserRequest>("application/json");
        group
            .MapPut(
                "/users/{user_id}/grants",
                async (
                    string user_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IOptions<SessionConfiguration> configured,
                    IOptions<BootstrapOptions> bootstrap,
                    CancellationToken ct
                ) =>
                {
                    var caller = await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<UserGrantsRequest>(context, ct);
                    var user = await FindUser(db, user_id, ct);
                    if (
                        configured.Value.LocalAccountsMode == "bootstrap-only"
                        && string.Equals(
                            user.Email,
                            bootstrap.Value.AdminEmail,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new ApiException(403, "The bootstrap account is read-only");
                    if (
                        request.OrganizationIds is null
                        || request.ProjectKeys is null
                        || request.OrganizationIds.Any(x => x is null)
                        || request.ProjectKeys.Any(x => x is null)
                    )
                        throw new ApiException(422, "Grants must be arrays of strings");
                    var organizations = request.OrganizationIds.ToHashSet();
                    var projects = (request.ProjectKeys ?? []).ToHashSet();
                    var knownOrganizations = (
                        await db.Set<Organization>()
                            .Where(x => organizations.Contains(x.Id))
                            .Select(x => x.Id)
                            .ToListAsync(ct)
                    ).ToHashSet();
                    var knownProjects = (
                        await db.Set<Project>()
                            .Where(x => projects.Contains(x.Key))
                            .Select(x => x.Key)
                            .ToListAsync(ct)
                    ).ToHashSet();
                    if (
                        !organizations.SetEquals(knownOrganizations)
                        || !projects.SetEquals(knownProjects)
                    )
                        throw new ApiException(422, "Unknown organization or project in grants");
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    await db.Set<UserGrant>()
                        .Where(x => x.UserId == user_id)
                        .ExecuteDeleteAsync(ct);
                    if (organizations.Count == 0 && projects.Count == 0)
                        db.Add(
                            new UserGrant
                            {
                                Id = Guid.NewGuid().ToString(),
                                UserId = user_id,
                                ScopeType = AccessGrantScopes.AllProjects,
                                ScopeId = AccessGrantScopes.AllProjectsId,
                            }
                        );
                    foreach (var id in organizations.Order(StringComparer.Ordinal))
                        db.Add(
                            new UserGrant
                            {
                                Id = Guid.NewGuid().ToString(),
                                UserId = user_id,
                                ScopeType = "organization",
                                ScopeId = id,
                            }
                        );
                    foreach (var key in projects.Order(StringComparer.Ordinal))
                        db.Add(
                            new UserGrant
                            {
                                Id = Guid.NewGuid().ToString(),
                                UserId = user_id,
                                ScopeType = "project",
                                ScopeId = key,
                            }
                        );
                    var result = new
                    {
                        user_id,
                        organization_ids = organizations.Order(StringComparer.Ordinal),
                        project_keys = projects.Order(StringComparer.Ordinal),
                    };
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "user.grants.replace",
                        "user",
                        user_id,
                        user.Email,
                        new { result.organization_ids, result.project_keys }
                    );
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
            )
            .Accepts<UserGrantsRequest>("application/json");
        group
            .MapPut(
                "/users/{user_id}",
                async (
                    string user_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IOptions<SessionConfiguration> configured,
                    IOptions<BootstrapOptions> bootstrap,
                    CancellationToken ct
                ) =>
                {
                    var caller = await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<UpdateUserRequest>(context, ct);
                    var user = await FindUser(db, user_id, ct);
                    if (
                        configured.Value.LocalAccountsMode == "bootstrap-only"
                        && string.Equals(
                            user.Email,
                            bootstrap.Value.AdminEmail,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new ApiException(403, "The bootstrap account is read-only");
                    var changes = new Dictionary<string, object>();
                    if (request.Role is not null)
                    {
                        ValidRole(request.Role, "Invalid role");
                        changes["role"] = new { from = user.Role, to = request.Role };
                        user.Role = request.Role;
                    }
                    if (request.IsActive is { } active)
                    {
                        changes["is_active"] = new { from = user.IsActive, to = active };
                        user.IsActive = active;
                    }
                    if (request.FullName is not null)
                        user.FullName = request.FullName.Trim();
                    if (changes.Count > 0)
                        audit.Log(
                            caller.Email,
                            caller.Role,
                            changes.ContainsKey("role") ? "user.role.change" : "user.status.change",
                            "user",
                            user.UserId,
                            user.Email,
                            changes
                        );
                    await db.SaveChangesAsync(ct);
                    return new
                    {
                        user.UserId,
                        user.Role,
                        user.IsActive,
                    };
                }
            )
            .Accepts<UpdateUserRequest>("application/json");
        group
            .MapPut(
                "/users/{user_id}/password",
                async (
                    string user_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    IOptions<SessionConfiguration> configured,
                    CancellationToken ct
                ) =>
                {
                    var caller = await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<ResetPasswordRequest>(context, ct);
                    Required(request.NewPassword, "new_password");
                    var user = await FindUser(db, user_id, ct);
                    if (configured.Value.LocalAccountsMode == "bootstrap-only")
                        throw new ApiException(
                            403,
                            "Local password administration is disabled in bootstrap-only mode"
                        );
                    if (request.NewPassword.EnumerateRunes().Count() < 8)
                        throw new ApiException(422, "Password must be at least 8 characters");
                    user.HashedPassword = AuthService.HashPassword(request.NewPassword);
                    user.AuthProvider = "local";
                    audit.Log(
                        caller.Email,
                        caller.Role,
                        "user.password.reset",
                        "user",
                        user.UserId,
                        user.Email
                    );
                    await db.SaveChangesAsync(ct);
                    return new { status = "password updated" };
                }
            )
            .Accepts<ResetPasswordRequest>("application/json");
        group.MapDelete(
            "/users/{user_id}",
            async (
                string user_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                IOptions<SessionConfiguration> configured,
                IOptions<BootstrapOptions> bootstrap,
                CancellationToken ct
            ) =>
            {
                var caller = await auth.RequireAsync(context, "admin", ct);
                if (caller.Sub == user_id)
                    throw new ApiException(400, "Cannot delete your own account");
                var user = await FindUser(db, user_id, ct);
                if (
                    configured.Value.LocalAccountsMode == "bootstrap-only"
                    && string.Equals(
                        user.Email,
                        bootstrap.Value.AdminEmail,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    throw new ApiException(403, "The bootstrap account cannot be deleted");
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                audit.Log(
                    caller.Email,
                    caller.Role,
                    "user.delete",
                    "user",
                    user.UserId,
                    user.Email,
                    new { had_role = user.Role, auth_provider = user.AuthProvider }
                );
                await db.Set<UserGrant>().Where(x => x.UserId == user_id).ExecuteDeleteAsync(ct);
                db.Remove(user);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new { status = "deleted" };
            }
        );
        group
            .MapGet(
                "/entra/login",
                (string? mode, HttpContext context, EntraService entra, CancellationToken ct) =>
                    entra.LoginAsync(context, mode ?? "interactive", ct)
            )
            .AllowAnonymous();
        group
            .MapGet(
                "/entra/callback",
                (HttpContext context, EntraService entra, CancellationToken ct) =>
                    entra.CallbackAsync(context, ct)
            )
            .AllowAnonymous();
        group
            .MapGet(
                "/entra/session",
                (HttpContext context, EntraService entra) => entra.SessionHandoff(context)
            )
            .AllowAnonymous();
        return endpoints;
    }
}

