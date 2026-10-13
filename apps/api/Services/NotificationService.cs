using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeInsights.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Services;

public sealed class NotificationService(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    ILogger<NotificationService> logger
)
{
    /// <summary>Dispatches committed gate events using an independent database scope.</summary>
    public void Dispatch(string qualityGate, Dictionary<string, object?> context)
    {
        // ponytail: delivery runs in process; durable retries require an outbox.
        var snapshot = JsonSerializer.SerializeToNode(context, ApiJson.Options)!.AsObject();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var channels = await db.Set<NotificationChannel>()
                    .Where(x => x.Enabled)
                    .ToListAsync();
                foreach (var channel in channels)
                {
                    if (
                        !(
                            qualityGate == "fail" && channel.OnFail
                            || qualityGate == "pass" && channel.OnPass
                        )
                    )
                        continue;
                    try
                    {
                        await SendAsync(
                            channel.ChannelType,
                            JsonNode.Parse(channel.Config)!.AsObject(),
                            qualityGate,
                            snapshot,
                            CancellationToken.None
                        );
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Notification channel {ChannelId} failed",
                            channel.Id
                        );
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Notification dispatch failed");
            }
        });
    }

    public async Task<string> SendTestAsync(
        string channelType,
        JsonObject config,
        CancellationToken ct = default
    )
    {
        var context = new JsonObject
        {
            ["project_key"] = "test-project",
            ["branch"] = "main",
            ["commit_sha"] = "abc12345",
            ["vulnerabilities"] = 3,
            ["bugs"] = 1,
            ["code_smells"] = 12,
            ["gate_failures"] = new JsonArray("Too many vulnerabilities (3 > 0)"),
            ["analysis_id"] = "test-00000000",
        };
        try
        {
            await SendAsync(channelType, config, "fail", context, ct);
            return "ok";
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            return exception.Message;
        }
    }

    public async Task SendAsync(
        string type,
        JsonObject config,
        string gate,
        JsonObject context,
        CancellationToken ct
    )
    {
        if (type == "webhook")
        {
            var url = Text(config, "url");
            if (url.Length == 0)
                throw new InvalidOperationException("Webhook URL is empty");
            var isTeams = IsTeams(url);
            var payload = isTeams
                ? BuildTeamsPayload(gate, context)
                : (JsonObject)context.DeepClone();
            if (!isTeams)
            {
                payload["event"] = "quality_gate." + gate;
                payload["quality_gate"] = gate;
            }
            await PostAsync(url, payload, ct);
        }
        else if (type == "email")
        {
            var label = gate == "fail" ? "FAILED" : "PASSED";
            var icon = gate == "fail" ? "❌" : "✅";
            await MailAsync(
                config,
                $"[CodeInsights] Quality Gate {label} {icon} — {Text(context, "project_key")} / {Text(context, "branch")}",
                BuildEmailBody(gate, context),
                ct
            );
        }
    }

    /// <summary>Sends one queued breach digest: resource is <c>project|stamp ticks|channel</c>.</summary>
    public async Task SendSlaBreachAsync(string resourceId, CancellationToken ct)
    {
        var parts = resourceId.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var ticks))
            throw new InvalidDataException("Invalid SLA notification resource");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channel = await db.Set<NotificationChannel>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == parts[2], ct);
        if (channel is not { Enabled: true, OnSlaBreach: true })
            return;
        var stamp = new DateTime(ticks, DateTimeKind.Utc);
        var findings = await db.Set<DurableFinding>().AsNoTracking()
            .Where(x => x.ProjectKey == parts[0] && x.SlaNotifiedAt == stamp)
            .OrderBy(x => x.SlaDueAt).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Branch, x.Severity, x.Tool, x.RuleId, x.Path, x.Line, x.SlaDueAt })
            .ToListAsync(ct);
        if (findings.Count == 0)
            return;
        var context = new JsonObject
        {
            ["event"] = "sla.breached",
            ["project_key"] = parts[0],
            ["branch"] = findings[0].Branch,
            ["breached_count"] = findings.Count,
            ["findings"] = JsonSerializer.SerializeToNode(findings.Take(SlaDigestLimit), ApiJson.Options),
        };
        var config = JsonNode.Parse(channel.Config)!.AsObject();
        if (channel.ChannelType == "webhook")
        {
            var url = Text(config, "url");
            if (url.Length == 0)
                throw new InvalidOperationException("Webhook URL is empty");
            await PostAsync(url, IsTeams(url) ? BuildSlaTeamsPayload(context) : context, ct);
        }
        else if (channel.ChannelType == "email")
            await MailAsync(
                config,
                $"[CodeInsights] SLA breached ⏰ — {parts[0]} ({findings.Count} findings)",
                BuildSlaEmailBody(context),
                ct
            );
    }

    public const int SlaDigestLimit = 20;

    private static readonly string[] TeamsWebhookMarkers =
    [
        "webhook.office.com",
        "outlook.office.com/webhook",
        "logic.azure.com",
        "flow.microsoft.com",
        "azure.com/workflows",
    ];

    private static bool IsTeams(string url) =>
        TeamsWebhookMarkers.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase));

    private async Task PostAsync(string url, JsonObject payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = clients.CreateClient("notifications");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("SAST-Platform/1.0");
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task MailAsync(JsonObject config, string subject, string body, CancellationToken ct)
    {
        var host = Text(config, "smtp_host");
        var recipient = Text(config, "to_email");
        var user = Text(config, "smtp_user");
        var password = Text(config, "smtp_password");
        if (host.Length == 0)
            throw new InvalidOperationException("smtp_host is not configured");
        if (recipient.Length == 0)
            throw new InvalidOperationException("to_email is not configured");
        var port = config["smtp_port"] is null ? 587 : int.Parse(config["smtp_port"]!.ToString());
        using var message = new MailMessage(Text(config, "smtp_from", user), recipient)
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        using var smtp = new SmtpClient(host, port) { EnableSsl = true, Timeout = 15000 };
        if (user.Length > 0 && password.Length > 0)
            smtp.Credentials = new NetworkCredential(user, password);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await smtp.SendMailAsync(message, timeout.Token);
    }

    private static IEnumerable<string> SlaLines(JsonObject context) =>
        (context["findings"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(f =>
                $"{Text(f, "severity")} · {Text(f, "tool")}/{Text(f, "rule_id")} · {Text(f, "path", "no file")}{(f["line"] is null ? "" : ":" + Text(f, "line"))} · due {Text(f, "sla_due_at")}"
            );

    public static JsonObject BuildSlaTeamsPayload(JsonObject context)
    {
        var count = (int)context["breached_count"]!;
        var lines = SlaLines(context).ToList();
        var body = new JsonArray(
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = $"⏰ CodeInsights — {count} finding(s) past their remediation deadline",
                ["size"] = "Large",
                ["weight"] = "Bolder",
                ["color"] = "Attention",
                ["wrap"] = true,
            },
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray(
                    new JsonObject { ["title"] = "Project", ["value"] = Text(context, "project_key") },
                    new JsonObject { ["title"] = "Branch", ["value"] = Text(context, "branch") }
                ),
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = string.Join("\n", lines.Select(l => "• " + l))
                    + (count > lines.Count ? $"\n… and {count - lines.Count} more in CodeInsights" : ""),
                ["wrap"] = true,
            }
        );
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = body,
                    },
                }
            ),
        };
    }

    public static string BuildSlaEmailBody(JsonObject context)
    {
        var count = (int)context["breached_count"]!;
        var lines = SlaLines(context).ToList();
        var builder = new StringBuilder(
            $"<html><body style=\"font-family:Arial,sans-serif;max-width:600px;margin:auto\"><h2 style=\"color:#b42332\">⏰ {count} finding(s) past their remediation deadline</h2><p><strong>Project:</strong> {WebUtility.HtmlEncode(Text(context, "project_key"))} · <strong>Branch:</strong> {WebUtility.HtmlEncode(Text(context, "branch"))}</p><ul>"
        );
        foreach (var line in lines)
            builder.Append("<li>" + WebUtility.HtmlEncode(line) + "</li>");
        builder.Append("</ul>");
        if (count > lines.Count)
            builder.Append($"<p>… and {count - lines.Count} more in CodeInsights.</p>");
        return builder
            .Append("<p style=\"color:#666;font-size:12px;margin-top:24px\">Sent by CodeInsights</p></body></html>")
            .ToString();
    }

    public static JsonObject BuildTeamsPayload(string gate, JsonObject context)
    {
        var failed = gate == "fail";
        var facts = new JsonArray();
        foreach (
            var (title, key) in new[]
            {
                ("Project", "project_key"),
                ("Branch", "branch"),
                ("Commit", "commit_sha"),
                ("Vulnerabilities", "vulnerabilities"),
                ("Bugs", "bugs"),
                ("Code smells", "code_smells"),
            }
        )
        {
            var value = Text(
                context,
                key,
                key is "vulnerabilities" or "bugs" or "code_smells" ? "0" : ""
            );
            if (key == "commit_sha")
                value = value[..Math.Min(8, value.Length)];
            facts.Add(new JsonObject { ["title"] = title, ["value"] = value });
        }
        var body = new JsonArray(
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] =
                    $"{(failed ? "🔴" : "🟢")} CodeInsights — Quality Gate {(failed ? "FAILED" : "PASSED")}",
                ["size"] = "Large",
                ["weight"] = "Bolder",
                ["color"] = failed ? "Attention" : "Good",
                ["wrap"] = true,
            },
            new JsonObject { ["type"] = "FactSet", ["facts"] = facts }
        );
        if (context["gate_failures"] is JsonArray { Count: > 0 } failures)
            body.Add(
                new JsonObject
                {
                    ["type"] = "TextBlock",
                    ["text"] =
                        "**Gate failures:**\n"
                        + string.Join("\n", failures.Take(8).Select(f => "• " + f)),
                    ["wrap"] = true,
                    ["color"] = "Attention",
                    ["spacing"] = "Medium",
                }
            );
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = body,
                    },
                }
            ),
        };
    }

    public static string BuildEmailBody(string gate, JsonObject context)
    {
        var builder = new StringBuilder(
            $"<html><body style=\"font-family:Arial,sans-serif;max-width:600px;margin:auto\"><h2 style=\"color:{(gate == "fail" ? "#e74c3c" : "#27ae60")}\">{(gate == "fail" ? "❌ Quality Gate FAILED" : "✅ Quality Gate PASSED")}</h2><table style=\"border-collapse:collapse;width:100%\">"
        );
        foreach (
            var (title, key) in new[]
            {
                ("Project", "project_key"),
                ("Branch", "branch"),
                ("Commit", "commit_sha"),
                ("Vulnerabilities", "vulnerabilities"),
                ("Bugs", "bugs"),
                ("Code smells", "code_smells"),
            }
        )
            builder.Append(
                $"<tr><td style=\"padding:6px;border:1px solid #ddd\"><strong>{title}</strong></td><td style=\"padding:6px;border:1px solid #ddd\">{WebUtility.HtmlEncode(Text(context, key, "0"))}</td></tr>"
            );
        builder.Append("</table>");
        if (context["gate_failures"] is JsonArray { Count: > 0 } failures)
            builder.Append(
                "<p><strong>Failures:</strong></p><ul>"
                    + string.Concat(
                        failures
                            .Take(10)
                            .Select(f => "<li>" + WebUtility.HtmlEncode(f?.ToString()) + "</li>")
                    )
                    + "</ul>"
            );
        return builder
            .Append(
                "<p style=\"color:#666;font-size:12px;margin-top:24px\">Sent by CodeInsights</p></body></html>"
            )
            .ToString();
    }

    private static string Text(JsonObject value, string key, string fallback = "") =>
        value[key]?.ToString() ?? fallback;
}
