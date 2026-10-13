using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeInsights.Api.Auth;

/// <summary>Authenticates opaque CI credentials through the ASP.NET Core authentication pipeline.</summary>
public sealed class PlatformAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthService auth
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CiToken";
    private string? failureDetail;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization"))
            return AuthenticateResult.NoResult();
        try
        {
            var principal = await auth.RequireAsync(Context, "ci", ct: Context.RequestAborted);
            var claims = new[]
            {
                new Claim(
                    ClaimTypes.NameIdentifier,
                    principal.Kind == "ci" ? principal.TokenId : principal.Sub
                ),
                new Claim(ClaimTypes.Role, principal.Role),
                new Claim("kind", principal.Kind),
            };
            return AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)),
                    SchemeName
                )
            );
        }
        catch (ApiException exception) when (exception.StatusCode == 401)
        {
            failureDetail = exception.Detail.ToString();
            return AuthenticateResult.Fail(exception);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        return Results.Problem(statusCode: 401, detail: failureDetail ?? "Authentication required").ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        return Results.Problem(statusCode: 403, detail: "Access denied").ExecuteAsync(Context);
    }
}

