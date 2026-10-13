using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Personal access tokens and repository resolution: the two credentials-facing additions used by
/// the VS Code extension. They must never widen what their owner is allowed to do.
/// </summary>
public sealed class PersonalTokenTests
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

    private static DefaultHttpContext Context(string token, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.Authorization = "Bearer " + token;
        return context;
    }

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        // Clone URLs of one repository, as produced by Azure Repos and by git remotes.
        var canonical = RepositoryUrls.Identity("https://dev.azure.com/up-group/Up.France.ODI/_git/App");
        Assert.True(canonical == "dev.azure.com/up-group/up.france.odi/_git/app", "Canonical HTTPS identity");
        foreach (
            var variant in new[]
            {
                "https://up-group@dev.azure.com/up-group/Up.France.ODI/_git/App",
                "https://dev.azure.com/up-group/Up.France.ODI/_git/App.git",
                "https://dev.azure.com/up-group/Up.France.ODI/_git/App/",
                "git@ssh.dev.azure.com:v3/up-group/Up.France.ODI/App",
                "ssh://git@ssh.dev.azure.com/v3/up-group/Up.France.ODI/App",
            }
        )
            Assert.True(RepositoryUrls.Identity(variant) == canonical, $"Identity of {variant}");
        Assert.True(
            RepositoryUrls.Identity("https://dev.azure.com/up-group/Up.France.ODI/_git/Other") != canonical,
            "Another repository has another identity"
        );
        Assert.True(RepositoryUrls.Identity("") == "" && RepositoryUrls.Identity(null) == "", "Empty identity");
        Assert.True(RepositoryUrls.Identity("not a url at all") == "", "Unparsable repository URL yields no identity");
        Assert.True(
            RepositoryUrls.Identity("git@github.com:up-group/app.git") == "github.com/up-group/app",
            "GitHub SSH identity"
        );

        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Console.WriteLine(
                "Repository identity passed; personal access token checks require CODEINSIGHTS_TEST_DB."
            );
            return;
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using var db = new AppDbContext(options);
        await new DatabaseMigrator(db).MigrateAsync();
        var sessionOptions = Options.Create(
            new SessionOptions { SigningKey = "Synthetic-signing-key-at-least-32-bytes-only-for-tests" }
        );
        var auth = new AuthService(db, sessionOptions);

        var suffix = Guid.NewGuid().ToString("N");
        var owner = new PlatformUser
        {
            UserId = Guid.NewGuid().ToString(),
            Email = "pat-" + suffix + "@example.test",
            FullName = "Token owner",
            Role = "regular",
        };
        var organization = new Organization
        {
            Id = Guid.NewGuid().ToString(),
            Key = "pat-" + suffix,
            Name = "Token owner organization",
        };
        var project = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = "pat-" + suffix,
            Name = "Token owner project",
            OrganizationId = organization.Id,
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        var otherProject = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = "pat-other-" + suffix,
            Name = "Foreign project",
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        db.AddRange(owner, organization, project, otherProject);
        db.Add(
            new UserGrant
            {
                Id = Guid.NewGuid().ToString(),
                UserId = owner.UserId,
                ScopeType = "organization",
                ScopeId = organization.Id,
            }
        );
        db.Add(
            new ProjectIntegration
            {
                Id = Guid.NewGuid().ToString(),
                ProjectKey = project.Key,
                RepoProvider = "azure-devops",
                RepoUrl = "https://dev.azure.com/up-group/Up.France.ODI/_git/App",
            }
        );
        await db.SaveChangesAsync();

        string Issue(string plaintext, string[] scopes, DateTime expiresAt, string? ownerId, bool revoked = false)
        {
            db.Add(
                new PlatformApiToken
                {
                    TokenId = Guid.NewGuid().ToString(),
                    TokenName = "check",
                    TokenHash = AuthService.Sha256(plaintext),
                    TokenPreview = "..." + plaintext[^4..],
                    ExpiresAt = expiresAt,
                    IsCurrent = !revoked,
                    Revoked = revoked,
                    Scopes = JsonSerializer.Serialize(scopes),
                    OwnerUserId = ownerId,
                }
            );
            return plaintext;
        }

        var readToken = Issue("pat-read-" + suffix, [PersonalTokenScopes.Read], DateTime.UtcNow.AddDays(30), owner.UserId);
        var writeToken = Issue(
            "pat-write-" + suffix,
            [PersonalTokenScopes.Read, PersonalTokenScopes.CollaborationWrite],
            DateTime.UtcNow.AddDays(30),
            owner.UserId
        );
        var expiredToken = Issue("pat-expired-" + suffix, [PersonalTokenScopes.Read], DateTime.UtcNow.AddMinutes(-1), owner.UserId);
        var revokedToken = Issue("pat-revoked-" + suffix, [PersonalTokenScopes.Read], DateTime.UtcNow.AddDays(30), owner.UserId, revoked: true);
        var ciToken = Issue("pat-ci-" + suffix, ["analysis:read"], DateTime.UtcNow.AddDays(30), null);
        await db.SaveChangesAsync();

        // The token proves the owner's identity, with the owner's grants and nothing more.
        var principal = await auth.RequireAsync(Context(readToken), "user");
        Assert.True(principal.Kind == "user" && principal.Sub == owner.UserId, "Personal token authenticates its owner");
        Assert.True(principal.IsPersonalToken, "Personal token is distinguishable from a session");
        Assert.True(!principal.IsAdmin, "A regular owner stays regular");
        var allowed = await auth.AllowedProjectKeysAsync(principal);
        Assert.True(allowed.Contains(project.Key) && !allowed.Contains(otherProject.Key), "Personal token sees only the owner's projects");
        await auth.AssertProjectAccessAsync(principal, project.Key);
        await Denied(() => auth.AssertProjectAccessAsync(principal, otherProject.Key), 403);

        // Read-only by default, and never an administration credential.
        await Denied(() => auth.RequireAsync(Context(readToken, "POST"), "user"), 403);
        await Denied(() => auth.RequireAsync(Context(readToken, "PUT"), "platform"), 403);
        await Denied(() => auth.RequireAsync(Context(readToken), "admin"), 401);
        var writer = await auth.RequireAsync(Context(writeToken, "POST"), "user");
        Assert.True(writer.Sub == owner.UserId, "Collaboration scope allows a write on behalf of its owner");

        // Expired, revoked and disabled identities are refused.
        await Denied(() => auth.RequireAsync(Context(expiredToken), "user"), 401);
        await Denied(() => auth.RequireAsync(Context(revokedToken), "user"), 401);
        owner.IsActive = false;
        await db.SaveChangesAsync();
        await Denied(() => auth.RequireAsync(Context(readToken), "user"), 401);
        owner.IsActive = true;
        await db.SaveChangesAsync();

        // A CI token is not a user and cannot borrow a user identity.
        var ci = await auth.RequireAsync(Context(ciToken), "ci");
        Assert.True(ci.Kind == "ci" && !ci.IsPersonalToken, "CI tokens keep their own kind");
        await Denied(() => auth.RequireAsync(Context(ciToken), "user"), 401);
        var ciPlatform = await auth.RequireAsync(Context(ciToken, "POST"), "platform");
        Assert.True(ciPlatform.Kind == "ci", "The read-only rule does not apply to CI tokens");

        // An administrator's personal token still cannot administer.
        owner.Role = "admin";
        await db.SaveChangesAsync();
        var administrator = await auth.RequireAsync(Context(readToken), "user");
        Assert.True(administrator.IsAdmin, "The token carries the owner's role");
        await Denied(() => auth.RequireAsync(Context(readToken), "admin"), 401);
        await Denied(() => auth.RequireAsync(Context(readToken, "DELETE"), "user"), 403);

        Console.WriteLine("Personal access token checks passed.");
    }
}
