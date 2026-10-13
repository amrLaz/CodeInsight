using System.Net.Http.Headers;
using Azure.Core;

namespace CodeInsights.Api.Services;

/// <summary>Authenticates outbound Azure DevOps REST requests with the application managed identity.</summary>
public sealed class AzureDevOpsAuthentication(TokenCredential credential)
{
    /// <summary>Microsoft Entra scope used by Azure DevOps REST APIs.</summary>
    public const string Scope = "https://app.vssps.visualstudio.com/.default";

    /// <summary>Adds a short-lived managed identity bearer token to an HTTP client.</summary>
    public async ValueTask AuthorizeAsync(
        HttpClient client,
        string organizationUrl,
        CancellationToken ct
    )
    {
        NormalizeOrganizationUrl(organizationUrl);
        var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token.Token
        );
    }

    /// <summary>Validates and canonicalizes one Azure DevOps Services organization URL.</summary>
    public static string NormalizeOrganizationUrl(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IdnHost.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
            throw new InvalidDataException(
                "Azure DevOps organization URL must use https://dev.azure.com/<organization>."
            );

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath[..^1] : uri.AbsolutePath;
        var organization = path.Length > 1 ? path[1..] : "";
        if (
            organization.Length == 0
            || organization.Contains('/')
            || organization.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
        )
            throw new InvalidDataException(
                "Azure DevOps organization URL must contain exactly one organization segment."
            );
        return $"https://dev.azure.com/{organization.ToLowerInvariant()}";
    }
}
