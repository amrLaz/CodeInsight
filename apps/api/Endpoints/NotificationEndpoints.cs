using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications/channels").RequireAuthorization();
        group.MapGet(
            "",
            async (HttpContext context, AuthService auth, AppDbContext db, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return Results.Json(
                    (
                        await db.Set<NotificationChannel>()
                            .OrderBy(x => x.CreatedAt)
                            .ToListAsync(ct)
                    ).Select(Response),
                    ApiJson.Options
                );
            }
        );
        group
            .MapPost(
                "",
                async (
                    HttpContext context,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<ChannelCreateRequest>(context, ct);
                    if (request.ChannelType is not ("webhook" or "email"))
                        throw new ApiException(422, "channel_type must be 'webhook' or 'email'");
                    var row = new NotificationChannel
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = request.Name.Trim(),
                        ChannelType = request.ChannelType,
                        Config = request.Config.ToJsonString(),
                        Enabled = request.Enabled,
                        OnFail = request.OnFail,
                        OnPass = request.OnPass,
                        OnSlaBreach = request.OnSlaBreach,
                    };
                    db.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Json(Response(row), ApiJson.Options);
                }
            )
            .Accepts<ChannelCreateRequest>("application/json");
        group
            .MapPut(
                "/{channel_id}",
                async (
                    string channel_id,
                    HttpContext context,
                    AuthService auth,
                    AppDbContext db,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<ChannelUpdateRequest>(context, ct);
                    var row = await Find(db, channel_id, ct);
                    if (request.Name is not null)
                        row.Name = request.Name.Trim();
                    if (request.Config is not null)
                    {
                        var config = IntegrationEndpoints.Config(row.Config);
                        var password = config["smtp_password"]?.DeepClone();
                        foreach (var (key, value) in request.Config)
                            config[key] = value?.DeepClone();
                        if (
                            !IntegrationEndpoints.Truthy(config["smtp_password"])
                            && IntegrationEndpoints.Truthy(password)
                        )
                            config["smtp_password"] = password;
                        row.Config = config.ToJsonString();
                    }
                    if (request.Enabled is { } enabled)
                        row.Enabled = enabled;
                    if (request.OnFail is { } fail)
                        row.OnFail = fail;
                    if (request.OnPass is { } pass)
                        row.OnPass = pass;
                    if (request.OnSlaBreach is { } sla)
                        row.OnSlaBreach = sla;
                    row.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Json(Response(row), ApiJson.Options);
                }
            )
            .Accepts<ChannelUpdateRequest>("application/json");
        group.MapDelete(
            "/{channel_id}",
            async (
                string channel_id,
                HttpContext context,
                AuthService auth,
                AppDbContext db,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                db.Remove(await Find(db, channel_id, ct));
                await db.SaveChangesAsync(ct);
                return Results.Json(new { status = "deleted" }, ApiJson.Options);
            }
        );
        group.MapPost(
            "/{channel_id}/test",
            async (
                string channel_id,
                HttpContext context,
                AuthService auth,
                AppDbContext db,
                NotificationService notifications,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var row = await Find(db, channel_id, ct);
                var result = await notifications.SendTestAsync(
                    row.ChannelType,
                    IntegrationEndpoints.Config(row.Config),
                    ct
                );
                if (result != "ok")
                    throw new ApiException(400, "Test failed: " + result);
                return Results.Json(
                    new { status = "ok", message = "Test notification sent successfully" },
                    ApiJson.Options
                );
            }
        );
    }

    private static async Task<NotificationChannel> Find(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<NotificationChannel>().FindAsync([id], ct)
        ?? throw new ApiException(404, "Channel not found");

    private static object Response(NotificationChannel row)
    {
        var config = IntegrationEndpoints.Config(row.Config);
        if (config.ContainsKey("smtp_password"))
        {
            config["smtp_password_set"] = IntegrationEndpoints.Truthy(config["smtp_password"]);
            config.Remove("smtp_password");
        }
        return new
        {
            row.Id,
            row.Name,
            row.ChannelType,
            config,
            row.Enabled,
            row.OnFail,
            row.OnPass,
            row.OnSlaBreach,
            created_at = ApiJson.Iso(row.CreatedAt),
            updated_at = ApiJson.Iso(row.UpdatedAt),
        };
    }
}

public sealed class ChannelCreateRequest
{
    [Required(AllowEmptyStrings = true)]
    public string Name { get; set; } = null!;

    [Required]
    public string ChannelType { get; set; } = null!;

    [Required]
    public JsonObject Config { get; set; } = [];
    public bool OnFail { get; set; } = true;
    public bool OnPass { get; set; }
    public bool OnSlaBreach { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ChannelUpdateRequest
{
    public string? Name { get; set; }
    public JsonObject? Config { get; set; }
    public bool? OnFail { get; set; }
    public bool? OnPass { get; set; }
    public bool? OnSlaBreach { get; set; }
    public bool? Enabled { get; set; }
}
