using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CodeInsights.Api.Auth;

public sealed class EntraService(
    AppDbContext db,
    AuthService auth,
    AuditService audit,
    IHttpClientFactory clients,
    ILogger<EntraService> logger
)
{
    private const string Cookie = "quality_hub_oidc";
    private const string CookiePath = "/api/v1/auth/entra";
    private const string SessionHandoffCookie = "quality_hub_session_handoff";
    private const string SessionHandoffPath = "/api/v1/auth";
    private static readonly string[] RequiredConfigKeys = ["tenant_id", "client_id", "client_secret", "redirect_uri"];

    public static void ValidateRedirectUri(string value)
    {
        if (
            Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (
                uri.Scheme == "https" && !string.IsNullOrEmpty(uri.Host)
                || uri.Scheme == "http"
                    && new[] { "localhost", "127.0.0.1", "::1", "[::1]" }.Contains(uri.Host)
            )
        )
            return;
        throw new ApiException(400, "Entra redirect_uri must use HTTPS except on loopback");
    }

    private async Task<Dictionary<string, JsonElement>> ConfigAsync(CancellationToken ct)
    {
        var provider = await db.Set<AuthProviderConfig>()
            .FirstOrDefaultAsync(x => x.ProviderType == "azure-ad", ct);
        if (provider is null || !provider.Enabled)
            throw new ApiException(400, "Azure AD authentication is not enabled");
        Dictionary<string, JsonElement> config;
        try
        {
            config =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(provider.Config) ?? [];
        }
        catch (JsonException)
        {
            config = [];
        }
        var missing = RequiredConfigKeys
            .Where(key =>
                !config.TryGetValue(key, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString())
            )
            .ToArray();
        if (missing.Length > 0)
            throw new ApiException(
                400,
                "Azure AD config incomplete — missing: " + string.Join(", ", missing)
            );
        ValidateRedirectUri(Text(config, "redirect_uri"));
        return config;
    }

    private static string Text(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        string fallback = ""
    ) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string RandomSecret() =>
        AuthService.Base64Url(RandomNumberGenerator.GetBytes(32));

    public async Task<object> LoginAsync(HttpContext context, string mode, CancellationToken ct)
    {
        if (mode is not ("interactive" or "silent"))
            throw new ApiException(422, "Entra login mode must be interactive or silent");
        var config = await ConfigAsync(ct);
        var state = RandomSecret();
        var browser = RandomSecret();
        var nonce = RandomSecret();
        db.Add(
            new OidcTransaction
            {
                StateHash = AuthService.Sha256(state),
                CookieHash = AuthService.Sha256(browser),
                Nonce = nonce,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                IsSilent = mode == "silent",
            }
        );
        await db.SaveChangesAsync(ct);
        context.Response.Cookies.Append(
            Cookie,
            browser,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Text(config, "redirect_uri")
                    .StartsWith("https://", StringComparison.Ordinal),
                SameSite = SameSiteMode.Lax,
                Path = CookiePath,
                MaxAge = TimeSpan.FromSeconds(600),
            }
        );
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = Text(config, "client_id"),
            ["response_type"] = "code",
            ["redirect_uri"] = Text(config, "redirect_uri"),
            ["scope"] = Text(config, "scopes", "openid profile email"),
            ["state"] = state,
            ["nonce"] = nonce,
            ["response_mode"] = "query",
        };
        if (mode == "silent")
            parameters["prompt"] = "none";
        var query = string.Join(
            "&",
            parameters.Select(x =>
                Uri.EscapeDataString(x.Key)
                + "="
                + Uri.EscapeDataString(x.Value).Replace("%20", "+")
            )
        );
        return new
        {
            auth_url = $"https://login.microsoftonline.com/{Text(config, "tenant_id")}/oauth2/v2.0/authorize?{query}",
        };
    }

    public async Task<OidcTransaction> ConsumeTransactionAsync(
        string? state,
        string? browser,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(browser))
            throw new ApiException(400, "Invalid or missing OIDC transaction");
        var hash = AuthService.Sha256(state);
        var transaction = await db.Set<OidcTransaction>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.StateHash == hash, ct);
        if (transaction is null)
            throw new ApiException(400, "Invalid or missing OIDC transaction");
        var now = DateTime.UtcNow;
        var cookieHash = AuthService.Sha256(browser);
        if (
            transaction.UsedAt != null
            || transaction.ExpiresAt <= now
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(transaction.CookieHash),
                Encoding.UTF8.GetBytes(cookieHash)
            )
        )
            throw new ApiException(
                400,
                "Expired, replayed, or browser-mismatched OIDC transaction"
            );
        var updated = await db.Set<OidcTransaction>()
            .Where(x =>
                x.StateHash == hash
                && x.CookieHash == cookieHash
                && x.UsedAt == null
                && x.ExpiresAt > now
            )
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, now), ct);
        if (updated != 1)
            throw new ApiException(400, "Expired or replayed OIDC transaction");
        transaction.UsedAt = now;
        return transaction;
    }

    public static Dictionary<string, JsonElement> VerifyIdToken(
        string token,
        IReadOnlyDictionary<string, JsonElement> config,
        string nonce,
        string jwks,
        long? now = null
    )
    {
        Dictionary<string, JsonElement> claims;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var untrusted = handler.ReadJwtToken(token);
            if (untrusted.Header.Alg != "RS256" || string.IsNullOrEmpty(untrusted.Header.Kid))
                throw new SecurityTokenException();
            var keys = new JsonWebKeySet(jwks)
                .Keys.Where(key =>
                    key.Kid == untrusted.Header.Kid
                    && key.Kty == "RSA"
                    && (string.IsNullOrEmpty(key.Use) || key.Use == "sig")
                    && (string.IsNullOrEmpty(key.Alg) || key.Alg == "RS256")
                )
                .ToArray();
            handler.ValidateToken(
                token,
                new TokenValidationParameters
                {
                    ValidateAudience = false,
                    ValidateIssuer = false,
                    ValidateLifetime = false,
                    RequireExpirationTime = false,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = keys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.Zero,
                },
                out var validated
            );
            claims =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    ((JwtSecurityToken)validated).Payload.SerializeToJson()
                ) ?? [];
        }
        catch (Exception exception)
            when (exception
                    is SecurityTokenException
                        or ArgumentException
                        or JsonException
                        or FormatException
            )
        {
            throw new ApiException(401, "Invalid Entra ID token");
        }
        var timestamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static long Number(
            IReadOnlyDictionary<string, JsonElement> values,
            string key,
            long fallback
        )
        {
            if (!values.TryGetValue(key, out var value))
                return fallback;
            if (
                long.TryParse(
                    value.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var number
                )
            )
                return number;
            throw new ApiException(401, "Invalid Entra ID token claims");
        }
        var audienceValid =
            claims.TryGetValue("aud", out var audience)
            && (
                audience.ValueKind == JsonValueKind.Array
                    ? audience
                        .EnumerateArray()
                        .Any(x =>
                            x.ValueKind == JsonValueKind.String
                            && x.GetString() == Text(config, "client_id")
                        )
                    : audience.ValueKind == JsonValueKind.String
                        && audience.GetString() == Text(config, "client_id")
            );
        if (
            Text(claims, "iss")
                != $"https://login.microsoftonline.com/{Text(config, "tenant_id")}/v2.0"
            || Text(claims, "tid") != Text(config, "tenant_id")
            || !audienceValid
            || Text(claims, "nonce") != nonce
            || Number(claims, "exp", 0) <= timestamp
            || Number(claims, "nbf", 0) > timestamp
            || Number(claims, "iat", timestamp) > timestamp + 60
            || string.IsNullOrEmpty(Text(claims, "oid"))
        )
            throw new ApiException(401, "Invalid Entra ID token claims");
        return claims;
    }

    public async Task<IResult> CallbackAsync(HttpContext context, CancellationToken ct)
    {
        string redirect;
        try
        {
            var transaction = await ConsumeTransactionAsync(
                context.Request.Query["state"],
                context.Request.Cookies[Cookie],
                ct
            );
            var error = context.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                if (
                    transaction.IsSilent
                    && error is "login_required" or "interaction_required" or "consent_required"
                )
                {
                    context.Response.Cookies.Delete(
                        Cookie,
                        new CookieOptions { Path = CookiePath }
                    );
                    return Results.Redirect(
                        "/#entra_silent=unavailable",
                        permanent: false,
                        preserveMethod: true
                    );
                }
                throw new ApiException(
                    401,
                    context.Request.Query["error_description"].FirstOrDefault() ?? error
                );
            }
            var code = context.Request.Query["code"].ToString();
            if (string.IsNullOrEmpty(code))
                throw new ApiException(400, "Missing authorization code");
            var config = await ConfigAsync(ct);
            using var client = clients.CreateClient("entra");
            client.Timeout = TimeSpan.FromSeconds(15);
            var tenant = Text(config, "tenant_id");
            using var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = Text(config, "client_id"),
                    ["client_secret"] = Text(config, "client_secret"),
                    ["redirect_uri"] = Text(config, "redirect_uri"),
                    ["code"] = code,
                    ["scope"] = Text(config, "scopes", "openid profile email"),
                }
            );
            using var tokenResponse = await client.PostAsync(
                $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                content,
                ct
            );
            tokenResponse.EnsureSuccessStatusCode();
            var jwksUrl = Text(config, "jwks_uri");
            if (string.IsNullOrEmpty(jwksUrl))
                jwksUrl = $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
            var jwks = await client.GetStringAsync(jwksUrl, ct);
            using var tokenPayload = JsonDocument.Parse(
                await tokenResponse.Content.ReadAsStringAsync(ct)
            );
            var token = tokenPayload.RootElement.TryGetProperty("id_token", out var idToken)
                ? idToken.GetString() ?? ""
                : "";
            var claims = VerifyIdToken(token, config, transaction.Nonce, jwks);
            var objectId = Text(claims, "oid");
            var name = Text(claims, "name");
            if (string.IsNullOrEmpty(name))
                name = Text(claims, "preferred_username", "Entra User");
            var email = Text(claims, "email");
            if (string.IsNullOrEmpty(email))
                email = Text(claims, "preferred_username");
            if (string.IsNullOrEmpty(email))
                email = Text(claims, "upn");
            email = email.ToLowerInvariant();
            if (string.IsNullOrEmpty(email))
                throw new ApiException(401, "Entra ID token has no usable email claim");
            var user = await db.Set<PlatformUser>()
                .FirstOrDefaultAsync(
                    x => x.EntraTenantId == tenant && x.EntraObjectId == objectId,
                    ct
                );
            var isNew = user is null;
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
                    Email = email,
                    FullName = name,
                    Role = "default",
                    AuthProvider = "entra",
                    IsActive = true,
                    EntraTenantId = tenant,
                    EntraObjectId = objectId,
                };
                db.Add(user);
            }
            else if (!user.IsActive)
                throw new ApiException(403, "This account is disabled");
            else
            {
                user.FullName = name;
                user.Email = email;
            }
            user.LastLoginAt = DateTime.UtcNow;
            audit.Log(
                email,
                user.Role,
                isNew ? "user.login.sso.first" : "user.login.sso",
                "session",
                resourceLabel: email,
                details: new { provider = "entra", tenant_id = tenant }
            );
            await db.SaveChangesAsync(ct);
            var session = await auth.SignSessionTokenAsync(user, ct);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    full_name = user.FullName,
                    email = user.Email,
                    access_role = user.Role,
                    token = session,
                }
            );
            // A URL that self-authenticates on load — even via a fragment never sent to
            // the server — reads to an automated classifier exactly like a credential-
            // exposure page (Safe Browsing flagged this exact origin as "deceptive").
            // Hand the session off through a short-lived, single-use httpOnly cookie
            // instead: the browser follows a clean redirect, then the SPA exchanges the
            // cookie for the payload via SessionHandoffAsync below.
            context.Response.Cookies.Append(
                SessionHandoffCookie,
                Convert.ToBase64String(payload).Replace('+', '-').Replace('/', '_'),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Text(config, "redirect_uri")
                        .StartsWith("https://", StringComparison.Ordinal),
                    SameSite = SameSiteMode.Lax,
                    Path = SessionHandoffPath,
                    MaxAge = TimeSpan.FromSeconds(30),
                }
            );
            redirect = "/?entra_session=ready";
        }
        catch (Exception ex)
            when (ex
                    is ApiException
                        or HttpRequestException
                        or JsonException
                        or DbUpdateException
                        or Npgsql.NpgsqlException
                        or FormatException
                        or ArgumentException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(
                "Entra authentication failed error_type={ErrorType}",
                ex.GetType().Name
            );
            redirect =
                "/#entra_error="
                + Uri.EscapeDataString(
                    ex is ApiException api
                        ? Convert.ToString(api.Detail, CultureInfo.InvariantCulture)
                            ?? "Entra ID authentication failed"
                        : "Entra ID authentication failed"
                );
        }
        context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = CookiePath });
        return Results.Redirect(redirect, permanent: false, preserveMethod: true);
    }

    /// <summary>Exchanges the handoff cookie set by CallbackAsync for the session payload. The
    /// cookie is deleted on the browser after one legitimate use, but nothing tracks
    /// consumption server-side — its 30s lifetime and HttpOnly/Secure/SameSite=Lax
    /// scoping are the actual protection, not single-use semantics.</summary>
    public IResult SessionHandoff(HttpContext context)
    {
        var value = context.Request.Cookies[SessionHandoffCookie];
        context.Response.Cookies.Delete(
            SessionHandoffCookie,
            new CookieOptions { Path = SessionHandoffPath }
        );
        if (string.IsNullOrEmpty(value))
            return Results.Problem(statusCode: 404, detail: "No pending Entra sign-in");
        try
        {
            var payload = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/')
            );
            return Results.Content(
                Encoding.UTF8.GetString(payload),
                "application/json"
            );
        }
        catch (FormatException)
        {
            return Results.Problem(statusCode: 404, detail: "No pending Entra sign-in");
        }
    }
}
