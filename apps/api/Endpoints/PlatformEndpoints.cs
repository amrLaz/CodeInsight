using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class PlatformEndpoints
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["instance_name"] = "CodeInsights",
        ["report_max_size_mb"] = "450",
        ["retention_keep_days"] = "90",
        ["retention_keep_failed_days"] = "30",
        ["retention_max_per_branch"] = "50",
        ["retention_auto_cleanup"] = "true",
        ["retention_protected_branches"] = "master,main,develop",
        ["audit_log_retention_days"] = "365",
    };
    private static readonly string[] AllowedScopes =
    [
        "analysis:read",
        "analysis:write",
        "integration:write",
        "project:register",
    ];

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/platform").RequireAuthorization();
        group
            .MapPost(
                "/token/generate",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    CancellationToken ct
                ) =>
                {
                    var principal = await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<TokenGenerateRequest>(context, ct);
                    if (request.RequireCurrentToken)
                    {
                        var current = context
                            .Request.Headers["X-Current-Platform-Token"]
                            .ToString();
                        if (current.Length == 0)
                            throw new ApiException(401, "Current token is required to rotate");
                        var hash = Hash(current);
                        if (
                            !await db.Set<PlatformApiToken>()
                                .AnyAsync(t => t.TokenHash == hash && t.IsCurrent && !t.Revoked, ct)
                        )
                            throw new ApiException(401, "Invalid current token");
                    }
                    if (request.ProjectKeys.Length == 0)
                        throw new ApiException(
                            422,
                            "At least one project must be assigned to a CI token"
                        );
                    if (request.Scopes.Length == 0 || request.Scopes.Except(AllowedScopes).Any())
                        throw new ApiException(422, "Invalid CI token scope");
                    await ValidateProjects(
                        db,
                        request.ProjectKeys,
                        "Unknown project in CI token assignment",
                        ct
                    );
                    var plaintext = Convert
                        .ToBase64String(RandomNumberGenerator.GetBytes(32))
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_');
                    var now = DateTime.UtcNow;
                    var token = new PlatformApiToken
                    {
                        TokenId = Guid.NewGuid().ToString(),
                        TokenName =
                            request.TokenName.Length == 0 ? "CI/CD token" : request.TokenName,
                        TokenHash = Hash(plaintext),
                        TokenPreview = "..." + plaintext[^4..],
                        CreatedAt = now,
                        ExpiresAt = request.TtlDays is null
                            ? null
                            : now.AddDays(request.TtlDays.Value),
                        IsCurrent = true,
                        Revoked = false,
                        Scopes = JsonSerializer.Serialize(request.Scopes.Distinct().Order()),
                    };
                    db.Add(token);
                    foreach (var key in request.ProjectKeys.Distinct().Order())
                        db.Add(
                            new TokenProjectAccess { TokenId = token.TokenId, ProjectKey = key }
                        );
                    audit.Log(
                        principal.Email,
                        principal.Role,
                        "token.generate",
                        "api_token",
                        token.TokenId,
                        token.TokenName,
                        new
                        {
                            preview = token.TokenPreview,
                            expires_at = token.ExpiresAt is null
                                ? null
                                : ApiJson.Iso(token.ExpiresAt.Value),
                            project_keys = request.ProjectKeys.Distinct().Order(),
                            scopes = request.Scopes.Distinct().Order(),
                        }
                    );
                    await db.SaveChangesAsync(ct);
                    return Results.Json(
                        new
                        {
                            token = plaintext,
                            generated_at = ApiJson.Iso(now),
                            expires_at = token.ExpiresAt is null
                                ? null
                                : ApiJson.Iso(token.ExpiresAt.Value),
                        },
                        ApiJson.Options
                    );
                }
            )
            .Accepts<TokenGenerateRequest>("application/json");
        group.MapPost(
            "/token/{token_id}/revoke",
            async (
                string token_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                CancellationToken ct
            ) =>
            {
                var principal = await auth.RequireAsync(context, "admin", ct);
                var token =
                    await db.Set<PlatformApiToken>().FindAsync([token_id], ct)
                    ?? throw new ApiException(404, "Token not found");
                token.Revoked = true;
                token.IsCurrent = false;
                audit.Log(
                    principal.Email,
                    principal.Role,
                    "token.revoke",
                    "api_token",
                    token.TokenId,
                    token.TokenName
                );
                await db.SaveChangesAsync(ct);
                return Results.Json(new { token_id, revoked = true }, ApiJson.Options);
            }
        );
        group
            .MapPut(
                "/token/{token_id}/access",
                async (
                    string token_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    CancellationToken ct
                ) =>
                {
                    var principal = await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<TokenAccessRequest>(context, ct);
                    var token =
                        await db.Set<PlatformApiToken>().FindAsync([token_id], ct)
                        ?? throw new ApiException(404, "Token not found");
                    if (request.Scopes.Except(AllowedScopes).Any())
                        throw new ApiException(422, "Unknown project or invalid CI token scope");
                    await ValidateProjects(
                        db,
                        request.ProjectKeys,
                        "Unknown project or invalid CI token scope",
                        ct
                    );
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    await db.Set<TokenProjectAccess>()
                        .Where(x => x.TokenId == token_id)
                        .ExecuteDeleteAsync(ct);
                    foreach (var key in request.ProjectKeys.Distinct().Order())
                        db.Add(new TokenProjectAccess { TokenId = token_id, ProjectKey = key });
                    token.Scopes = JsonSerializer.Serialize(request.Scopes.Distinct().Order());
                    var result = new
                    {
                        token_id,
                        project_keys = request.ProjectKeys.Distinct().Order(),
                        scopes = request.Scopes.Distinct().Order(),
                    };
                    audit.Log(
                        principal.Email,
                        principal.Role,
                        "token.access.replace",
                        "api_token",
                        token_id,
                        token.TokenName,
                        new { result.project_keys, result.scopes }
                    );
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Results.Json(result, ApiJson.Options);
                }
            )
            .Accepts<TokenAccessRequest>("application/json");
        group.MapGet(
            "/token/status",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var rows = await db.Set<PlatformApiToken>()
                    .Where(t => t.IsCurrent && !t.Revoked)
                    .ToListAsync(ct);
                if (rows.Count == 0)
                    return Results.Json(
                        new
                        {
                            configured = false,
                            expired = false,
                            expires_at = (string?)null,
                        },
                        ApiJson.Options
                    );
                var valid = rows.Where(t => t.ExpiresAt is null || t.ExpiresAt >= DateTime.UtcNow)
                    .ToList();
                var expiration = valid
                    .Where(t => t.ExpiresAt is not null)
                    .Select(t => t.ExpiresAt)
                    .Order()
                    .FirstOrDefault();
                return Results.Json(
                    new
                    {
                        configured = true,
                        expired = valid.Count == 0,
                        expires_at = expiration is null ? null : ApiJson.Iso(expiration.Value),
                        active_token_count = valid.Count,
                    },
                    ApiJson.Options
                );
            }
        );
        group.MapGet(
            "/token/history",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var rows = await db.Set<PlatformApiToken>()
                    .OrderByDescending(t => t.CreatedAt)
                    .ToListAsync(ct);
                var projects = await db.Set<TokenProjectAccess>().ToListAsync(ct);
                return Results.Json(
                    rows.Select(t => new
                    {
                        t.TokenId,
                        t.TokenName,
                        t.TokenPreview,
                        created_at = ApiJson.Iso(t.CreatedAt),
                        expires_at = t.ExpiresAt is null ? "" : ApiJson.Iso(t.ExpiresAt.Value),
                        t.IsCurrent,
                        t.Revoked,
                        scopes = JsonSerializer.Deserialize<string[]>(t.Scopes),
                        project_keys = projects
                            .Where(p => p.TokenId == t.TokenId)
                            .Select(p => p.ProjectKey)
                            .Order(),
                    }),
                    ApiJson.Options
                );
            }
        );
        group.MapGet(
            "/settings",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return Results.Json(await Settings(db, ct), ApiJson.Options);
            }
        );
        group
            .MapPut(
                "/settings",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    AuditService audit,
                    RetentionService retention,
                    CancellationToken ct
                ) =>
                {
                    var principal = await auth.RequireAsync(context, "admin", ct);
                    var updates = await ApiJson.ReadAsync<JsonObject>(context, ct);
                    var accepted = new Dictionary<string, string>();
                    foreach (var (key, value) in updates)
                    {
                        if (!Defaults.ContainsKey(key) || value is null)
                            continue;
                        if (
                            value is not JsonValue scalar
                            || !scalar.TryGetValue<string>(out var text)
                        )
                            throw new ApiException(
                                422,
                                new[]
                                {
                                    new
                                    {
                                        type = "string_type",
                                        loc = new[] { "body", key },
                                        msg = "Input should be a valid string",
                                    },
                                }
                            );
                        accepted[key] = text;
                    }
                    foreach (var (key, value) in accepted)
                        await retention.SetSettingAsync(key, value, ct);
                    if (accepted.Count > 0)
                        audit.Log(
                            principal.Email,
                            principal.Role,
                            "settings.update",
                            "platform_settings",
                            details: accepted
                        );
                    await db.SaveChangesAsync(ct);
                    return Results.Json(await Settings(db, ct), ApiJson.Options);
                }
            )
            .Accepts<JsonObject>("application/json");
        group.MapPost(
            "/cleanup",
            async (
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                AuditService audit,
                RetentionService retention,
                CancellationToken ct
            ) =>
            {
                var principal = await auth.RequireAsync(context, "admin", ct);
                var result = await retention.RunAsync(ct);
                audit.Log(
                    principal.Email,
                    principal.Role,
                    "data.cleanup",
                    "analysis_records",
                    details: new { result.DeletedAnalyses }
                );
                await db.SaveChangesAsync(ct);
                return Results.Json(result, ApiJson.Options);
            }
        );
        group.MapGet(
            "/cleanup/status",
            async (
                HttpContext context,
                AuthService auth,
                RetentionService retention,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return Results.Json(
                    new
                    {
                        last_cleanup_at = await retention.GetSettingAsync(
                            "last_cleanup_at",
                            "",
                            ct
                        ),
                        last_cleanup_deleted = int.Parse(
                            (await retention.GetSettingAsync("last_cleanup_deleted", "0", ct)) is ""
                                ? "0"
                                : await retention.GetSettingAsync("last_cleanup_deleted", "0", ct)
                        ),
                    },
                    ApiJson.Options
                );
            }
        );
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task ValidateProjects(
        AppDbContext db,
        string[] keys,
        string message,
        CancellationToken ct
    )
    {
        if (
            await db.Set<Project>().CountAsync(p => keys.Contains(p.Key), ct)
            != keys.Distinct().Count()
        )
            throw new ApiException(422, message);
    }

    private static async Task<Dictionary<string, string>> Settings(
        AppDbContext db,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, string>(Defaults);
        foreach (
            var row in await db.Set<PlatformSetting>()
                .Where(s => Defaults.Keys.Contains(s.Key))
                .ToListAsync(ct)
        )
            result[row.Key] = row.Value;
        return result;
    }
}

public sealed class TokenGenerateRequest
{
    [Required(AllowEmptyStrings = true)]
    public string TokenName { get; set; } = null!;

    [Range(1, 3650)]
    public int? TtlDays { get; set; }
    public bool RequireCurrentToken { get; set; }

    [Required]
    public string[] ProjectKeys { get; set; } = [];

    [Required]
    public string[] Scopes { get; set; } = ["analysis:read", "analysis:write", "integration:write"];
}

public sealed class TokenAccessRequest
{
    [Required, MinLength(1)]
    public string[] ProjectKeys { get; set; } = null!;

    [Required, MinLength(1)]
    public string[] Scopes { get; set; } = null!;
}
