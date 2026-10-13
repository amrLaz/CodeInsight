using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class EntraAccessTests
{

private static async Task Denied(Func<Task> action, int status)
    {
        try
        {
            await action();
        }
        catch (ApiException exception) when (exception.StatusCode == status)
        {
            return;
        }
        throw new InvalidOperationException($"Expected HTTP {status}");
    }

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        Assert.True(
            EntraAccessService.MembershipTtl == TimeSpan.FromMinutes(5),
            "Entra membership cache is exactly five minutes"
        );
        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Console.WriteLine(
                "Entra cache contract passed; effective access checks require CODEINSIGHTS_TEST_DB."
            );
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using var db = new AppDbContext(options);
        await new DatabaseMigrator(db).MigrateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = Guid.NewGuid().ToString();
        var user = new PlatformUser
        {
            UserId = Guid.NewGuid().ToString(),
            Email = $"entra-{suffix}@example.test",
            FullName = "Entra access owner",
            Role = "default",
            AuthProvider = "entra",
            EntraTenantId = tenant,
            EntraObjectId = Guid.NewGuid().ToString(),
        };
        var localUser = new PlatformUser
        {
            UserId = Guid.NewGuid().ToString(),
            Email = $"local-{suffix}@example.test",
            FullName = "Local access owner",
            Role = "default",
            AuthProvider = "local",
        };
        var regularGroup = new EntraAccessGroup
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenant,
            ObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Nested regular",
            Role = "regular",
            IsActive = true,
        };
        var adminGroup = new EntraAccessGroup
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenant,
            ObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Nested admin",
            Role = "admin",
            IsActive = false,
        };
        var organization = new Organization
        {
            Id = Guid.NewGuid().ToString(),
            Key = suffix,
            Name = "Group organization",
        };
        var organizationProject = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = $"org-{suffix}",
            Name = "Organization project",
            OrganizationId = organization.Id,
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        var directProject = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = $"direct-{suffix}",
            Name = "Direct project",
            OrganizationId = organization.Id,
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        var groupProject = new Project
        {
            Id = Guid.NewGuid().ToString(),
            Key = $"group-{suffix}",
            Name = "Group project",
            OrganizationId = organization.Id,
            AnalysisProfileId = "p0-dotnet",
            AnalysisProfileVersion = "1",
        };
        db.AddRange(
            user,
            localUser,
            regularGroup,
            adminGroup,
            organization,
            organizationProject,
            directProject,
            groupProject,
            new UserGrant
            {
                Id = Guid.NewGuid().ToString(),
                UserId = user.UserId,
                ScopeType = "project",
                ScopeId = directProject.Key,
            },
            new EntraGroupGrant
            {
                Id = Guid.NewGuid().ToString(),
                GroupId = regularGroup.Id,
                ScopeType = "organization",
                ScopeId = organization.Id,
            },
            new EntraGroupGrant
            {
                Id = Guid.NewGuid().ToString(),
                GroupId = regularGroup.Id,
                ScopeType = "project",
                ScopeId = groupProject.Key,
            }
        );
        await db.SaveChangesAsync();

        var photoGraph = new FakeGraph(
            [],
            photo: new EntraPhoto([4, 8, 15, 16, 23, 42], "image/jpeg")
        );
        var configuredPhoto = await EntraAccessEndpoints.GetConfiguredUserPhotoAsync(
            db,
            photoGraph,
            user.EntraObjectId
        );
        Assert.True(
            configuredPhoto?.Content.SequenceEqual(new byte[] { 4, 8, 15, 16, 23, 42 }) == true
                && photoGraph.PhotoCalls == 1,
            "Configured Entra user photo is loaded through the bounded Graph photo service"
        );
        Assert.True(
            await EntraAccessEndpoints.GetConfiguredUserPhotoAsync(
                db,
                photoGraph,
                regularGroup.ObjectId
            )
                is null
                && await EntraAccessEndpoints.GetConfiguredUserPhotoAsync(
                    db,
                    photoGraph,
                    Guid.NewGuid().ToString()
                ) is null
                && photoGraph.PhotoCalls == 1,
            "Unknown principals and Entra groups cannot trigger a user photo request"
        );
        Assert.True(
            await EntraAccessEndpoints.GetConfiguredUserPhotoAsync(
                db,
                photoGraph,
                localUser.UserId
            )
                is null
                && photoGraph.PhotoCalls == 1,
            "Local accounts cannot trigger an Entra photo request"
        );
        Assert.True(
            await EntraAccessEndpoints.GetConfiguredUserPhotoAsync(
                db,
                new FakeGraph([], photo: null),
                user.EntraObjectId
            ) is null,
            "Missing configured-user photo remains a 404 fallback"
        );

        var graph = new FakeGraph([regularGroup.ObjectId, adminGroup.ObjectId]);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var access = new EntraAccessService(
            db,
            graph,
            cache,
            NullLogger<EntraAccessService>.Instance
        );
        var effective = await access.ResolveAsync(user);
        Assert.True(
            effective.Role == "regular" && effective.GroupIds.SequenceEqual([regularGroup.Id]),
            "Nested active group contributes and strongest role wins"
        );
        Assert.True(graph.MembershipCalls == 1, "First effective access resolution queries Graph once");
        var cached = await access.ResolveAsync(user);
        Assert.True(
            cached.Role == "regular" && graph.MembershipCalls == 1,
            "Successful transitive membership is cached"
        );

        var auth = new AuthService(
            db,
            Options.Create(
                new SessionOptions
                {
                    SigningKey = "Synthetic-signing-key-at-least-32-bytes-only-for-tests",
                }
            ),
            access
        );
        var principal = new Principal(
            "user",
            user.UserId,
            user.Email,
            user.FullName,
            effective.Role,
            DirectoryGroupIds: effective.GroupIds
        );
        var projects = await auth.AllowedProjectKeysAsync(principal);
        Assert.True(
            projects.Contains(directProject.Key)
                && projects.Contains(groupProject.Key)
                && projects.Contains(organizationProject.Key),
            "Direct and group organization/project grants are additive"
        );

        adminGroup.IsActive = true;
        await db.SaveChangesAsync();
        effective = await access.ResolveAsync(user);
        Assert.True(
            effective.Role == "admin" && graph.MembershipCalls == 1,
            "Admin group wins without bypassing the five-minute membership cache"
        );
        regularGroup.IsActive = false;
        adminGroup.IsActive = false;
        await db.SaveChangesAsync();
        effective = await access.ResolveAsync(user);
        Assert.True(
            effective.Role == "default" && effective.GroupIds.Length == 0,
            "Disabled groups stop contributing immediately"
        );

        regularGroup.IsActive = true;
        await db.SaveChangesAsync();
        using var failedCache = new MemoryCache(new MemoryCacheOptions());
        var unavailable = new EntraAccessService(
            db,
            new FakeGraph([], fail: true),
            failedCache,
            NullLogger<EntraAccessService>.Instance
        );
        effective = await unavailable.ResolveAsync(user);
        Assert.True(
            effective.Role == user.Role
                && effective.GroupIds.Length == 0
                && !effective.DirectoryAccessAvailable,
            "Graph outage fails closed to direct role and grants"
        );

        var rawToken = "cip_user_" + suffix;
        db.Add(
            new PlatformApiToken
            {
                TokenId = Guid.NewGuid().ToString(),
                TokenName = "owner",
                TokenHash = AuthService.Sha256(rawToken),
                TokenPreview = "cip_user",
                OwnerUserId = user.UserId,
                Scopes = JsonSerializer.Serialize(new[] { PersonalTokenScopes.Read }),
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            }
        );
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Authorization = "Bearer " + rawToken;
        var tokenPrincipal = await auth.RequireAsync(context, "user");
        Assert.True(
            tokenPrincipal.IsPersonalToken
                && tokenPrincipal.Role == "regular"
                && tokenPrincipal.DirectoryGroupIds?.Contains(regularGroup.Id) == true,
            "Personal token carries exactly its owner's effective group access"
        );
        await Denied(() => auth.RequireAsync(context, "admin"), 401);
    }

    private sealed class FakeGraph(
        HashSet<string> memberships,
        bool fail = false,
        EntraPhoto? photo = null
    )
        : IEntraGraphService
    {
        public int MembershipCalls { get; private set; }
        public int PhotoCalls { get; private set; }

        public Task<HashSet<string>> GetTransitiveGroupIdsAsync(
            string objectId,
            CancellationToken ct = default
        )
        {
            MembershipCalls++;
            if (fail)
                throw new ApiException(502, "Microsoft Graph is unavailable");
            return Task.FromResult(
                new HashSet<string>(memberships, StringComparer.OrdinalIgnoreCase)
            );
        }

        public Task<EntraDirectoryPage> SearchAsync(
            string? kind,
            string search,
            string? cursor,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<EntraDirectoryPrincipal> GetPrincipalAsync(
            string kind,
            string objectId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<EntraPhoto?> GetPhotoAsync(
            string objectId,
            CancellationToken ct = default
        )
        {
            PhotoCalls++;
            return Task.FromResult(photo);
        }
    }
}
