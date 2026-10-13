using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeInsights.Api;
using CodeInsights.Api.Auth;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

public sealed class EntraGraphTests
{

private static async Task Denied(Func<Task> action, int status)
    {
        try
        {
            await action();
        }
        catch (ApiException exception) when (exception.StatusCode == status)
        {
            Assert.True(
                !Convert
                    .ToString(exception.Detail)!
                    .Contains("synthetic-secret", StringComparison.Ordinal),
                "Graph errors never expose the client secret"
            );
            return;
        }
        throw new InvalidOperationException($"Expected HTTP {status}");
    }

    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        Assert.True(
            EntraGraphTokenProvider.GraphScopes.SequenceEqual([
                "https://graph.microsoft.com/.default",
            ]),
            "Graph application token uses only the .default scope"
        );
        await Denied(
            () =>
            {
                EntraGraphService.EncodeCursor("https://evil.example/v1.0/users", "users");
                return Task.CompletedTask;
            },
            422
        );

        var connection = Environment.GetEnvironmentVariable("CODEINSIGHTS_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Console.WriteLine(
                "Graph scope/continuation passed; HTTP Graph checks require CODEINSIGHTS_TEST_DB."
            );
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        await using var db = new AppDbContext(options);
        await new DatabaseMigrator(db).MigrateAsync();
        var provider = await db.Set<AuthProviderConfig>()
            .FirstOrDefaultAsync(x => x.ProviderType == "azure-ad");
        if (provider is null)
        {
            provider = new AuthProviderConfig
            {
                Id = Guid.NewGuid().ToString(),
                ProviderType = "azure-ad",
                Name = "Entra",
            };
            db.Add(provider);
        }
        provider.Enabled = true;
        provider.Config = JsonSerializer.Serialize(
            new
            {
                tenant_id = "synthetic-tenant",
                client_id = "synthetic-client",
                client_secret = "synthetic-secret",
            }
        );
        await db.SaveChangesAsync();

        var handler = new QueuedHandler();
        var tokens = new RecordingTokenProvider();
        var service = new EntraGraphService(db, new SingleClientFactory(handler), tokens);
        var userId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        var hiddenId = Guid.NewGuid().ToString();

        var mixedHandler = new RoutingHandler(request =>
        {
            var isUsers = request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal);
            var values = Enumerable.Range(0, 8)
                .Select(index =>
                    isUsers
                        ? (object)new
                        {
                            id = Guid.NewGuid().ToString(),
                            givenName = index == 0 ? "Ada" : $"User{index}",
                            surname = index == 0 ? "Lovelace" : "Example",
                            displayName = $"Ignored user {index}",
                            mail = $"user{index}@example.test",
                            accountEnabled = true,
                        }
                        : new
                        {
                            id = Guid.NewGuid().ToString(),
                            displayName = $"Group {index}",
                            mail = (string?)null,
                            visibility = index == 7 ? "HiddenMembership" : "Public",
                        }
                )
                .ToArray();
            return Json(HttpStatusCode.OK, new { value = values });
        });
        var mixedTokens = new RecordingTokenProvider();
        var mixedService = new EntraGraphService(
            db,
            new SingleClientFactory(mixedHandler),
            mixedTokens
        );
        await Denied(() => mixedService.SearchAsync("all", "A", null), 422);
        await Denied(
            () => mixedService.SearchAsync("all", "Ad", EntraGraphService.EncodeCursor(
                "https://graph.microsoft.com/v1.0/users?$skiptoken=safe",
                "users"
            )),
            422
        );
        var mixed = await mixedService.SearchAsync(null, "Ad", null);
        Assert.True(
            mixed.Items.Count == 12
                && mixed.NextCursor is null
                && mixed.Items.Any(item => item.DisplayName == "Ada Lovelace")
                && mixed.Items.All(item => !item.HiddenMembership),
            "Mixed directory search formats user names, excludes hidden groups and caps suggestions"
        );
        Assert.True(
            mixed.Items.Select(item => item.DisplayName).SequenceEqual(
                mixed.Items.Select(item => item.DisplayName).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            ),
            "Mixed directory suggestions are sorted by their visible name"
        );
        Assert.True(
            mixedHandler.Uris.Count == 2
                && mixedHandler.Uris.All(uri => uri.Query.Contains("%24top=8", StringComparison.OrdinalIgnoreCase) || uri.Query.Contains("$top=8", StringComparison.OrdinalIgnoreCase))
                && mixedHandler.Uris.Select(uri => uri.AbsolutePath).ToHashSet().SetEquals([
                    "/v1.0/users",
                    "/v1.0/groups",
                ])
                && mixedHandler.Authorization.All(value => value == "Bearer synthetic-token")
                && mixedTokens.Calls == 1,
            "Mixed search sends two bounded parallel requests with one shared application token"
        );
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await mixedService.SearchAsync("all", "Ad", null, cancelled.Token);
                throw new InvalidOperationException("Cancelled directory search must stop");
            }
            catch (OperationCanceledException)
            {
                // Expected: the caller cancellation is not converted into a Graph availability error.
            }
        }

        var next = "https://graph.microsoft.com/v1.0/users?$skiptoken=safe";
        handler.Enqueue(
            Json(
                HttpStatusCode.OK,
                new
                {
                    value = new[]
                    {
                        new
                        {
                            id = userId,
                            displayName = "Ada",
                            mail = "ada@example.test",
                            accountEnabled = true,
                        },
                    },
                    @odata_nextLink = next,
                },
                next
            )
        );
        var first = await service.SearchAsync("user", "Ad", null);
        Assert.True(
            first.Items.Single().ObjectId == userId && first.NextCursor is not null,
            "Directory user search returns a canonical principal and opaque cursor"
        );
        handler.Enqueue(Json(HttpStatusCode.OK, new { value = Array.Empty<object>() }));
        var second = await service.SearchAsync("user", "Ad", first.NextCursor);
        Assert.True(
            second.Items.Count == 0 && handler.Uris.Last().ToString() == next,
            "Directory cursor continues only on the validated Graph URL"
        );
        Assert.True(
            handler.Authorization.All(value => value == "Bearer synthetic-token"),
            "Every Graph request carries the application Bearer token"
        );
        Assert.True(
            tokens.Configuration?.ClientSecret == "synthetic-secret",
            "Graph reads the existing Entra provider configuration"
        );

        handler.Enqueue(
            Json(
                HttpStatusCode.OK,
                new
                {
                    id = userId,
                    displayName = "Canonical Ada",
                    mail = "canonical@example.test",
                    accountEnabled = true,
                }
            )
        );
        var canonical = await service.GetPrincipalAsync("entra-user", userId);
        Assert.True(
            canonical.DisplayName == "Canonical Ada" && canonical.Email == "canonical@example.test",
            "Administrative assignment reloads canonical user data from Graph"
        );

        var membershipNext =
            $"https://graph.microsoft.com/v1.0/users/{userId}/transitiveMemberOf/microsoft.graph.group?$skiptoken=2";
        handler.Enqueue(
            Json(
                HttpStatusCode.OK,
                new
                {
                    value = new object[]
                    {
                        new { id = groupId, visibility = "Public" },
                        new { id = hiddenId, visibility = "HiddenMembership" },
                    },
                },
                membershipNext
            )
        );
        handler.Enqueue(
            Json(
                HttpStatusCode.OK,
                new
                {
                    value = new[]
                    {
                        new { id = Guid.NewGuid().ToString(), visibility = "Private" },
                    },
                }
            )
        );
        var memberships = await service.GetTransitiveGroupIdsAsync(userId);
        Assert.True(
            memberships.Contains(groupId)
                && !memberships.Contains(hiddenId)
                && memberships.Count == 2,
            "Transitive groups paginate and hidden-membership groups fail closed"
        );

        var cycle =
            $"https://graph.microsoft.com/v1.0/users/{userId}/transitiveMemberOf/microsoft.graph.group?$skiptoken=cycle";
        handler.Enqueue(Json(HttpStatusCode.OK, new { value = Array.Empty<object>() }, cycle));
        handler.Enqueue(Json(HttpStatusCode.OK, new { value = Array.Empty<object>() }, cycle));
        await Denied(() => service.GetTransitiveGroupIdsAsync(userId), 502);

        handler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                },
            }
        );
        var photo = await service.GetPhotoAsync(userId);
        Assert.True(
            photo?.Content.SequenceEqual(new byte[] { 1, 2, 3 }) == true
                && photo.ContentType == "image/jpeg",
            "Profile photo accepts a bounded image payload"
        );
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.True(
            await service.GetPhotoAsync(userId) is null,
            "Missing Graph photo becomes an API fallback"
        );
        handler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not an image"),
            }
        );
        await Denied(() => service.GetPhotoAsync(userId), 502);
        handler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
                },
            }
        );
        await Denied(() => service.GetPhotoAsync(userId), 502);
        handler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("synthetic-secret"),
            }
        );
        await Denied(() => service.GetPrincipalAsync("entra-user", userId), 502);
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        object value,
        string? next = null
    )
    {
        var json = JsonSerializer.Serialize(value);
        if (next is not null)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            values.Remove("odata_nextLink");
            values["@odata.nextLink"] = JsonSerializer.SerializeToElement(next);
            json = JsonSerializer.Serialize(values);
        }
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingTokenProvider : IEntraGraphTokenProvider
    {
        public EntraGraphConfiguration? Configuration { get; private set; }
        public int Calls { get; private set; }

        public ValueTask<string> GetTokenAsync(
            EntraGraphConfiguration configuration,
            CancellationToken ct = default
        )
        {
            Configuration = configuration;
            Calls++;
            return ValueTask.FromResult("synthetic-token");
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new();
        public List<Uri> Uris { get; } = [];
        public List<string> Authorization { get; } = [];

        public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString() ?? "");
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource bothRequestsStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int started;
        public List<Uri> Uris { get; } = [];
        public List<string> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            lock (gate)
            {
                Uris.Add(request.RequestUri!);
                Authorization.Add(request.Headers.Authorization?.ToString() ?? "");
            }
            if (Interlocked.Increment(ref started) == 2)
                bothRequestsStarted.TrySetResult();
            await bothRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return response(request);
        }
    }
}
