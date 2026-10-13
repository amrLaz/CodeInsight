using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Endpoints;

public static class IntegrationEndpoints
{
    private static readonly string[] RequiredAzureAdKeys = ["tenant_id", "client_id", "client_secret", "redirect_uri"];

    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations").RequireAuthorization();
        group
            .MapGet(
                "/providers",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    var principal = await auth.OptionalAsync(context, ct);
                    var rows = await db.Set<AuthProviderConfig>()
                        .OrderBy(x => x.ProviderType)
                        .ToListAsync(ct);
                    return Results.Json(
                        rows.Select(x =>
                            principal is { Kind: "user", IsAdmin: true }
                                ? ProviderResponse(x)
                                : (object)
                                    new
                                    {
                                        provider_id = x.ProviderType,
                                        display_name = x.Name,
                                        x.Category,
                                        x.Enabled,
                                    }
                        ),
                        ApiJson.Options
                    );
                }
            )
            .AllowAnonymous();
        group
            .MapPut(
                "/providers/{provider_id}",
                async (
                    string provider_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<AuthProviderUpdateRequest>(context, ct);
                    var row = await Provider(db, provider_id, ct);
                    if (!string.IsNullOrEmpty(request.ConnectionUrl))
                        row.ConnectionUrl = request.ConnectionUrl;
                    if (request.ConfigJson is not null)
                    {
                        var config = Config(row.Config);
                        var secret = config["client_secret"]?.DeepClone();
                        foreach (var (key, value) in request.ConfigJson)
                            config[key] = value?.DeepClone();
                        if (!Truthy(config["client_secret"]) && Truthy(secret))
                            config["client_secret"] = secret;
                        if (provider_id == "azure-ad" && Truthy(config["redirect_uri"]))
                            EntraService.ValidateRedirectUri(config["redirect_uri"]!.ToString());
                        row.Config = config.ToJsonString();
                    }
                    row.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Json(ProviderResponse(row), ApiJson.Options);
                }
            )
            .Accepts<AuthProviderUpdateRequest>("application/json");
        group.MapPost(
            "/providers/{provider_id}/test",
            async (
                string provider_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var row = await Provider(db, provider_id, ct);
                if (provider_id == "azure-ad")
                {
                    var config = Config(row.Config);
                    var missing = RequiredAzureAdKeys
                        .Where(k =>
                            config[k] is not JsonValue v
                            || !v.TryGetValue<string>(out var value)
                            || string.IsNullOrWhiteSpace(value)
                        )
                        .ToArray();
                    if (missing.Length > 0)
                        throw new ApiException(
                            400,
                            "Azure AD configuration is incomplete — missing: "
                                + string.Join(", ", missing)
                        );
                    EntraService.ValidateRedirectUri(config["redirect_uri"]!.GetValue<string>());
                }
                else if (string.IsNullOrEmpty(row.ConnectionUrl))
                    throw new ApiException(400, "Connection URL is required before testing");
                row.Status = "validated";
                row.Enabled = true;
                row.LastTestedAt = row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Json(ProviderResponse(row), ApiJson.Options);
            }
        );
        group.MapPost(
            "/providers/{provider_id}/disable",
            async (
                string provider_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var row = await Provider(db, provider_id, ct);
                row.Enabled = false;
                row.Status = "disabled";
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Json(ProviderResponse(row), ApiJson.Options);
            }
        );
        group.MapGet(
            "/projects",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                var principal = await auth.RequireAsync(context, "platform", ct);
                var keys = await auth.AllowedProjectKeysAsync(principal, ct: ct);
                var rows = await db.Set<ProjectIntegration>()
                    .Where(x => keys.Contains(x.ProjectKey))
                    .ToListAsync(ct);
                return Results.Json(rows.Select(ProjectResponse), ApiJson.Options);
            }
        );
        group
            .MapPut(
                "/projects/{project_key}",
                async (
                    string project_key,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    var principal = await auth.RequireAsync(context, "platform", ct);
                    var request = await ApiJson.ReadAsync<ProjectIntegrationRequest>(context, ct);
                    await auth.AssertProjectAccessAsync(
                        principal,
                        project_key,
                        "integration:write",
                        ct
                    );
                    var row = await db.Set<ProjectIntegration>()
                        .FirstOrDefaultAsync(x => x.ProjectKey == project_key, ct);
                    if (row is null)
                    {
                        row = new ProjectIntegration
                        {
                            Id = Guid.NewGuid().ToString(),
                            ProjectKey = project_key,
                        };
                        db.Add(row);
                    }
                    row.RepoProvider = request.RepoProvider;
                    row.RepoUrl = RepositoryUrls.Normalize(request.RepoProvider, request.RepoUrl);
                    row.PipelineProvider = request.PipelineProvider;
                    row.PipelineUrl = request.PipelineUrl;
                    row.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Results.Json(ProjectResponse(row), ApiJson.Options);
                }
            )
            .Accepts<ProjectIntegrationRequest>("application/json");
        group.MapGet(
            "/azure-devops/configs",
            async (HttpContext context, AppDbContext db, AuthService auth, CancellationToken ct) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                return Results.Json(
                    (await db.Set<AzureDevOpsOrganizationConfig>().ToListAsync(ct)).Select(
                        AzureResponse
                    ),
                    ApiJson.Options
                );
            }
        );
        group
            .MapPost(
                "/azure-devops/configs",
                async (
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<AzureDevOpsConfigRequest>(context, ct);
                    await ValidateAzure(db, request, null, ct);
                    var row = new AzureDevOpsOrganizationConfig
                    {
                        ConfigId = Guid.NewGuid().ToString(),
                    };
                    ApplyAzure(row, request);
                    db.Add(row);
                    await db.SaveChangesAsync(ct);
                    return Results.Json(AzureResponse(row), ApiJson.Options);
                }
            )
            .Accepts<AzureDevOpsConfigRequest>("application/json");
        group
            .MapPut(
                "/azure-devops/configs/{config_id}",
                async (
                    string config_id,
                    HttpContext context,
                    AppDbContext db,
                    AuthService auth,
                    CancellationToken ct
                ) =>
                {
                    await auth.RequireAsync(context, "admin", ct);
                    var request = await ApiJson.ReadAsync<AzureDevOpsConfigRequest>(context, ct);
                    var row = await Azure(db, config_id, ct);
                    await ValidateAzure(db, request, config_id, ct);
                    ApplyAzure(row, request);
                    await db.SaveChangesAsync(ct);
                    return Results.Json(AzureResponse(row), ApiJson.Options);
                }
            )
            .Accepts<AzureDevOpsConfigRequest>("application/json");
        group.MapDelete(
            "/azure-devops/configs/{config_id}",
            async (
                string config_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var row = await Azure(db, config_id, ct);
                db.Remove(row);
                await db.SaveChangesAsync(ct);
                return Results.Json(
                    new { message = $"Configuration '{row.ConfigName}' deleted successfully" },
                    ApiJson.Options
                );
            }
        );
        group.MapPost(
            "/azure-devops/configs/{config_id}/test",
            async (
                string config_id,
                HttpContext context,
                AppDbContext db,
                AuthService auth,
                IHttpClientFactory clients,
                AzureDevOpsAuthentication azureDevOpsAuthentication,
                CancellationToken ct
            ) =>
            {
                await auth.RequireAsync(context, "admin", ct);
                var row = await Azure(db, config_id, ct);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var client = clients.CreateClient("azure-devops");
                    await azureDevOpsAuthentication.AuthorizeAsync(
                        client,
                        row.OrganizationUrl,
                        timeout.Token
                    );
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        row.OrganizationUrl.TrimEnd('/') + "/_apis/projects?api-version=7.0&$top=1"
                    );
                    using var response = await client.SendAsync(request, timeout.Token);
                    var code = (int)response.StatusCode;
                    if (code == 401)
                        throw new ApiException(
                            400,
                            "Azure DevOps rejected the CodeInsights managed identity (401)"
                        );
                    if (code == 403)
                        throw new ApiException(
                            400,
                            "The CodeInsights managed identity lacks Azure DevOps Project Read permission (403)"
                        );
                    if (code is not (200 or 203))
                        throw new ApiException(400, $"Azure DevOps returned HTTP {code}");
                    using var identityResponse = await client.GetAsync(row.OrganizationUrl.TrimEnd('/') + "/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1", timeout.Token);
                    if (!identityResponse.IsSuccessStatusCode) throw new ApiException(400, "Azure DevOps bot identity could not be resolved");
                    using var identityBody = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync(timeout.Token));
                    row.BotIdentityId = identityBody.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(row.BotIdentityId)) throw new ApiException(400, "Azure DevOps returned no authenticated identity");
                    row.Status = "validated";
                    row.LastTestedAt = row.UpdatedAt = DateTime.UtcNow;
                    row.LastError = null;
                    await db.SaveChangesAsync(ct);
                    return Results.Json(
                        new
                        {
                            status = "ok",
                            message = "Azure DevOps managed identity is valid and accessible",
                        },
                        ApiJson.Options
                    );
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    row.Status = "error";
                    row.LastTestedAt = row.UpdatedAt = DateTime.UtcNow;
                    row.LastError = exception is ApiException api
                        ? api.Detail.ToString()
                        : exception.Message;
                    await db.SaveChangesAsync(ct);
                    if (exception is ApiException)
                        throw;
                    throw new ApiException(400, "Connection test failed: " + exception.Message);
                }
            }
        );
    }

    internal static JsonObject Config(string? value)
    {
        try
        {
            return JsonNode.Parse(value ?? "{}") as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    internal static bool Truthy(JsonNode? value) =>
        value is not null
        && value.ToJsonString() is not ("null" or "false" or "0" or "\"\"" or "[]" or "{}");

    private static object ProviderResponse(AuthProviderConfig row)
    {
        var config = Config(row.Config);
        if (config.ContainsKey("client_secret"))
        {
            config["client_secret_configured"] = Truthy(config["client_secret"]);
            config.Remove("client_secret");
        }
        return new
        {
            provider_id = row.ProviderType,
            display_name = row.Name,
            row.Category,
            row.ConnectionUrl,
            row.Enabled,
            row.Status,
            last_tested_at = row.LastTestedAt is null ? "" : ApiJson.Iso(row.LastTestedAt.Value),
            config,
        };
    }

    private static object ProjectResponse(ProjectIntegration row) =>
        new
        {
            row.ProjectKey,
            row.RepoProvider,
            row.RepoUrl,
            row.PipelineProvider,
            row.PipelineUrl,
        };

    private static object AzureResponse(AzureDevOpsOrganizationConfig row) =>
        new
        {
            row.ConfigId,
            row.ConfigName,
            row.OrganizationUrl,
            row.QualityGateEnabled,
            row.RepoImportEnabled,
            row.WorkItemsEnabled,
            authentication_type = "managed_identity",
            row.Status,
            last_tested_at = row.LastTestedAt is null ? null : ApiJson.Iso(row.LastTestedAt.Value),
            row.LastError,
            created_at = ApiJson.Iso(row.CreatedAt),
            updated_at = ApiJson.Iso(row.UpdatedAt),
        };

    private static async Task<AuthProviderConfig> Provider(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<AuthProviderConfig>().FirstOrDefaultAsync(x => x.ProviderType == id, ct)
        ?? throw new ApiException(404, $"Provider '{id}' not found");

    private static async Task<AzureDevOpsOrganizationConfig> Azure(
        AppDbContext db,
        string id,
        CancellationToken ct
    ) =>
        await db.Set<AzureDevOpsOrganizationConfig>().FindAsync([id], ct)
        ?? throw new ApiException(404, "Configuration not found");

    private static async Task ValidateAzure(
        AppDbContext db,
        AzureDevOpsConfigRequest request,
        string? id,
        CancellationToken ct
    )
    {
        try
        {
            request.OrganizationUrl = AzureDevOpsAuthentication.NormalizeOrganizationUrl(
                request.OrganizationUrl
            );
        }
        catch (InvalidDataException exception)
        {
            throw new ApiException(422, exception.Message);
        }
        if (
            await db.Set<AzureDevOpsOrganizationConfig>()
                .AnyAsync(x => x.ConfigId != id && x.ConfigName == request.ConfigName, ct)
        )
            throw new ApiException(400, $"Configuration '{request.ConfigName}' already exists");
        if (
            await db.Set<AzureDevOpsOrganizationConfig>()
                .AnyAsync(x => x.ConfigId != id && x.OrganizationUrl == request.OrganizationUrl, ct)
        )
            throw new ApiException(
                400,
                $"Organization URL '{request.OrganizationUrl}' is already configured"
            );
    }

    private static void ApplyAzure(
        AzureDevOpsOrganizationConfig row,
        AzureDevOpsConfigRequest request
    )
    {
        row.ConfigName = request.ConfigName;
        row.OrganizationUrl = request.OrganizationUrl;
        row.RepoImportEnabled = request.RepoImportEnabled;
        row.QualityGateEnabled = request.QualityGateEnabled;
        row.WorkItemsEnabled = request.WorkItemsEnabled;
        row.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class AuthProviderUpdateRequest
{
    [Required(AllowEmptyStrings = true)]
    public string ConnectionUrl { get; set; } = "";
    public JsonObject? ConfigJson { get; set; }
}

public sealed class ProjectIntegrationRequest
{
    [Required(AllowEmptyStrings = true)]
    public string RepoProvider { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string RepoUrl { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string PipelineProvider { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string PipelineUrl { get; set; } = null!;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AzureDevOpsConfigRequest
{
    [Required(AllowEmptyStrings = true)]
    public string ConfigName { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string OrganizationUrl { get; set; } = null!;

    public bool RepoImportEnabled { get; set; }
    public bool QualityGateEnabled { get; set; }
    public bool WorkItemsEnabled { get; set; }
}
