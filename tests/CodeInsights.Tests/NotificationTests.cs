using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Nodes;
using CodeInsights.Api.Endpoints;
using CodeInsights.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class NotificationTests
{
    [Fact]
    public Task All() => RunAsync();

    internal static async Task RunAsync()
    {
        var handler = new CaptureHandler();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new NotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ClientFactory(handler),
            NullLogger<NotificationService>.Instance
        );
        Assert.True(
            await service.SendTestAsync(
                "webhook",
                new JsonObject { ["url"] = "https://example.invalid/hook" }
            ) == "ok",
            "Webhook succeeds"
        );
        Assert.True(
            handler.Payload?["quality_gate"]?.ToString() == "fail"
                && handler.Payload?["event"]?.ToString() == "quality_gate.fail",
            "Generic webhook event contract"
        );
        Assert.True(handler.UserAgent == "SAST-Platform/1.0", "Webhook agent contract");
        Assert.True(
            await service.SendTestAsync(
                "webhook",
                new JsonObject { ["url"] = "https://example.logic.azure.com/workflows/test" }
            ) == "ok",
            "Teams webhook succeeds"
        );
        var card = handler.Payload?["attachments"]?[0]?["content"];
        Assert.True(
            card?["version"]?.ToString() == "1.4"
                && card?["body"]?[0]?["color"]?.ToString() == "Attention",
            "Teams failed gate presentation"
        );
        Assert.True(
            card?["body"]?[1]?["facts"]?[2]?["value"]?.ToString() == "abc12345",
            "Teams commit preview"
        );
        handler.Status = HttpStatusCode.BadGateway;
        Assert.True(
            await service.SendTestAsync(
                "webhook",
                new JsonObject { ["url"] = "https://example.invalid/hook" }
            ) != "ok",
            "HTTP failure is reported"
        );
        Assert.True(
            await service.SendTestAsync("webhook", new JsonObject()) == "Webhook URL is empty",
            "Empty URL rejected"
        );
        var context = new JsonObject
        {
            ["project_key"] = "<script>alert(1)</script>",
            ["branch"] = "<img>",
            ["commit_sha"] = "1234567890",
            ["gate_failures"] = new JsonArray("<b>untrusted</b>"),
        };
        var body = NotificationService.BuildEmailBody("fail", context);
        Assert.True(
            !body.Contains("<script>")
                && body.Contains("&lt;script&gt;")
                && body.Contains("&lt;b&gt;untrusted&lt;/b&gt;"),
            "Untrusted notification content is HTML encoded"
        );
        var pass = NotificationService.BuildTeamsPayload("pass", context);
        Assert.True(
            pass["attachments"]?[0]?["content"]?["body"]?[0]?["color"]?.ToString() == "Good",
            "Teams passing gate presentation"
        );
        var invalid = new TokenGenerateRequest { TokenName = "CI", TtlDays = 0 };
        Assert.True(
            !Validator.TryValidateObject(invalid, new ValidationContext(invalid), [], true),
            "Token TTL validation"
        );
        Console.WriteLine("Notification checks passed (fake HTTP, Teams, HTML, token validation).");
    }

private sealed class ClientFactory(CaptureHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonNode? Payload { get; private set; }
        public string? UserAgent { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            UserAgent = request.Headers.UserAgent.ToString();
            return new HttpResponseMessage(Status);
        }
    }
}
