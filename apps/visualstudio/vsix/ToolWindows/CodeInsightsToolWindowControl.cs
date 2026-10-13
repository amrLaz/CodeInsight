using Microsoft.VisualStudio.Extensibility.UI;

namespace CodeInsights.VisualStudio.ToolWindows;

internal sealed class CodeInsightsToolWindowControl : RemoteUserControl
{
    public CodeInsightsToolWindowControl(object? dataContext)
        : base(dataContext)
    {
    }
}
