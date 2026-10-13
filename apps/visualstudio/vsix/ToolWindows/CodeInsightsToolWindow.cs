using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using CodeInsights.VisualStudio.Commands;
using CodeInsights.VisualStudio.Runtime;

namespace CodeInsights.VisualStudio.ToolWindows;

[VisualStudioContribution]
internal sealed class CodeInsightsToolWindow : ToolWindow
{
    private readonly CodeInsightsRuntime _runtime;
    private CodeInsightsToolWindowData? _data;

    public CodeInsightsToolWindow(CodeInsightsRuntime runtime)
    {
        Title = "CodeInsights";
        _runtime = runtime;
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
        AllowAutoCreation = true,
        Toolbar = new ToolWindowToolbar(Toolbar),
    };

    [VisualStudioContribution]
    private static ToolbarConfiguration Toolbar => new("%CodeInsights.Toolbar%")
    {
        Children =
        [
            ToolbarChild.Command<RefreshCommand>(),
            ToolbarChild.Command<OpenHubCommand>(),
            ToolbarChild.Command<AnalyzeChangesCommand>(),
            ToolbarChild.Command<FilterIssuesCommand>(),
        ],
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        _data = new CodeInsightsToolWindowData(this.Extensibility, _runtime);
        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(new CodeInsightsToolWindowControl(_data));
    }
}
