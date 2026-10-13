using System.Text.Json;
using Azure.Core;
using CodeInsights.Api;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;

public sealed class AzureDevOpsAuthenticationTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var credential = new RecordingCredential();
        var authentication = new AzureDevOpsAuthentication(credential);
        using var client = new HttpClient();

        await authentication.AuthorizeAsync(
            client,
            "https://dev.azure.com/up-group/",
            CancellationToken.None
        );

        Assert.True(
            credential.Scopes.SequenceEqual([AzureDevOpsAuthentication.Scope]),
            "Azure DevOps Entra scope"
        );
        Assert.True(
            client.DefaultRequestHeaders.Authorization?.Scheme == "Bearer"
                && client.DefaultRequestHeaders.Authorization.Parameter == "short-lived-token",
            "Managed identity token sent as Bearer"
        );
        Assert.True(
            typeof(AzureDevOpsConfigRequest).GetProperty("PersonalAccessToken") is null,
            "Azure DevOps configuration contract has no PAT"
        );
        try
        {
            JsonSerializer.Deserialize<AzureDevOpsConfigRequest>(
                """{"config_name":"test","organization_url":"https://dev.azure.com/up-group","personal_access_token":"obsolete"}""",
                ApiJson.Options
            );
            throw new InvalidOperationException("Legacy Azure DevOps PAT input was accepted.");
        }
        catch (JsonException)
        {
            // Expected: a removed secret must fail loudly instead of being ignored.
        }
        Assert.True(
            AzureDevOpsAuthentication.NormalizeOrganizationUrl(
                "https://dev.azure.com/UP-GROUP/"
            ) == "https://dev.azure.com/up-group",
            "Azure DevOps organization URL canonicalized"
        );

        var rejectedCredential = new RecordingCredential();
        var rejectedAuthentication = new AzureDevOpsAuthentication(rejectedCredential);
        await ExpectInvalidAsync(rejectedAuthentication, "https://hostile.invalid/up-group");
        await ExpectInvalidAsync(
            rejectedAuthentication,
            "https://dev.azure.com/up-group?redirect=https://hostile.invalid"
        );
        await ExpectInvalidAsync(rejectedAuthentication, "https://user@dev.azure.com/up-group");
        await ExpectInvalidAsync(rejectedAuthentication, "https://dev.azure.com:444/up-group");
        Assert.True(rejectedCredential.Requests == 0, "Invalid organization rejected before token acquisition");
        Console.WriteLine("Azure DevOps managed identity authentication check passed.");
    }

    private static async Task ExpectInvalidAsync(
        AzureDevOpsAuthentication authentication,
        string organizationUrl
    )
    {
        using var client = new HttpClient();
        try
        {
            await authentication.AuthorizeAsync(client, organizationUrl, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Unsafe Azure DevOps organization URL was accepted.");
    }

private sealed class RecordingCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public int Requests { get; private set; }

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Azure DevOps authentication must stay asynchronous.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(
                new AccessToken("short-lived-token", DateTimeOffset.UtcNow.AddMinutes(5))
            );
        }
    }
}
