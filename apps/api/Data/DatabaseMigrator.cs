using System.Text;
using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Data;

public sealed class DatabaseMigrator(AppDbContext db)
{
    public Task MigrateAsync(CancellationToken ct = default) => db.Database.MigrateAsync(ct);

    public static async Task SeedAsync(AppDbContext db, BootstrapOptions bootstrap, CancellationToken ct)
    {
        var providers = new[]
        {
            ("saml", "SAML 2.0", "Enterprise SSO", ""),
            (
                "azure-ad",
                "Azure AD / Entra ID",
                "Enterprise SSO",
                "https://login.microsoftonline.com"
            ),
            ("azure-devops", "Azure DevOps", "DevOps Platform", "https://dev.azure.com"),
            ("github", "GitHub", "DevOps Platform", "https://github.com"),
            ("gitlab", "GitLab", "DevOps Platform", "https://gitlab.com"),
        };
        foreach (var (type, name, category, url) in providers)
            if (!await db.Set<AuthProviderConfig>().AnyAsync(x => x.ProviderType == type, ct))
                db.Add(
                    new AuthProviderConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        ProviderType = type,
                        Name = name,
                        Category = category,
                        ConnectionUrl = url,
                        Config = "{}",
                        Enabled = false,
                        Status = "ready",
                    }
                );
        if (!await db.Set<PlatformUser>().AnyAsync(x => x.Role == "admin", ct))
        {
            var password = bootstrap.AdminPassword;
            if (password.Length == 0)
                throw new InvalidOperationException(
                    "Bootstrap:AdminPassword is required to bootstrap the first administrator"
                );
            if (password.EnumerateRunes().Count() < 12)
                throw new InvalidOperationException(
                    "Bootstrap:AdminPassword must contain at least 12 characters"
                );
            db.Add(
                new PlatformUser
                {
                    UserId = Guid.NewGuid().ToString(),
                    Email =
                        bootstrap.AdminEmail,
                    FullName = "Platform Admin",
                    Role = "admin",
                    HashedPassword = AuthService.HashPassword(password),
                    AuthProvider = "local",
                    IsActive = true,
                }
            );
        }
        await db.SaveChangesAsync(ct);
        if (bootstrap.RequireCompleteMappings)
            await ValidateMappingsAsync(db, ct);

    }

    private static async Task ValidateMappingsAsync(AppDbContext db, CancellationToken ct)
    {
        var organizations = (
            await db.Set<Organization>().Select(x => x.Id).ToListAsync(ct)
        ).ToHashSet();
        var projects = await db.Set<Project>().ToListAsync(ct);
        var profiles = new[]
        {
            "p0-dotnet",
            "p0-react",
            "p0-iac",
            "platform-sast",
            "upcoop-security",
            "upcoop-dotnet",
            "upcoop-react",
            "upcoop-python",
            "upcoop-flutter",
            "upcoop-php",
        };
        var unmappedProjects = projects.Count(x =>
            x.OrganizationId == null
            || !organizations.Contains(x.OrganizationId)
            || !profiles.Contains(x.AnalysisProfileId)
            || !GateEvaluation.TryGetProfile(x.AnalysisProfileId ?? "", x.AnalysisProfileVersion ?? "", out _)
        );
        var now = DateTime.UtcNow;
        var tokens = await db.Set<PlatformApiToken>()
            .Where(x => x.IsCurrent && !x.Revoked && (x.ExpiresAt == null || x.ExpiresAt > now))
            .ToListAsync(ct);
        var mapped = (
            await db.Set<TokenProjectAccess>().Select(x => x.TokenId).Distinct().ToListAsync(ct)
        ).ToHashSet();
        var unmappedTokens = tokens.Count(x => !mapped.Contains(x.TokenId) || !HasScopes(x.Scopes));
        if (unmappedProjects != 0 || unmappedTokens != 0)
            throw new InvalidOperationException(
                $"P0 access mapping incomplete: projects={unmappedProjects}, active_ci_tokens={unmappedTokens}"
            );
    }

    private static bool HasScopes(string scopes)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(scopes)?.Length ?? 0) > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}





