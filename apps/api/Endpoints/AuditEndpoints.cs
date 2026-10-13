using System.Text.Json;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/v1/audit-logs",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var query = context.Request.Query;
                var limit = Parse(query["limit"], 100, 1, 500);
                var offset = Parse(query["offset"], 0, 0, int.MaxValue);
                var entries = db.Set<AuditLog>().AsNoTracking();
                string? action = query["action"],
                    actor = query["actor"];
                if (!string.IsNullOrEmpty(action))
                    entries = entries.Where(e => e.Action == action);
                if (!string.IsNullOrEmpty(actor))
                    entries = entries.Where(e =>
                        EF.Functions.ILike(e.ActorEmail, "%" + actor + "%")
                    );
                var total = await entries.CountAsync(ct);
                var rows = await entries
                    .OrderByDescending(e => e.CreatedAt)
                    .Skip(offset)
                    .Take(limit)
                    .ToListAsync(ct);
                return Results.Json(
                    new
                    {
                        total,
                        entries = rows.Select(e => new
                        {
                            e.Id,
                            e.ActorEmail,
                            e.ActorRole,
                            e.Action,
                            e.ResourceType,
                            e.ResourceId,
                            e.ResourceLabel,
                            details = e.Details is null
                                ? (JsonElement?)null
                                : JsonSerializer.Deserialize<JsonElement>(e.Details),
                            created_at = ApiJson.Iso(e.CreatedAt),
                        }),
                    },
                    ApiJson.Options
                );
            }
        );
        app.MapGet(
            "/api/v1/audit-logs/actions",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return await db.Set<AuditLog>()
                    .Select(e => e.Action)
                    .Distinct()
                    .Order()
                    .ToListAsync(ct);
            }
        );
    }

    private static int Parse(string? value, int fallback, int min, int max) =>
        string.IsNullOrEmpty(value) ? fallback
        : int.TryParse(value, out var number) && number >= min && number <= max ? number
        : throw new ApiException(422, "Invalid pagination parameter");
}

