using System.Text.Json;
using System.Text.Json.Nodes;
using CodeInsights.VisualStudio.Auth;
using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace CodeInsights.VisualStudio.Commands;

[VisualStudioContribution]
internal sealed class ConfigureMcpCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;
    private readonly TokenStore _tokens;

    public ConfigureMcpCommand(CodeInsightsRuntime runtime, TokenStore tokens)
    {
        _runtime = runtime;
        _tokens = tokens;
    }

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.ConfigureMcp%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var server = _runtime.McpServerPath;
        if (server is null)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                "Le serveur MCP n'est pas embarqué dans cette installation. Utiliser le paquet npm @upcoop/codeinsights-mcp.",
                PromptOptions.OK,
                cancellationToken);
            return;
        }

        var confirmed = await this.Extensibility.Shell().ShowPromptAsync(
            "Écrire le serveur CodeInsights dans .mcp.json du profil utilisateur ? Le jeton y est recopié : ne pas versionner ce fichier.",
            PromptOptions.OKCancel with { Title = "CodeInsights" },
            cancellationToken);
        if (!confirmed) return;

        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcp.json");
        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var servers = root["servers"] as JsonObject ?? new JsonObject();
        var env = new JsonObject { ["CODEINSIGHTS_URL"] = _runtime.LastSettings.ServerUrl };
        var token = _tokens.Get();
        if (!string.IsNullOrEmpty(token)) env["CODEINSIGHTS_TOKEN"] = token;
        servers["codeinsights"] = new JsonObject
        {
            ["command"] = "node",
            ["args"] = new JsonArray(server),
            ["env"] = env,
        };
        root["servers"] = servers;
        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync($"Serveur MCP écrit dans {path}.", PromptOptions.OK, cancellationToken);
    }
}
