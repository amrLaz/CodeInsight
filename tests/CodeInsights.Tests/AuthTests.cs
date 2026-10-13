using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public sealed class AuthTests
{

private static async Task Denied(Func<Task> action, int status)
    {
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.StatusCode == status)
        {
            return;
        }
        throw new InvalidOperationException($"Expected HTTP {status}");
    }

    private static Dictionary<string, JsonElement> Json(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(value)
        )!;

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var nativeHash = AuthService.HashPassword("Synthetic-password-123");
        Assert.True(
            AuthService.VerifyPassword("Synthetic-password-123", nativeHash),
            "Identity PasswordHasher accepts correct password"
        );
        Assert.True(!AuthService.VerifyPassword("wrong", nativeHash), "Incorrect password denied");
        Assert.True(!AuthService.VerifyPassword("wrong", "bad-base64"), "Malformed password denied");
        Assert.True(
            new Microsoft.AspNetCore.Identity.PasswordHasher<PlatformUser>().VerifyHashedPassword(
                new PlatformUser(),
                nativeHash,
                "Synthetic-password-123"
            ) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
            "Identity password format"
        );
        var sessionOptions = Options.Create(
            new SessionOptions
            {
                SigningKey = "Synthetic-signing-key-at-least-32-bytes-only-for-tests",
            }
        );
        var bootstrapOnly = new SessionOptions { LocalAccountsMode = "bootstrap-only" };
        Assert.True(
            !bootstrapOnly.LocalLoginVisible && new SessionOptions().LocalLoginVisible,
            "bootstrap-only hides local login while development keeps it enabled"
        );
        EntraService.ValidateRedirectUri("http://localhost:8080/callback");
        EntraService.ValidateRedirectUri("http://[::1]:8080/callback");
        await Denied(
            () =>
            {
                EntraService.ValidateRedirectUri("http://example.com/callback");
                return Task.CompletedTask;
            },
            400
        );
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var publicKey = JsonWebKeyConverter.ConvertFromRSASecurityKey(
            new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = key.KeyId }
        );
        var keys = JsonSerializer.Serialize(
            new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        kid = key.KeyId,
                        use = "sig",
                        alg = "RS256",
                        n = publicKey.N,
                        e = publicKey.E,
                    },
                },
            }
        );
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var config = Json(new { tenant_id = "test-tenant", client_id = "test-client" });
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "https://login.microsoftonline.com/test-tenant/v2.0",
            ["aud"] = "test-client",
            ["tid"] = "test-tenant",
            ["oid"] = "test-user",
            ["nonce"] = "test-nonce",
            ["exp"] = now + 3600,
            ["nbf"] = now - 10,
            ["iat"] = now,
            ["email"] = "test@example.test",
        };
        string Sign(Dictionary<string, object> values) =>
            new JwtSecurityTokenHandler().WriteToken(
                new JwtSecurityToken(
                    new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)),
                    JwtPayload.Deserialize(JsonSerializer.Serialize(values))
                )
            );
        Assert.True(
            EntraService
                .VerifyIdToken(Sign(claims), config, "test-nonce", keys, now)["oid"]
                .GetString() == "test-user",
            "Valid Entra signature and claims"
        );
        foreach (
            var (name, invalid) in new (string, object)[]
            {
                ("iss", "wrong"),
                ("tid", "wrong"),
                ("aud", "wrong"),
                ("nonce", "wrong"),
                ("exp", now),
                ("nbf", now + 1),
                ("iat", now + 61),
                ("oid", ""),
            }
        )
        {
            var bad = new Dictionary<string, object>(claims) { [name] = invalid };
            await Denied(
                () =>
                {
                    EntraService.VerifyIdToken(Sign(bad), config, "test-nonce", keys, now);
                    return Task.CompletedTask;
                },
                401
            );
        }
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Console.WriteLine(
                "Auth crypto/JWT passed; PostgreSQL checks require CODEINSIGHTS_TEST_DB."
            );
            return;
        }
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection)
            .UseAsyncSeeding(
                (context, _, token) =>
                    DatabaseMigrator.SeedAsync(
                        (AppDbContext)context,
                        new BootstrapOptions { AdminPassword = "Isolated-bootstrap-123" },
                        token
                    )
            )
            .Options;
        await using var db = new AppDbContext(options);
        await new DatabaseMigrator(db).MigrateAsync();
        await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(async _ =>
                {
                    await using var parallel = new AppDbContext(options);
                    await new DatabaseMigrator(parallel).MigrateAsync();
                })
        );
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.True(
            appliedMigrations.Count(x => x.EndsWith("_InitialCreate", StringComparison.Ordinal))
                == 1
                && !(await db.Database.GetPendingMigrationsAsync()).Any(),
            "Initial migration applied once and all versioned migrations applied"
        );
        Assert.True(!db.Database.HasPendingModelChanges(), "EF model matches the migration snapshot");
        var suffix = Guid.NewGuid().ToString("N");
        var user = new PlatformUser
        {
            UserId = Guid.NewGuid().ToString(),
            Email = suffix + "@example.test",
            FullName = "Test",
            HashedPassword = nativeHash,
            Role = "regular",
        };
        var org = new Organization
        {
            Id = Guid.NewGuid().ToString(),
            Key = suffix,
            Name = "Allowed",
        };
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = "test-" + suffix,
            Name = "Allowed",
            OrganizationId = org.Id,
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        db.AddRange(user, org, project);
        await db.SaveChangesAsync();
        var auth = new AuthService(db, sessionOptions);
        var signed = await auth.SignSessionTokenAsync(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(signed);
        Assert.True(
            jwt.Issuer == "CodeInsights"
                && jwt.Audiences.Single() == "CodeInsights.Web"
                && jwt.ValidTo - jwt.ValidFrom == TimeSpan.FromMinutes(480),
            "Session is a standard JWT with configured issuer, audience and lifetime"
        );
        var expired = new JwtSecurityToken(
            "CodeInsights",
            "CodeInsights.Web",
            [new System.Security.Claims.Claim("sub", user.UserId)],
            DateTime.UtcNow.AddHours(-2),
            DateTime.UtcNow.AddHours(-1),
            new SigningCredentials(
                sessionOptions.Value.SecurityKey(),
                SecurityAlgorithms.HmacSha256
            )
        );
        await Denied(
            () =>
                auth.RequireAsync(
                    Context(new JwtSecurityTokenHandler().WriteToken(expired)),
                    "user"
                ),
            401
        );
        static DefaultHttpContext Context(string token)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = "Bearer " + token;
            return context;
        }
        var principal = await auth.RequireAsync(Context(signed), "user");
        Assert.True(
            principal.Sub == user.UserId
                && (await auth.AllowedProjectKeysAsync(principal)).Count == 0,
            "User has no implicit grants"
        );
        var allProjectsGrant = new UserGrant
        {
            Id = Guid.NewGuid().ToString(),
            UserId = user.UserId,
            ScopeType = AccessGrantScopes.AllProjects,
            ScopeId = AccessGrantScopes.AllProjectsId,
        };
        db.Add(allProjectsGrant);
        await db.SaveChangesAsync();
        var allProjectKeys = await auth.AllowedProjectKeysAsync(principal);
        Assert.True(
            allProjectKeys.Contains(project.Key)
                && allProjectKeys.Count == await db.Set<Project>().CountAsync(),
            "Explicit all-projects grant resolves every project"
        );
        db.Remove(allProjectsGrant);
        db.Add(
            new UserGrant
            {
                Id = Guid.NewGuid().ToString(),
                UserId = user.UserId,
                ScopeType = "organization",
                ScopeId = org.Id,
            }
        );
        await db.SaveChangesAsync();
        await auth.AssertProjectAccessAsync(principal, project.Key);
        await Denied(() => auth.AssertProjectAccessAsync(principal, "other-project"), 403);
        await Denied(
            () => auth.AssertProjectAccessAsync(principal, project.Key, "analysis:write"),
            403
        );
        user.Role = "default";
        await db.SaveChangesAsync();
        Assert.True(
            (await auth.RequireAsync(Context(signed))).Role == "default",
            "Current DB role overrides signed role"
        );
        user.IsActive = false;
        await db.SaveChangesAsync();
        await Denied(() => auth.RequireAsync(Context(signed), "user"), 401);
        user.IsActive = true;
        await db.SaveChangesAsync();
        await Denied(() => auth.RequireAsync(Context(signed + "bad"), "user"), 401);
        var rawCi = "ci-" + suffix;
        var tokenRow = new PlatformApiToken
        {
            TokenId = Guid.NewGuid().ToString(),
            TokenName = "test",
            TokenHash = AuthService.Sha256(rawCi),
            TokenPreview = "...test",
            Scopes = "[\"analysis:read\"]",
        };
        db.Add(tokenRow);
        db.Add(new TokenProjectAccess { TokenId = tokenRow.TokenId, ProjectKey = project.Key });
        await db.SaveChangesAsync();
        var ci = await auth.RequireAsync(Context(rawCi), "ci");
        await auth.AssertProjectAccessAsync(ci, project.Key);
        await Denied(() => auth.AssertProjectAccessAsync(ci, project.Key, "analysis:write"), 403);
        await Denied(() => auth.RequireAsync(Context(rawCi), "user"), 401);
        tokenRow.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await Denied(() => auth.RequireAsync(Context(rawCi), "ci"), 401);
        tokenRow.ExpiresAt = null;
        tokenRow.Revoked = true;
        await db.SaveChangesAsync();
        await Denied(() => auth.RequireAsync(Context(rawCi), "ci"), 401);
        var state = "state-" + suffix;
        var cookie = "cookie-" + suffix;
        db.Add(
            new OidcTransaction
            {
                StateHash = AuthService.Sha256(state),
                CookieHash = AuthService.Sha256(cookie),
                Nonce = "nonce",
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            }
        );
        await db.SaveChangesAsync();
        var successes = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(async _ =>
                {
                    await using var parallel = new AppDbContext(options);
                    var entra = new EntraService(
                        parallel,
                        new AuthService(parallel, sessionOptions),
                        new AuditService(parallel, NullLogger<AuditService>.Instance),
                        new UnusedHttpClients(),
                        NullLogger<EntraService>.Instance
                    );
                    try
                    {
                        await entra.ConsumeTransactionAsync(state, cookie);
                        return 1;
                    }
                    catch (ApiException ex) when (ex.StatusCode == 400)
                    {
                        return 0;
                    }
                })
        );
        Assert.True(successes.Sum() == 1, "OIDC transaction consumption is atomic under replay");
        var provider = await db.Set<AuthProviderConfig>()
            .FirstAsync(x => x.ProviderType == "azure-ad");
        provider.Enabled = true;
        provider.Config = JsonSerializer.Serialize(
            new
            {
                tenant_id = "test-tenant",
                client_id = "test-client",
                client_secret = "synthetic-secret",
                redirect_uri = "http://localhost/api/v1/auth/entra/callback",
            }
        );
        await db.SaveChangesAsync();
        using var http = new EntraHttpSubstitute { Jwks = keys };
        var service = new EntraService(
            db,
            auth,
            new AuditService(db, NullLogger<AuditService>.Instance),
            http,
            NullLogger<EntraService>.Instance
        );
        await Denied(() => service.LoginAsync(new DefaultHttpContext(), "invalid", default), 422);
        foreach (
            var expectedError in new[]
            {
                "login_required",
                "interaction_required",
                "consent_required",
            }
        )
        {
            var silentContext = new DefaultHttpContext();
            var silentStart = Json(await service.LoginAsync(silentContext, "silent", default));
            var silentAuthorization = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                new Uri(silentStart["auth_url"].GetString()!).Query
            );
            Assert.True(
                silentAuthorization["prompt"] == "none",
                "Silent Entra login explicitly uses prompt=none"
            );
            var silentStateHash = AuthService.Sha256(silentAuthorization["state"].ToString());
            Assert.True(
                await db.Set<OidcTransaction>()
                    .AnyAsync(x => x.StateHash == silentStateHash && x.IsSilent),
                "OIDC transaction persists silent mode"
            );
            var silentCallbackContext = new DefaultHttpContext();
            silentCallbackContext.Request.QueryString = new QueryString(
                $"?error={expectedError}&error_description=secret-description&state="
                    + silentAuthorization["state"]
            );
            silentCallbackContext.Request.Headers.Cookie = silentContext
                .Response.Headers.SetCookie.ToString()
                .Split(';')[0];
            var silentCallback = (Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)
                await service.CallbackAsync(silentCallbackContext, default);
            Assert.True(
                silentCallback.Url == "/#entra_silent=unavailable",
                $"Silent {expectedError} returns quietly without exposing the provider description"
            );
        }

        var loginContext = new DefaultHttpContext();
        var start = Json(await service.LoginAsync(loginContext, "interactive", default));
        var authorization = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            new Uri(start["auth_url"].GetString()!).Query
        );
        Assert.True(
            !authorization.ContainsKey("prompt"),
            "Interactive Entra login does not force prompt=none"
        );
        var browserCookie = loginContext.Response.Headers.SetCookie.ToString().Split(';')[0];
        Assert.True(
            loginContext
                .Response.Headers.SetCookie.ToString()
                .Contains("httponly", StringComparison.OrdinalIgnoreCase),
            "OIDC browser secret is HTTP-only"
        );
        var ssoClaims = new Dictionary<string, object>(claims)
        {
            ["nonce"] = authorization["nonce"].ToString(),
            ["oid"] = "oid-" + suffix,
            ["email"] = "entra-" + suffix + "@example.test",
        };
        http.Token = Sign(ssoClaims);
        var callbackContext = new DefaultHttpContext();
        callbackContext.Request.QueryString = new QueryString(
            "?code=synthetic-code&state=" + authorization["state"]
        );
        callbackContext.Request.Headers.Cookie = browserCookie;
        var callback = (Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)
            await service.CallbackAsync(callbackContext, default);
        Assert.True(
            callback.Url == "/?entra_session=ready",
            "Simulated Entra callback redirects to a clean URL — the payload never appears there"
        );
        Assert.True(
            callback.PreserveMethod && !callback.Permanent && http.Calls == 2,
            "OIDC exchange and JWKS calls preserve redirect status"
        );
        var handoffCookie = callbackContext.Response.Headers.SetCookie.First(header =>
            header!.StartsWith("quality_hub_session_handoff=", StringComparison.Ordinal)
        )!;
        Assert.True(
            handoffCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase),
            "Session handoff cookie is HTTP-only"
        );
        using var resultServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        var handoffContext = new DefaultHttpContext { RequestServices = resultServices };
        handoffContext.Request.Headers.Cookie = handoffCookie.Split(';')[0];
        handoffContext.Response.Body = new MemoryStream();
        await service.SessionHandoff(handoffContext).ExecuteAsync(handoffContext);
        handoffContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var resultPayload = await JsonDocument.ParseAsync(handoffContext.Response.Body);
        var missingHandoffContext = new DefaultHttpContext { RequestServices = resultServices };
        missingHandoffContext.Response.Body = new MemoryStream();
        await service.SessionHandoff(missingHandoffContext).ExecuteAsync(missingHandoffContext);
        Assert.True(
            missingHandoffContext.Response.StatusCode == 404,
            "Session handoff without the cookie reports no pending sign-in"
        );
        var ssoPrincipal = await auth.RequireAsync(
            Context(resultPayload.RootElement.GetProperty("token").GetString()!),
            "user"
        );
        Assert.True(
            ssoPrincipal.Role == "default"
                && (await auth.AllowedProjectKeysAsync(ssoPrincipal)).Count == 0,
            "Provisioned Entra identity has no implicit grants"
        );
        var replayContext = new DefaultHttpContext();
        replayContext.Request.QueryString = callbackContext.Request.QueryString;
        replayContext.Request.Headers.Cookie = browserCookie;
        var replay = (Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult)
            await service.CallbackAsync(replayContext, default);
        Assert.True(
            replay.Url!.StartsWith("/#entra_error=", StringComparison.Ordinal) && http.Calls == 2,
            "Replay fails before outbound exchange"
        );
        provider.Enabled = false;
        provider.Config = "{}";
        await db.SaveChangesAsync();
        Console.WriteLine(
            "Identity PasswordHasher/JWT, migrations/concurrency, grants, sessions, CI expiry/revocation and OIDC replay checks passed."
        );
    }

    private sealed class UnusedHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No HTTP calls expected");
    }

    private sealed class EntraHttpSubstitute : HttpMessageHandler, IHttpClientFactory
    {
        public string Token { get; set; } = "";
        public string Jwks { get; set; } = "";
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Assert.True(
                    request.RequestUri!.AbsoluteUri
                        == "https://login.microsoftonline.com/test-tenant/oauth2/v2.0/token"
                        && body.Contains("code=synthetic-code", StringComparison.Ordinal)
                        && body.Contains("grant_type=authorization_code", StringComparison.Ordinal),
                    "OIDC code exchange contract"
                );
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { id_token = Token })),
                };
            }
            Assert.True(
                request.RequestUri!.AbsoluteUri
                    == "https://login.microsoftonline.com/test-tenant/discovery/v2.0/keys",
                "OIDC tenant JWKS endpoint"
            );
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Jwks),
            };
        }
    }
}
