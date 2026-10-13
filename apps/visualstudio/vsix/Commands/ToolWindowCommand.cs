using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using CodeInsights.VisualStudio.ToolWindows;

namespace CodeInsights.VisualStudio.Commands;

[VisualStudioContribution]
internal sealed class ShowToolWindowCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.ShowWindow%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu, CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.StatusInformation, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<CodeInsightsToolWindow>(activate: true, cancellationToken);
    }
}
