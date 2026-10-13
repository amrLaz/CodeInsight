using Azure.Core;
using Azure.Identity;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

if (args.Contains("--healthcheck"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        return (await client.GetAsync("http://localhost:8000/api/v1/health")).IsSuccessStatusCode
            ? 0
            : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8000"
);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = ApiJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = ApiJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.NumberHandling = ApiJson.Options.NumberHandling;
    foreach (var converter in ApiJson.Options.Converters)
        o.SerializerOptions.Converters.Add(converter);
});
builder.Services.AddDbContext<AppDbContext>((services, options) => options.UseNpgsql(
    builder.Configuration.GetConnectionString("CodeInsights")
    ?? throw new InvalidOperationException("ConnectionStrings:CodeInsights is required"))
    .UseAsyncSeeding((context, _, ct) => DatabaseMigrator.SeedAsync((AppDbContext)context,
        services.GetRequiredService<IOptions<BootstrapOptions>>().Value, ct)));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("Bootstrap"));
builder.Services.AddScoped<DatabaseMigrator>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddScoped<EntraService>();
builder.Services.AddScoped<IEntraAccessService, EntraAccessService>();
builder.Services.AddScoped<IEntraGraphService, EntraGraphService>();
builder.Services.AddSingleton<IEntraGraphTokenProvider, EntraGraphTokenProvider>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<TokenCredential>(_ =>
    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
);
builder.Services.AddSingleton<AzureDevOpsAuthentication>();
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
builder.Services.AddSingleton<ReportStorage>();
builder.Services.AddScoped<IngestionProcessor>();
builder.Services.AddScoped<IngestionAdo>();
builder.Services.AddScoped<AzureDevOpsDecorator>();
builder.Services.AddScoped<GitHubDecorator>();
builder.Services.AddScoped<PullRequestDecoration>();
builder.Services.AddScoped<FindingWorkItems>();
builder.Services.AddScoped<IngestionMaintenance>();
builder.Services.AddHostedService<IngestionWorker>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50L * 1024 * 1024);
builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection("Analysis"));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("entra-graph", client =>
    client.Timeout = TimeSpan.FromSeconds(15)
);
builder.Services.AddOpenApi();
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p =>
        p.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("Link", "X-Next-Cursor")
            .AllowCredentials()
    )
);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope
        .ServiceProvider.GetRequiredService<DatabaseMigrator>()
        .MigrateAsync(CancellationToken.None);
}
if (args.Contains("--migrate"))
    return 0;
app.Use(
    async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            await Results.Problem(statusCode: ex.StatusCode, detail: ex.Detail.ToString()).ExecuteAsync(context);
        }
        catch (BadHttpRequestException ex)
        {
            await Results.Problem(statusCode: ex.StatusCode, detail: "Invalid request").ExecuteAsync(context);
            if (app.Logger.IsEnabled(LogLevel.Debug))
                app.Logger.LogDebug(
                    "Invalid request on {Path}: {Reason}",
                    context.Request.Path,
                    ex.StatusCode
                );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogError(
                "Request failed on {Path}: {ExceptionType}",
                context.Request.Path,
                ex.GetType().Name
            );
            await Results.Problem(statusCode: 500, detail: "Internal Server Error").ExecuteAsync(context);
        }
    }
);
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/v1/health", () => new { status = "ok" });
app.MapGet("/healthz", () => new { service = "api", status = "running" });
app.MapAuthEndpoints();
app.MapEntraAccessEndpoints();
app.MapProjectEndpoints();
app.MapGateEndpoints();
app.MapAnalysisEndpoints();
app.MapIssuePromptEndpoints();
app.MapIngestionEndpoints();
app.MapProfileEndpoints();
app.MapInsightEndpoints();
app.MapCodeEndpoints();
app.MapNewCodeEndpoints();
app.MapCollaborationEndpoints();
app.MapSlaEndpoints();
app.MapWorkItemEndpoints();
app.MapPortfolioEndpoints();
app.MapLicenseEndpoints();
app.MapRuleEndpoints();
app.MapPlatformEndpoints();
app.MapIntegrationEndpoints();
app.MapNotificationEndpoints();
app.MapAuditEndpoints();
app.MapOpenApi("/openapi.json");
app.MapMethods("/openapi.json", ["HEAD"], () => Results.Ok());
app.MapMethods("/docs", ["GET", "HEAD"], () => Results.File(Path.Combine(AppContext.BaseDirectory, "Docs", "swagger.html"), "text/html; charset=utf-8"));
app.MapMethods("/redoc", ["GET", "HEAD"], () => Results.File(Path.Combine(AppContext.BaseDirectory, "Docs", "redoc.html"), "text/html; charset=utf-8"));
app.MapMethods("/docs/oauth2-redirect", ["GET", "HEAD"], () => Results.File(Path.Combine(AppContext.BaseDirectory, "Docs", "oauth2-redirect.html"), "text/html; charset=utf-8"));
app.UseDefaultFiles();
app.UseStaticFiles(
    new StaticFileOptions
    {
        OnPrepareResponse = context =>
        {
            if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers.CacheControl = "no-cache";
            }
        },
    }
);
await app.RunAsync();
return 0;

