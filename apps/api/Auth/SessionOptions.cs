using System.Security.Claims;
using System.Text;
using CodeInsights.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CodeInsights.Api.Auth;

public sealed class SessionOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "CodeInsights";
    public string Audience { get; set; } = "CodeInsights.Web";
    public int SessionMinutes { get; set; } = 480;
    public string LocalAccountsMode { get; set; } = "enabled";
    public bool AutoEntraSignIn { get; set; } = true;
    public bool LocalLoginVisible => LocalAccountsMode == "enabled";
    public SymmetricSecurityKey SecurityKey() => new(Encoding.UTF8.GetBytes(SigningKey));
    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        ValidateIssuerSigningKey = true, IssuerSigningKey = SecurityKey(), ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.Zero, NameClaimType = "name", RoleClaimType = "role"
    };
}

public static class PlatformAuthentication
{
    public const string Scheme = "Platform";
    public static IServiceCollection AddPlatformAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>().Bind(configuration.GetSection("Authentication"))
            .Validate(x => Encoding.UTF8.GetByteCount(x.SigningKey) >= 32, "Authentication:SigningKey must contain at least 32 UTF-8 bytes")
            .Validate(x => !string.IsNullOrWhiteSpace(x.Issuer) && !string.IsNullOrWhiteSpace(x.Audience), "JWT issuer and audience are required")
            .Validate(x => x.SessionMinutes > 0, "Authentication:SessionMinutes must be positive")
            .Validate(
                x => x.LocalAccountsMode is "enabled" or "bootstrap-only",
                "Authentication:LocalAccountsMode must be enabled or bootstrap-only"
            )
            .ValidateOnStart();
        services.AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, Scheme, policy => policy.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().Count(c => c == '.') == 2 ? JwtBearerDefaults.AuthenticationScheme : PlatformAuthenticationHandler.SchemeName)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, PlatformAuthenticationHandler>(PlatformAuthenticationHandler.SchemeName, _ => { });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<SessionOptions>>((jwt, configured) =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = configured.Value.ValidationParameters();
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var userId = context.Principal?.FindFirst("sub")?.Value;
                    var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                    var user = await db.Set<PlatformUser>().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive, context.HttpContext.RequestAborted);
                    if (user is null) { context.Fail("Invalid or inactive user"); return; }
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.UserId), new Claim("name", user.FullName), new Claim("email", user.Email), new Claim("role", user.Role), new Claim("kind", "user") }, JwtBearerDefaults.AuthenticationScheme, "name", "role"));
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    await Results.Problem(statusCode: 401, detail: "Invalid, expired, or inactive session").ExecuteAsync(context.HttpContext);
                }
            };
        });
        services.AddAuthorization();
        return services;
    }
}
