using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CodeInsights.Api.Auth;

public sealed record EntraEffectiveAccess(
    string Role,
    string[] GroupIds,
    bool DirectoryAccessAvailable
);

public interface IEntraAccessService
{
    Task<EntraEffectiveAccess> ResolveAsync(
        PlatformUser user,
        CancellationToken ct = default
    );
}

public sealed class EntraAccessService(
    AppDbContext db,
    IEntraGraphService graph,
    IMemoryCache cache,
    ILogger<EntraAccessService> logger
) : IEntraAccessService
{
    internal static readonly TimeSpan MembershipTtl = TimeSpan.FromMinutes(5);

    public async Task<EntraEffectiveAccess> ResolveAsync(
        PlatformUser user,
        CancellationToken ct = default
    )
    {
        if (
            user.AuthProvider != "entra"
            || string.IsNullOrWhiteSpace(user.EntraTenantId)
            || string.IsNullOrWhiteSpace(user.EntraObjectId)
        )
            return new EntraEffectiveAccess(user.Role, [], true);

        var configured = await db.Set<EntraAccessGroup>()
            .AsNoTracking()
            .Where(x => x.TenantId == user.EntraTenantId && x.IsActive)
            .ToListAsync(ct);
        if (configured.Count == 0)
            return new EntraEffectiveAccess(user.Role, [], true);

        var cacheKey = $"entra-memberships:{user.EntraTenantId}:{user.EntraObjectId}";
        HashSet<string> memberships;
        if (!cache.TryGetValue(cacheKey, out memberships!))
        {
            try
            {
                memberships = await graph.GetTransitiveGroupIdsAsync(user.EntraObjectId, ct);
                cache.Set(cacheKey, memberships, MembershipTtl);
            }
            catch (ApiException exception)
            {
                logger.LogWarning(
                    "Entra group membership unavailable for user {UserId} status={StatusCode}",
                    user.UserId,
                    exception.StatusCode
                );
                return new EntraEffectiveAccess(user.Role, [], false);
            }
        }

        var groups = configured.Where(x => memberships.Contains(x.ObjectId)).ToArray();
        var role = groups.Select(x => x.Role).Append(user.Role).MaxBy(RoleRank) ?? user.Role;
        return new EntraEffectiveAccess(role, groups.Select(x => x.Id).ToArray(), true);
    }

    internal static int RoleRank(string role) =>
        role switch
        {
            "admin" => 3,
            "regular" => 2,
            _ => 1,
        };
}
