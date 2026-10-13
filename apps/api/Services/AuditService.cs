using System.Text.Json;
using CodeInsights.Api.Data;

namespace CodeInsights.Api.Services;

/// <summary>Appends an audit record to the caller's transaction.</summary>
public sealed class AuditService(AppDbContext db, ILogger<AuditService> logger)
{
    public void Log(
        string actorEmail,
        string actorRole,
        string action,
        string resourceType,
        string? resourceId = null,
        string? resourceLabel = null,
        object? details = null
    )
    {
        try
        {
            db.Add(
                new AuditLog
                {
                    Id = Guid.NewGuid().ToString(),
                    ActorEmail = actorEmail,
                    ActorRole = actorRole,
                    Action = action,
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    ResourceLabel = resourceLabel,
                    Details = details is null
                        ? null
                        : JsonSerializer.Serialize(details, ApiJson.Options),
                    CreatedAt = DateTime.UtcNow,
                }
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Audit append failed for {Action}", action);
        }
    }
}
