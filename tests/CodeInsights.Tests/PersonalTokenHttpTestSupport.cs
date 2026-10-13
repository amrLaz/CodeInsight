using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeInsights.Api;

/// <summary>
/// End-to-end contract of the credentials a workstation uses: creating a personal access token,
/// what it may and may not do, and resolving a project from a clone URL.
/// </summary>
public static class PersonalTokenHttpTestSupport
{
    public static async Task RunAsync(
        HttpClient client,
        string adminSession,
        string projectKey,
        string projectId
    )
    {
        var created = await Call(
            client,
            "POST",
            "/api/v1/auth/me/tokens",
            new { name = "VS Code checks", ttl_days = 7 },
            adminSession,
            201
        );
        var pat = created.GetProperty("token").GetString()!;
        var tokenId = created.GetProperty("token_id").GetString()!;
        Assert.True(
            created.GetProperty("scopes").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["analysis:read"]),
            "a personal token is read-only by default"
        );
        // The timestamp must be a real instant: an unparsable suffix reaches the UI as "Invalid Date".
        Assert.True(
            DateTimeOffset.TryParse(created.GetProperty("expires_at").GetString(), out var expiresAt)
                && expiresAt > DateTimeOffset.UtcNow,
            "expiration is set and parsable"
        );
        Assert.True(
            DateTimeOffset.TryParse(created.GetProperty("created_at").GetString(), out _),
            "creation timestamp is parsable"
        );

        // Expiration is mandatory and bounded, and the scope list is closed.
        await Call(client, "POST", "/api/v1/auth/me/tokens", new { name = "No expiry", ttl_days = 0 }, adminSession, 422);
        await Call(client, "POST", "/api/v1/auth/me/tokens", new { name = "Too long", ttl_days = 400 }, adminSession, 422);
        await Call(client, "POST", "/api/v1/auth/me/tokens", new { name = "", ttl_days = 7 }, adminSession, 422);
        await Call(
            client,
            "POST",
            "/api/v1/auth/me/tokens",
            new { name = "Escalation", ttl_days = 7, scopes = new[] { "analysis:write" } },
            adminSession,
            422
        );

        var listed = await Call(client, "GET", "/api/v1/auth/me/tokens", token: adminSession);
        var row = listed.EnumerateArray().First(x => x.GetProperty("token_id").GetString() == tokenId);
        Assert.True(row.GetProperty("preview").GetString()!.StartsWith("..."), "only a preview is listed");
        Assert.True(
            !listed.EnumerateArray().Any(x => x.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == pat)),
            "the plaintext token is never listed again"
        );

        // The token reads on behalf of its owner...
        await Call(client, "GET", "/api/v1/auth/me", token: pat);
        await Call(client, "GET", $"/api/v1/projects/{projectId}", token: pat);
        // ...but writes, administration and minting another token are refused.
        await Call(client, "POST", "/api/v1/gates", new { name = "From a token" }, pat, 403);
        await Call(client, "GET", "/api/v1/auth/users", token: pat, expected: 401);
        await Call(client, "POST", "/api/v1/auth/me/tokens", new { name = "Chained", ttl_days = 7 }, pat, 403);

        // Resolving the project from the clone URL the IDE knows.
        // The repository name follows the run so a previous run's project cannot match too.
        var repository = $"https://dev.azure.com/up-group/Up.France.ODI/_git/{projectKey}";
        await Call(
            client,
            "PUT",
            $"/api/v1/integrations/projects/{projectKey}",
            new
            {
                repo_provider = "azure-devops",
                repo_url = repository,
                pipeline_provider = "azure-devops",
                pipeline_url = "",
            },
            adminSession
        );
        foreach (
            var clone in new[]
            {
                repository,
                repository + ".git",
                $"git@ssh.dev.azure.com:v3/up-group/Up.France.ODI/{projectKey}",
            }
        )
        {
            var resolved = await Call(
                client,
                "GET",
                "/api/v1/projects?repo_url=" + Uri.EscapeDataString(clone),
                token: pat
            );
            Assert.True(
                resolved.EnumerateArray().Select(x => x.GetProperty("key").GetString()).SequenceEqual([projectKey]),
                $"clone URL {clone} resolves to its project"
            );
        }
        var unknown = await Call(
            client,
            "GET",
            "/api/v1/projects?repo_url=" + Uri.EscapeDataString("https://dev.azure.com/up-group/Up.France.ODI/_git/Unknown"),
            token: pat
        );
        Assert.True(unknown.GetArrayLength() == 0, "an unknown repository resolves to nothing");
        await Call(client, "GET", "/api/v1/projects?repo_url=%20", token: pat, expected: 422);

        // Revocation is immediate and only the owner can revoke.
        await Call(client, "DELETE", $"/api/v1/auth/me/tokens/{tokenId}", token: adminSession, expected: 204);
        await Call(client, "GET", "/api/v1/auth/me", token: pat, expected: 401);
        await Call(client, "DELETE", $"/api/v1/auth/me/tokens/{tokenId}", token: adminSession, expected: 204);
        await Call(client, "DELETE", "/api/v1/auth/me/tokens/unknown", token: adminSession, expected: 404);
        Console.WriteLine("Personal access token HTTP checks passed.");
    }

private static async Task<JsonElement> Call(
        HttpClient client,
        string method,
        string path,
        object? body = null,
        string? token = null,
        int expected = 200
    )
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode != expected)
            throw new InvalidOperationException(
                $"{method} {path}: expected {expected}, got {(int)response.StatusCode}: {text[..Math.Min(text.Length, 500)]}"
            );
        return response.StatusCode == HttpStatusCode.NoContent || text.Length == 0
            ? default
            : JsonDocument.Parse(text).RootElement.Clone();
    }
}
