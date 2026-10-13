using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace CodeInsights.VisualStudio.Commands;

[VisualStudioContribution]
internal sealed class RefreshCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public RefreshCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.Refresh%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var snapshot = await _runtime.RefreshAsync(cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(snapshot.Status.Text, PromptOptions.OK, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class OpenHubCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public OpenHubCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.OpenHub%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.OpenWebSite, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var url = await _runtime.HubUrlAsync("report", null, cancellationToken);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}

[VisualStudioContribution]
internal sealed class AnalyzeChangesCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public AnalyzeChangesCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.Analyze%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.TestRun, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var snapshot = await _runtime.AnalyzeAsync(cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(snapshot.Notice ?? snapshot.Status.Text, PromptOptions.OK, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class InstallHookCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public InstallHookCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.InstallHook%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var confirmed = await this.Extensibility.Shell().ShowPromptAsync(
            "CodeInsights peut écrire un crochet Git de rappel. Il n'empêche pas le push et n'est jamais installé tout seul.",
            PromptOptions.OKCancel with { Title = "CodeInsights" },
            cancellationToken);
        if (!confirmed) return;
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var message = await _runtime.InstallHookAsync(cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(message, PromptOptions.OK, cancellationToken);
    }
}
