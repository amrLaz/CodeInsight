using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeInsights.Api;

public sealed class HttpTests
{
    [Fact]
    public async Task ApiContracts()
    {
        var url = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_URL");
        if (string.IsNullOrWhiteSpace(url))
            throw Xunit.Sdk.SkipException.ForSkip("Set CODEINSIGHTS_TEST_URL to run API HTTP contract tests.");
        await RunAsync(url);
    }

    internal static async Task RunAsync(string url)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(40),
        };
        if (Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_SPA") == "true")
        {
            using var htmlResponse = await client.GetAsync("/");
            htmlResponse.EnsureSuccessStatusCode();
            if (htmlResponse.Headers.CacheControl?.NoCache != true)
                throw new InvalidOperationException(
                    "SPA HTML must be revalidated so deployed chunk references stay current."
                );
            var html = await htmlResponse.Content.ReadAsStringAsync();
            var assets = System.Text.RegularExpressions.Regex.Matches(
                html,
                "(?:src|href)=\"(/assets/[^\"]+)\""
            );
            if (assets.Count < 2)
                throw new InvalidOperationException(
                    "SPA must reference its JavaScript and CSS assets."
                );
            foreach (System.Text.RegularExpressions.Match asset in assets)
            {
                using var response = await client.GetAsync(asset.Groups[1].Value);
                response.EnsureSuccessStatusCode();
            }
            Console.WriteLine("Bundled React page and assets served successfully.");
        }
        var email =
            Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_EMAIL")
            ?? "admin@codeinsights.local";
        var password =
            Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_PASSWORD")
            ?? "Isolated-bootstrap-123";
        var login = await Call(client, "POST", "/api/v1/auth/login", new { email, password });
        var admin = login.GetProperty("token").GetString()!;
        using (var denied = await client.GetAsync("/api/v1/auth/me"))
        {
            var problem = await denied.Content.ReadFromJsonAsync<JsonElement>();
            if (
                denied.StatusCode != HttpStatusCode.Unauthorized
                || denied.Content.Headers.ContentType?.MediaType != "application/problem+json"
                || problem.GetProperty("status").GetInt32() != 401
            )
                throw new InvalidOperationException(
                    "Unauthorized responses must use ProblemDetails."
                );
        }
        await Call(client, "GET", "/api/v1/auth/me", token: admin);
        // Every protected route must reject an anonymous caller before touching project data.
        using var inventory = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "routes.json"))
        );
        var publicRoutes = new HashSet<string>
        {
            "/api/v1/health",
            "/healthz",
            "/api/v1/auth/login",
            "/api/v1/auth/methods",
            "/api/v1/auth/entra/login",
            "/api/v1/auth/entra/callback",
            "/api/v1/integrations/providers",
        };
        var checkedRoutes = 0;
        foreach (var route in inventory.RootElement.EnumerateArray())
        {
            var path = route.GetProperty("path").GetString()!;
            if (!path.StartsWith("/api/") || publicRoutes.Contains(path))
                continue;
            foreach (var method in route.GetProperty("methods").EnumerateArray())
            {
                var concrete = System.Text.RegularExpressions.Regex.Replace(
                    path,
                    "\\{[^}]+\\}",
                    "missing"
                );
                if (path.StartsWith("/api/v1/collaboration/occurrences/"))
                    concrete = "/api/v1/collaboration/occurrences/1";
                if (path.StartsWith("/api/v1/issues/"))
                    concrete = "/api/v1/issues/1/prompt";
                await Call(client, method.GetString()!, concrete, expected: 401);
                checkedRoutes++;
            }
        }
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var organization = await Call(
            client,
            "POST",
            "/api/v1/projects/organizations/catalog",
            new { key = "org-" + suffix, name = "HTTP checks" },
            admin
        );
        var key = "http-" + suffix;
        var project = await Call(
            client,
            "POST",
            "/api/v1/projects",
            new
            {
                key,
                name = "HTTP checks",
                organization_id = organization.GetProperty("id").GetString(),
                analysis_profile_id = "p0-dotnet",
                analysis_profile_version = "1",
            },
            admin
        );
        var projectId = project.GetProperty("id").GetString()!;
        await Call(client, "POST", "/api/v1/projects", new { key, name = "Duplicate" }, admin, 409);
        await Call(
            client,
            "PATCH",
            $"/api/v1/projects/{projectId}",
            new { description = "Preserved description" },
            admin
        );
        await Call(client, "GET", $"/api/v1/projects/{projectId}", token: admin);
        await Call(client, "GET", "/api/v1/projects?archived=false", token: admin);
        await Call(client, "GET", "/api/v1/projects/analysis-profiles/catalog", token: admin);
        await Call(client, "GET", "/api/v1/projects/organizations/catalog", token: admin);
        await Call(
            client,
            "POST",
            $"/api/v1/projects/{projectId}/retention-policy",
            new { keep_analyses_days = 91 },
            admin
        );
        await Call(client, "GET", $"/api/v1/projects/{projectId}/retention-policy", token: admin);
        await Call(
            client,
            "POST",
            $"/api/v1/projects/{projectId}/retention-policy",
            new { keep_analyses_days = 0 },
            admin,
            422
        );
        var gate = await Call(
            client,
            "POST",
            "/api/v1/gates",
            new { name = "Custom gate", min_security_score = 60 },
            admin
        );
        var gateId = gate.GetProperty("gate_id").GetString()!;
        await Call(client, "PUT", $"/api/v1/gates/{gateId}", new { name = "Updated gate" }, admin);
        await Call(client, "GET", $"/api/v1/gates/{gateId}", token: admin);
        await Call(client, "GET", "/api/v1/gates/default", token: admin);
        await Call(
            client,
            "PUT",
            $"/api/v1/gates/assignments/{key}",
            new { gate_id = gateId },
            admin
        );
        await Call(client, "GET", "/api/v1/gates/assignments", token: admin);
        await Call(client, "GET", "/api/v1/gates", token: admin);
        await Call(client, "DELETE", $"/api/v1/gates/assignments/{key}", token: admin);
        await Call(client, "DELETE", $"/api/v1/gates/{gateId}", token: admin);
        var token = await Call(
            client,
            "POST",
            "/api/v1/platform/token/generate",
            new
            {
                token_name = "HTTP checks",
                project_keys = new[] { key },
                scopes = new[] { "analysis:read", "analysis:write", "integration:write" },
            },
            admin
        );
        var ci = token.GetProperty("token").GetString()!;
        // Timestamps must be real instants; a malformed suffix reaches the UI as "Invalid Date".
        // A CI token without ttl_days has no expiry at all, and null stays null.
        foreach (var field in new[] { "generated_at", "expires_at" })
            if (
                token.GetProperty(field).GetString() is { } stamp
                && !DateTimeOffset.TryParse(stamp, out _)
            )
                throw new InvalidOperationException(
                    $"CI token {field} is not a parsable timestamp"
                );
        await PersonalTokenHttpTestSupport.RunAsync(client, admin, key, projectId);
        await AnalysisHttpTestSupport.RunAsync(client, ci, key, admin);
        await Call(client, "GET", $"/api/v1/projects/{projectId}/branches", token: admin);
        await AdminHttpTestSupport.RunAsync(client, admin, key);
        await Call(client, "DELETE", $"/api/v1/projects/{projectId}", token: admin);
        Console.WriteLine(
            $"HTTP checks passed, including {checkedRoutes} anonymous route rejections."
        );
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
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
