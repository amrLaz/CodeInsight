using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class ProjectFavoriteTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        var url = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_URL");
        if (string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(url))
        {
            Console.WriteLine(
                "Project favorite PostgreSQL/HTTP checks skipped: isolated database and API required."
            );
            return;
        }
        var info = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(
            info.Host is "localhost" or "127.0.0.1"
                && info.Database
                    is "codeinsights_checks"
                        or "codeinsights_test"
                        or "codeinsights_p1",
            "isolated local PostgreSQL required"
        );
        Assert.True(
            Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback,
            "test API is loopback"
        );

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options
        );
        var prefix = "favorite-" + Guid.NewGuid().ToString("N")[..8];
        var keys = new[]
        {
            prefix + "-a-plain",
            prefix + "-b-favorite",
            prefix + "-c-plain",
            prefix + "-d-favorite",
        };
        var emptyKey = prefix + "-e-empty";
        var deniedKey = prefix + "-z-denied";
        #pragma warning disable S2068 // Hard-coded credentials for testing purposes
        const string password = "Favorite-check-user-123";
        var userA = User(prefix + "-a", password);
        var userB = User(prefix + "-b", password);
        var projects = keys.Append(emptyKey)
            .Append(deniedKey)
            .Select(key => new Project
            {
                Id = Guid.NewGuid().ToString(),
                Key = key,
                Name = key,
            })
            .ToArray();
        db.AddRange(userA, userB);
        db.AddRange(projects);
        db.AddRange(keys.Select(key => Analysis(key)).Append(Analysis(deniedKey)));
        db.AddRange(
            new[] { userA, userB }.SelectMany(user =>
                keys.Append(emptyKey)
                    .Select(key => new UserGrant
                    {
                        Id = Guid.NewGuid().ToString(),
                        UserId = user.UserId,
                        ScopeType = "project",
                        ScopeId = key,
                    })
            )
        );
        var ciToken = AuthService.Base64Url(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
        );
        var ci = new PlatformApiToken
        {
            TokenId = Guid.NewGuid().ToString(),
            TokenName = prefix,
            TokenHash = AuthService.Sha256(ciToken),
            TokenPreview = "..." + ciToken[^4..],
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Scopes = "[\"analysis:read\"]",
        };
        db.Add(ci);
        db.AddRange(
            keys.Select(key => new TokenProjectAccess { TokenId = ci.TokenId, ProjectKey = key })
        );
        await db.SaveChangesAsync();

        using var client = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(40),
        };
        var tokenA = await Login(userA.Email);
        var tokenB = await Login(userB.Email);
        var favoriteB = FavoritePath(keys[1]);
        var favoriteD = FavoritePath(keys[3]);

        await Task.WhenAll(
            NoContent(HttpMethod.Put, favoriteB, tokenA),
            NoContent(HttpMethod.Put, favoriteB, tokenA)
        );
        db.ChangeTracker.Clear();
        Assert.True(
            await db.Set<UserProjectFavorite>().CountAsync(x => x.UserId == userA.UserId) == 1,
            "concurrent PUT creates one favorite"
        );
        await NoContent(HttpMethod.Delete, favoriteB, tokenA);
        await NoContent(HttpMethod.Delete, favoriteB, tokenA);
        db.ChangeTracker.Clear();
        Assert.True(
            !await db.Set<UserProjectFavorite>().AnyAsync(x => x.UserId == userA.UserId),
            "DELETE is idempotent"
        );
        await NoContent(HttpMethod.Put, favoriteB, tokenA);
        await NoContent(HttpMethod.Put, favoriteD, tokenA);

        await Status(HttpMethod.Put, FavoritePath(emptyKey), tokenA, HttpStatusCode.NotFound);
        await Status(HttpMethod.Put, FavoritePath(deniedKey), tokenA, HttpStatusCode.NotFound);
        await Status(HttpMethod.Put, favoriteB, ciToken, HttpStatusCode.Unauthorized);
        await Status(HttpMethod.Delete, favoriteB, ciToken, HttpStatusCode.Unauthorized);

        var summaryB = await Json(HttpMethod.Get, "/api/v1/insights/summary", tokenB);
        Assert.True(
            summaryB
                .GetProperty("projects")
                .EnumerateArray()
                .All(x => !x.GetProperty("is_favorite").GetBoolean()),
            "favorites are isolated between users"
        );
        await NoContent(HttpMethod.Put, FavoritePath(keys[0]), tokenB);
        var summaryA = await Json(HttpMethod.Get, "/api/v1/insights/summary", tokenA);
        Assert.True(
            !summaryA
                .GetProperty("projects")
                .EnumerateArray()
                .Single(x => x.GetProperty("project_key").GetString() == keys[0])
                .GetProperty("is_favorite")
                .GetBoolean(),
            "another user's favorite does not affect the caller"
        );

        using (var first = await Send(HttpMethod.Get, "/api/v1/insights/summary?limit=2", tokenA))
        {
            Assert.True(first.StatusCode == HttpStatusCode.OK, "first summary page succeeds");
            var body = await Read(first);
            var firstKeys = Keys(body);
            Assert.True(
                firstKeys.SequenceEqual(new[] { keys[1], keys[3] }),
                "favorites are the first page"
            );
            Assert.True(
                body.GetProperty("projects")
                    .EnumerateArray()
                    .All(x => x.GetProperty("is_favorite").GetBoolean()),
                "first page marks favorites"
            );
            var cursor = body.GetProperty("next_cursor").GetString();
            Assert.True(
                cursor is not null
                    && first.Headers.TryGetValues("X-Next-Cursor", out var cursors)
                    && cursors.Single() == cursor
                    && first.Headers.TryGetValues("Link", out var links)
                    && links.Single().Contains(cursor, StringComparison.Ordinal),
                "summary exposes one consistent continuation"
            );
            var second = await Json(
                HttpMethod.Get,
                $"/api/v1/insights/summary?limit=2&cursor={Uri.EscapeDataString(cursor!)}",
                tokenA
            );
            var all = firstKeys.Concat(Keys(second)).ToArray();
            Assert.True(
                all.SequenceEqual([keys[1], keys[3], keys[0], keys[2]])
                    && all.Distinct().Count() == all.Length,
                "pagination crosses the favorite boundary without gaps or duplicates"
            );
        }

        using (
            var favorites = await Send(
                HttpMethod.Get,
                "/api/v1/insights/summary?favorites_only=true&limit=1",
                tokenA
            )
        )
        {
            var body = await Read(favorites);
            Assert.True(Keys(body).SequenceEqual(new[] { keys[1] }), "favorite filter first page");
            var cursor = body.GetProperty("next_cursor").GetString()!;
            Assert.True(
                favorites
                    .Headers.GetValues("Link")
                    .Single()
                    .Contains("favorites_only=true", StringComparison.Ordinal),
                "favorite continuation preserves the filter"
            );
            var second = await Json(
                HttpMethod.Get,
                $"/api/v1/insights/summary?favorites_only=true&limit=1&cursor={Uri.EscapeDataString(cursor)}",
                tokenA
            );
            Assert.True(Keys(second).SequenceEqual(new[] { keys[3] }), "favorite filter second page");
        }
        await Status(
            HttpMethod.Get,
            "/api/v1/insights/summary?favorites_only=invalid",
            tokenA,
            HttpStatusCode.UnprocessableEntity
        );
        var ciSummary = await Json(HttpMethod.Get, "/api/v1/insights/summary", ciToken);
        Assert.True(
            ciSummary
                .GetProperty("projects")
                .EnumerateArray()
                .All(x => !x.GetProperty("is_favorite").GetBoolean()),
            "CI reads never inherit user favorites"
        );
        var ciFavorites = await Json(
            HttpMethod.Get,
            "/api/v1/insights/summary?favorites_only=true",
            ciToken
        );
        Assert.True(
            !ciFavorites.GetProperty("projects").EnumerateArray().Any(),
            "CI favorite filter is empty"
        );

        tokenA = await Login(userA.Email);
        Assert.True(
            Keys(await Json(HttpMethod.Get, "/api/v1/insights/summary?favorites_only=true", tokenA))
                .SequenceEqual([keys[1], keys[3]]),
            "favorites persist across login"
        );
        await db.Set<UserGrant>().Where(x => x.UserId == userA.UserId).ExecuteDeleteAsync();
        Assert.True(
            !Keys(await Json(HttpMethod.Get, "/api/v1/insights/summary", tokenA)).Any(),
            "lost access hides favorites"
        );
        db.AddRange(
            keys.Append(emptyKey)
                .Select(key => new UserGrant
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userA.UserId,
                    ScopeType = "project",
                    ScopeId = key,
                })
        );
        await db.SaveChangesAsync();
        Assert.True(
            Keys(await Json(HttpMethod.Get, "/api/v1/insights/summary?favorites_only=true", tokenA))
                .SequenceEqual(new[] { keys[1], keys[3] }),
            "restored access reveals retained favorites"
        );

        await CascadeChecks();
        Console.WriteLine("Project favorite PostgreSQL/HTTP checks passed.");

        async Task<string> Login(string email)
        {
            var body = await Json(
                HttpMethod.Post,
                "/api/v1/auth/login",
                null,
                new { email, password }
            );
            return body.GetProperty("token").GetString()!;
        }

        async Task CascadeChecks()
        {
            var firstUser = User(prefix + "-cascade-user", password);
            var secondUser = User(prefix + "-cascade-project", password);
            var firstProject = new Project
            {
                Id = Guid.NewGuid().ToString(),
                Key = prefix + "-cascade-a",
                Name = "Cascade A",
            };
            var secondProject = new Project
            {
                Id = Guid.NewGuid().ToString(),
                Key = prefix + "-cascade-b",
                Name = "Cascade B",
            };
            db.AddRange(firstUser, secondUser, firstProject, secondProject);
            db.AddRange(
                new UserProjectFavorite { UserId = firstUser.UserId, ProjectId = firstProject.Id },
                new UserProjectFavorite { UserId = secondUser.UserId, ProjectId = secondProject.Id }
            );
            await db.SaveChangesAsync();
            db.Remove(firstUser);
            db.Remove(secondProject);
            await db.SaveChangesAsync();
            Assert.True(
                !await db.Set<UserProjectFavorite>()
                    .AnyAsync(x => x.UserId == firstUser.UserId || x.ProjectId == secondProject.Id),
                "user and project deletes cascade favorites"
            );
        }

        async Task NoContent(HttpMethod method, string path, string token)
        {
            using var response = await Send(method, path, token);
            Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{method} {path} returns 204");
        }

        async Task Status(HttpMethod method, string path, string token, HttpStatusCode expected)
        {
            using var response = await Send(method, path, token);
            Assert.True(response.StatusCode == expected, $"{method} {path} returns {(int)expected}");
        }

        async Task<JsonElement> Json(
            HttpMethod method,
            string path,
            string? token,
            object? body = null
        )
        {
            using var response = await Send(method, path, token, body);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path} returns 200");
            return await Read(response);
        }

        async Task<HttpResponseMessage> Send(
            HttpMethod method,
            string path,
            string? token,
            object? body = null
        )
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: ApiJson.Options);
            return await client.SendAsync(request);
        }
    }

    private static PlatformUser User(string name, string password) =>
        new()
        {
            UserId = Guid.NewGuid().ToString(),
            Email = name + "@checks.local",
            FullName = name,
            Role = "regular",
            HashedPassword = AuthService.HashPassword(password),
        };

    private static AnalysisRecord Analysis(string projectKey) =>
        new()
        {
            AnalysisId = Guid.NewGuid().ToString(),
            ProjectKey = projectKey,
            CommitSha = "abcdef012345",
            Branch = "main",
            CiProvider = "local",
            ProviderRunId = Guid.NewGuid().ToString(),
            Status = "completed",
            CreatedAt = ApiJson.Iso(DateTime.UtcNow),
        };

    private static string FavoritePath(string key) =>
        "/api/v1/auth/me/favorite-projects/" + Uri.EscapeDataString(key);

    private static string[] Keys(JsonElement summary) =>
        summary
            .GetProperty("projects")
            .EnumerateArray()
            .Select(x => x.GetProperty("project_key").GetString()!)
            .ToArray();

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

}
