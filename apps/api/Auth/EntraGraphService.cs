using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CodeInsights.Api.Auth;

public sealed record EntraGraphConfiguration(string TenantId, string ClientId, string ClientSecret);

public sealed record EntraDirectoryPrincipal(
    string Kind,
    string ObjectId,
    string DisplayName,
    string? Email,
    bool IsActive,
    bool HiddenMembership
);

public sealed record EntraDirectoryPage(
    IReadOnlyList<EntraDirectoryPrincipal> Items,
    string? NextCursor
);

public sealed record EntraPhoto(byte[] Content, string ContentType);

public interface IEntraGraphTokenProvider
{
    ValueTask<string> GetTokenAsync(
        EntraGraphConfiguration configuration,
        CancellationToken ct = default
    );
}

public sealed class EntraGraphTokenProvider(IMemoryCache cache) : IEntraGraphTokenProvider
{
    internal static readonly string[] GraphScopes = [
        "https://graph.microsoft.com/.default",
    ];
    private static readonly TokenRequestContext GraphContext = new(GraphScopes);

    public async ValueTask<string> GetTokenAsync(
        EntraGraphConfiguration configuration,
        CancellationToken ct = default
    )
    {
        var cacheKey =
            "entra-graph-credential:"
            + AuthService.Sha256(
                $"{configuration.TenantId}\n{configuration.ClientId}\n{configuration.ClientSecret}"
            );
        var credential = cache.GetOrCreate(
            cacheKey,
            entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromHours(1);
                return new ClientSecretCredential(
                    configuration.TenantId,
                    configuration.ClientId,
                    configuration.ClientSecret
                );
            }
        )!;
        return (await credential.GetTokenAsync(GraphContext, ct)).Token;
    }
}

public interface IEntraGraphService
{
    Task<EntraDirectoryPage> SearchAsync(
        string? kind,
        string search,
        string? cursor,
        CancellationToken ct = default
    );

    Task<EntraDirectoryPrincipal> GetPrincipalAsync(
        string kind,
        string objectId,
        CancellationToken ct = default
    );

    Task<HashSet<string>> GetTransitiveGroupIdsAsync(
        string objectId,
        CancellationToken ct = default
    );

    Task<EntraPhoto?> GetPhotoAsync(string objectId, CancellationToken ct = default);
}

public sealed class EntraGraphService(
    AppDbContext db,
    IHttpClientFactory clients,
    IEntraGraphTokenProvider tokens
) : IEntraGraphService
{
    private const string GraphOrigin = "https://graph.microsoft.com";
    private const int MaxPhotoBytes = 1024 * 1024;
    private const int MaxContinuationPages = 100;

    public async Task<EntraDirectoryPage> SearchAsync(
        string? kind,
        string search,
        string? cursor,
        CancellationToken ct = default
    )
    {
        var term = search.Trim();
        if (term.Length is < 2 or > 100)
            throw new ApiException(422, "Directory search must contain 2 to 100 characters");

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "all" : kind;
        if (normalizedKind == "all")
        {
            if (!string.IsNullOrWhiteSpace(cursor))
                throw new ApiException(422, "Mixed directory search does not support a cursor");
            try
            {
                var configuration = await ConfigurationAsync(ct);
                var token = await tokens.GetTokenAsync(configuration, ct);
                var users = SearchResourceAsync("users", SearchUrl("users", term, 8), token, ct);
                var groups = SearchResourceAsync("groups", SearchUrl("groups", term, 8), token, ct);
                await Task.WhenAll(users, groups);
                return new EntraDirectoryPage(
                    users.Result.Items
                        .Concat(groups.Result.Items.Where(item => !item.HiddenMembership))
                        .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.Kind, StringComparer.Ordinal)
                        .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                        .Take(12)
                        .ToArray(),
                    null
                );
            }
            catch (Exception exception)
                when (exception is HttpRequestException or AuthenticationFailedException
                    || exception is TaskCanceledException && !ct.IsCancellationRequested
                )
            {
                throw new ApiException(502, "Microsoft Graph is unavailable");
            }
        }

        var resource = Resource(normalizedKind);
        var url = string.IsNullOrWhiteSpace(cursor)
            ? SearchUrl(resource, term, 25)
            : DecodeCursor(cursor, resource);
        var page = await SearchResourceAsync(resource, url, await AccessTokenAsync(ct), ct);
        return new EntraDirectoryPage(
            page.Items,
            page.NextLink is null ? null : EncodeCursor(page.NextLink, resource)
        );
    }

    private async Task<(EntraDirectoryPrincipal[] Items, string? NextLink)> SearchResourceAsync(
        string resource,
        string url,
        string token,
        CancellationToken ct
    )
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            url,
            token,
            ct,
            consistencyLevel: true
        );
        await EnsureGraphSuccessAsync(response, ct);
        try
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = payload
                .RootElement.GetProperty("value")
                .EnumerateArray()
                .Select(item => Principal(resource, item))
                .ToArray();
            var next = payload.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
            return (items, next);
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ApiException(502, "Microsoft Graph returned an invalid directory response");
        }
    }

    public async Task<EntraDirectoryPrincipal> GetPrincipalAsync(
        string kind,
        string objectId,
        CancellationToken ct = default
    )
    {
        var resource = Resource(kind);
        if (!Guid.TryParse(objectId, out _))
            throw new ApiException(422, "Invalid Entra object ID");
        var select =
            resource == "users"
                ? "id,givenName,surname,displayName,mail,userPrincipalName,accountEnabled"
                : "id,displayName,mail,visibility";
        var url =
            $"{GraphOrigin}/v1.0/{resource}/{Uri.EscapeDataString(objectId)}?$select={select}";
        using var response = await SendAsync(HttpMethod.Get, url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ApiException(404, "Entra directory principal not found");
        await EnsureGraphSuccessAsync(response, ct);
        try
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Principal(resource, payload.RootElement);
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ApiException(502, "Microsoft Graph returned an invalid directory response");
        }
    }

    public async Task<HashSet<string>> GetTransitiveGroupIdsAsync(
        string objectId,
        CancellationToken ct = default
    )
    {
        if (!Guid.TryParse(objectId, out _))
            throw new ApiException(422, "Invalid Entra object ID");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? url =
            $"{GraphOrigin}/v1.0/users/{Uri.EscapeDataString(objectId)}/transitiveMemberOf/microsoft.graph.group?$select=id,visibility&$top=999";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pageCount = 0;
        while (url is not null)
        {
            if (++pageCount > MaxContinuationPages || !visited.Add(url))
                throw new ApiException(502, "Microsoft Graph returned an invalid continuation chain");
            ValidateGraphUrl(
                url,
                $"/v1.0/users/{objectId}/transitiveMemberOf/microsoft.graph.group"
            );
            using var response = await SendAsync(HttpMethod.Get, url, ct, consistencyLevel: true);
            await EnsureGraphSuccessAsync(response, ct);
            try
            {
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in payload.RootElement.GetProperty("value").EnumerateArray())
                {
                    var hidden =
                        item.TryGetProperty("visibility", out var visibility)
                        && string.Equals(
                            visibility.GetString(),
                            "HiddenMembership",
                            StringComparison.Ordinal
                        );
                    if (
                        !hidden
                        && item.TryGetProperty("id", out var id)
                        && id.GetString() is { Length: > 0 } value
                    )
                        result.Add(value);
                }
                url = payload.RootElement.TryGetProperty("@odata.nextLink", out var next)
                    ? next.GetString()
                    : null;
            }
            catch (Exception exception)
                when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new ApiException(
                    502,
                    "Microsoft Graph returned an invalid membership response"
                );
            }
        }
        return result;
    }

    public async Task<EntraPhoto?> GetPhotoAsync(string objectId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(objectId, out _))
            throw new ApiException(422, "Invalid Entra object ID");
        var url = $"{GraphOrigin}/v1.0/users/{Uri.EscapeDataString(objectId)}/photos/48x48/$value";
        using var response = await SendAsync(
            HttpMethod.Get,
            url,
            ct,
            completion: HttpCompletionOption.ResponseHeadersRead
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureGraphSuccessAsync(response, ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(502, "Microsoft Graph returned an invalid profile photo");
        if (response.Content.Headers.ContentLength is > MaxPhotoBytes)
            throw new ApiException(502, "Microsoft Graph profile photo is too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return new EntraPhoto(await ReadBoundedAsync(stream, MaxPhotoBytes, ct), contentType);
    }

    internal static string EncodeCursor(string nextLink, string resource)
    {
        ValidateGraphUrl(nextLink, $"/v1.0/{resource}");
        return AuthService.Base64Url(Encoding.UTF8.GetBytes(nextLink));
    }

    internal static string DecodeCursor(string cursor, string resource)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            ValidateGraphUrl(url, $"/v1.0/{resource}");
            return url;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ApiException(422, "Invalid directory cursor");
        }
    }

    private static string Resource(string kind) =>
        kind switch
        {
            "user" or "entra-user" => "users",
            "group" or "entra-group" => "groups",
            _ => throw new ApiException(422, "Directory principal kind must be user or group"),
        };

    private static string SearchUrl(string resource, string term, int top)
    {
        var escaped = term.Replace("'", "''", StringComparison.Ordinal);
        var filter =
            resource == "users"
                ? $"startswith(displayName,'{escaped}') or startswith(mail,'{escaped}') or startswith(userPrincipalName,'{escaped}')"
                : $"startswith(displayName,'{escaped}') or startswith(mail,'{escaped}')";
        var select =
            resource == "users"
                ? "id,givenName,surname,displayName,mail,userPrincipalName,accountEnabled"
                : "id,displayName,mail,visibility";
        return $"{GraphOrigin}/v1.0/{resource}?$select={select}&$filter={Uri.EscapeDataString(filter)}&$count=true&$top={top}";
    }

    private static EntraDirectoryPrincipal Principal(string resource, JsonElement item)
    {
        static string? Text(JsonElement value, string property) =>
            value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
        var objectId =
            Text(item, "id")
            ?? throw new ApiException(502, "Microsoft Graph returned a principal without an ID");
        var givenName = Text(item, "givenName")?.Trim();
        var surname = Text(item, "surname")?.Trim();
        var fullName = string.Join(
            ' ',
            new[] { givenName, surname }.Where(value => !string.IsNullOrWhiteSpace(value))
        );
        var displayName = resource == "users" && fullName.Length > 0
            ? fullName
            : Text(item, "displayName") ?? objectId;
        var email = Text(item, "mail") ?? Text(item, "userPrincipalName");
        var active =
            !item.TryGetProperty("accountEnabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.False;
        return new EntraDirectoryPrincipal(
            resource == "users" ? "entra-user" : "entra-group",
            objectId,
            displayName,
            email,
            active,
            string.Equals(Text(item, "visibility"), "HiddenMembership", StringComparison.Ordinal)
        );
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        CancellationToken ct,
        bool consistencyLevel = false,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead
    )
    {
        try
        {
            return await SendAuthorizedAsync(
                method,
                url,
                await AccessTokenAsync(ct),
                ct,
                consistencyLevel,
                completion
            );
        }
        catch (Exception exception)
            when (exception is HttpRequestException or AuthenticationFailedException
                || exception is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            throw new ApiException(502, "Microsoft Graph is unavailable");
        }
    }

    private async ValueTask<string> AccessTokenAsync(CancellationToken ct)
    {
        try
        {
            return await tokens.GetTokenAsync(await ConfigurationAsync(ct), ct);
        }
        catch (AuthenticationFailedException)
        {
            throw new ApiException(502, "Microsoft Graph is unavailable");
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string url,
        string token,
        CancellationToken ct,
        bool consistencyLevel = false,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead
    )
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (consistencyLevel || url.Contains("$count=true", StringComparison.Ordinal))
                request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
            return await clients.CreateClient("entra-graph").SendAsync(request, completion, ct);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                || exception is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            throw new ApiException(502, "Microsoft Graph is unavailable");
        }
    }

    private async Task<EntraGraphConfiguration> ConfigurationAsync(CancellationToken ct)
    {
        var provider = await db.Set<AuthProviderConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderType == "azure-ad", ct);
        if (provider is null || !provider.Enabled)
            throw new ApiException(503, "Entra ID authentication is not enabled");
        Dictionary<string, JsonElement> config;
        try
        {
            config =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(provider.Config) ?? [];
        }
        catch (JsonException)
        {
            config = [];
        }
        static string Value(IReadOnlyDictionary<string, JsonElement> values, string key) =>
            values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        var configuration = new EntraGraphConfiguration(
            Value(config, "tenant_id"),
            Value(config, "client_id"),
            Value(config, "client_secret")
        );
        if (
            string.IsNullOrWhiteSpace(configuration.TenantId)
            || string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.ClientSecret)
        )
            throw new ApiException(503, "Entra ID configuration is incomplete");
        return configuration;
    }

    private static void ValidateGraphUrl(string url, string expectedPath)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
        )
            throw new ApiException(422, "Invalid Microsoft Graph continuation URL");
    }

    private static async Task EnsureGraphSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        if (response.IsSuccessStatusCode)
            return;
        _ = await response.Content.ReadAsStringAsync(ct);
        throw new ApiException(502, "Microsoft Graph request failed");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken ct
    )
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new ApiException(502, "Microsoft Graph profile photo is too large");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
