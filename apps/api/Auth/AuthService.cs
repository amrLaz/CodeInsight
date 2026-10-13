using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Auth;

public sealed record Principal(
    string Kind,
    string Sub = "",
    string Email = "",
    string FullName = "",
    string Role = "",
    string TokenId = "",
    string[]? Scopes = null,
    string[]? DirectoryGroupIds = null,
    bool DirectoryAccessAvailable = true
)
{
    public bool IsAdmin => Kind == "user" && Role == "admin";

    /// <summary>A user identity proven by a personal access token instead of an interactive session.</summary>
    public bool IsPersonalToken => Kind == "user" && TokenId.Length > 0;
}

/// <summary>Scopes a user may grant to their own access token. Administration is never one of them.</summary>
public static class PersonalTokenScopes
{
    public const string Read = "analysis:read";
    public const string CollaborationWrite = "collaboration:write";
    public static readonly string[] Allowed = [Read, CollaborationWrite];
}

/// <summary>Internal grant values persisted when an administrator explicitly assigns every project.</summary>
public static class AccessGrantScopes
{
    public const string AllProjects = "all";
    public const string AllProjectsId = "*";
}

public sealed class AuthService(
    AppDbContext db,
    Microsoft.Extensions.Options.IOptions<SessionOptions> sessionOptions,
    IEntraAccessService entraAccess
)
{
    internal AuthService(
        AppDbContext db,
        Microsoft.Extensions.Options.IOptions<SessionOptions> sessionOptions
    )
        : this(db, sessionOptions, new DirectAccess()) { }

    private sealed class DirectAccess : IEntraAccessService
    {
        public Task<EntraEffectiveAccess> ResolveAsync(
            PlatformUser user,
            CancellationToken ct = default
        ) => Task.FromResult(new EntraEffectiveAccess(user.Role, [], true));
    }

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static readonly Microsoft.AspNetCore.Identity.PasswordHasher<PlatformUser> PasswordHasher =
        new();

    public static string HashPassword(string password) =>
        PasswordHasher.HashPassword(new PlatformUser(), password);

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            return PasswordHasher.VerifyHashedPassword(new PlatformUser(), stored, password)
                != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Task<string> SignSessionTokenAsync(PlatformUser user, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var settings = sessionOptions.Value;
        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new System.Security.Claims.Claim("sub", user.UserId),
            new System.Security.Claims.Claim("email", user.Email),
            new System.Security.Claims.Claim("name", user.FullName),
            new System.Security.Claims.Claim("role", user.Role),
            new System.Security.Claims.Claim("jti", Guid.NewGuid().ToString()),
        };
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            now,
            now.AddMinutes(settings.SessionMinutes),
            new Microsoft.IdentityModel.Tokens.SigningCredentials(
                settings.SecurityKey(),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256
            )
        );
        return Task.FromResult(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token)
        );
    }

    private async Task<Principal?> UserAsync(string token, CancellationToken ct)
    {
        try
        {
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler
            {
                MapInboundClaims = false,
            };
            var claims = handler.ValidateToken(
                token,
                sessionOptions.Value.ValidationParameters(),
                out _
            );
            var sub = claims.FindFirst("sub")?.Value;
            var user = await db.Set<PlatformUser>()
                .FirstOrDefaultAsync(x => x.UserId == sub && x.IsActive, ct);
            return user is null ? null : await UserPrincipalAsync(user, ct: ct);
        }
        catch (Exception exception)
            when (exception
                    is Microsoft.IdentityModel.Tokens.SecurityTokenException
                        or ArgumentException
            )
        {
            return null;
        }
    }

    private async Task<Principal?> OpaqueAsync(string token, CancellationToken ct)
    {
        var hash = Sha256(token);
        var row = await db.Set<PlatformApiToken>()
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.IsCurrent && !x.Revoked, ct);
        if (row is null || row.ExpiresAt <= DateTime.UtcNow)
            return null;
        string[] scopes;
        try
        {
            scopes = JsonSerializer.Deserialize<string[]>(row.Scopes) ?? [];
        }
        catch (JsonException)
        {
            scopes = [];
        }
        if (row.OwnerUserId is null)
            return new Principal("ci", Role: "ci", TokenId: row.TokenId, Scopes: scopes);
        // A personal access token carries the identity of its owner, so every grant, filter and
        // refusal applied to that user applies unchanged; it never widens their access.
        var owner = await db.Set<PlatformUser>()
            .FirstOrDefaultAsync(x => x.UserId == row.OwnerUserId && x.IsActive, ct);
        return owner is null ? null : await UserPrincipalAsync(owner, row.TokenId, scopes, ct);
    }

    private async Task<Principal> UserPrincipalAsync(
        PlatformUser user,
        string tokenId = "",
        string[]? scopes = null,
        CancellationToken ct = default
    )
    {
        var effective = await entraAccess.ResolveAsync(user, ct);
        return new Principal(
            "user",
            user.UserId,
            user.Email,
            user.FullName,
            effective.Role,
            tokenId,
            scopes,
            effective.GroupIds,
            effective.DirectoryAccessAvailable
        );
    }

    public async Task<Principal?> OptionalAsync(
        HttpContext context,
        CancellationToken ct = default
    ) =>
        context.Request.Headers.ContainsKey("Authorization")
            ? await RequireAsync(context, ct: ct)
            : null;

    public async Task<Principal> RequireAsync(
        HttpContext context,
        string kind = "platform",
        CancellationToken ct = default
    )
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (
            !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authorization[7..])
        )
            throw new ApiException(401, "Authentication required");
        var token = authorization[7..].Trim();
        var principal = kind == "ci" ? await OpaqueAsync(token, ct) : await UserAsync(token, ct);
        // Administration keeps requiring an interactive session: a personal access token is a
        // convenience for tooling, never a way to administer the platform unattended.
        if (principal is null && kind is "platform" or "user")
            principal = await OpaqueAsync(token, ct);
        if (kind == "user" && principal?.Kind != "user")
            principal = null;
        if (principal is null)
            throw new ApiException(
                401,
                kind switch
                {
                    "ci" => "Invalid, expired, or revoked platform token",
                    "platform" => "Invalid, expired, revoked, or inactive credential",
                    _ => "Invalid, expired, or inactive session",
                }
            );
        if (kind == "admin" && (!principal.IsAdmin || principal.IsPersonalToken))
            throw new ApiException(403, "This action requires one of: admin");
        AssertPersonalTokenMethod(principal, context.Request.Method);
        return principal;
    }

    /// <summary>Personal access tokens read; they only write when explicitly granted the collaboration scope.</summary>
    public static void AssertPersonalTokenMethod(Principal principal, string method)
    {
        if (
            !principal.IsPersonalToken
            || HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || (principal.Scopes ?? []).Contains(PersonalTokenScopes.CollaborationWrite)
        )
            return;
        throw new ApiException(403, "Personal access token scope does not allow this action");
    }

    private static string? Scope(string permission) =>
        permission switch
        {
            "read" => "analysis:read",
            "analysis:write" => permission,
            "integration:write" => permission,
            _ => null,
        };

    public void AssertCiPermission(Principal principal, string permission = "read")
    {
        if (
            principal.Kind == "ci"
            && (Scope(permission) is not { } scope || !(principal.Scopes ?? []).Contains(scope))
        )
            throw new ApiException(403, "Credential scope does not allow this action");
    }

    public async Task<HashSet<string>> AllowedProjectKeysAsync(
        Principal principal,
        string permission = "read",
        CancellationToken ct = default
    )
    {
        if (principal.IsAdmin)
            return (await db.Set<Project>().Select(x => x.Key).ToListAsync(ct)).ToHashSet();
        if (principal.Kind == "ci")
        {
            if (Scope(permission) is not { } scope || !(principal.Scopes ?? []).Contains(scope))
                return [];
            return (
                await db.Set<TokenProjectAccess>()
                    .Where(x => x.TokenId == principal.TokenId)
                    .Select(x => x.ProjectKey)
                    .ToListAsync(ct)
            ).ToHashSet();
        }
        if (principal.Kind != "user")
            return [];
        var grants = await db.Set<UserGrant>()
            .Where(x => x.UserId == principal.Sub)
            .ToListAsync(ct);
        if (principal.DirectoryGroupIds is { Length: > 0 } groupIds)
        {
            grants.AddRange(
                (
                    await db.Set<EntraGroupGrant>()
                        .Where(x => groupIds.Contains(x.GroupId))
                        .ToListAsync(ct)
                ).Select(x => new UserGrant { ScopeType = x.ScopeType, ScopeId = x.ScopeId })
            );
        }
        if (
            grants.Any(x =>
                x.ScopeType == AccessGrantScopes.AllProjects
                && x.ScopeId == AccessGrantScopes.AllProjectsId
            )
        )
            return (await db.Set<Project>().Select(x => x.Key).ToListAsync(ct)).ToHashSet();
        var keys = grants.Where(x => x.ScopeType == "project").Select(x => x.ScopeId).ToHashSet();
        var organizations = grants
            .Where(x => x.ScopeType == "organization")
            .Select(x => x.ScopeId)
            .ToArray();
        keys.UnionWith(
            await db.Set<Project>()
                .Where(x => x.OrganizationId != null && organizations.Contains(x.OrganizationId))
                .Select(x => x.Key)
                .ToListAsync(ct)
        );
        return keys;
    }

    public async Task AssertProjectAccessAsync(
        Principal principal,
        string key,
        string permission = "read",
        CancellationToken ct = default
    )
    {
        if (!(await AllowedProjectKeysAsync(principal, permission, ct)).Contains(key))
            throw new ApiException(403, "Project access denied");
        AssertCiPermission(principal, permission);
        if (principal.Kind == "user" && permission != "read" && !principal.IsAdmin)
            throw new ApiException(403, "Administrator role required");
    }
}
