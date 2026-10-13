using CodeInsights.VisualStudio.Host;
using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Helpers;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Range = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

namespace CodeInsights.VisualStudio.Editor;

internal sealed class DiagnosticsService : DisposableObject
{
    private readonly CodeInsightsRuntime _runtime;
    private readonly VisualStudioExtensibility _extensibility;
    private DiagnosticsReporter? _reporter;

    public DiagnosticsService(CodeInsightsRuntime runtime, VisualStudioExtensibility extensibility)
    {
        _runtime = runtime;
        _extensibility = extensibility;
    }

    public async Task PublishFileAsync(Uri documentUri, CancellationToken cancellationToken)
    {
        if (!documentUri.IsFile) return;
        _reporter ??= _extensibility.Languages().GetDiagnosticsReporter(nameof(CodeInsightsExtension));
        var context = await _runtime.FileContextAsync(documentUri.LocalPath, cancellationToken);
        await _reporter.ClearDiagnosticsAsync(documentUri, cancellationToken);
        var diagnostics = context.Diagnostics.Select(ToDiagnostic).ToList();
        if (diagnostics.Count > 0) await _reporter.ReportDiagnosticsAsync(diagnostics, cancellationToken);
    }

    public async Task ClearAsync(Uri documentUri, CancellationToken cancellationToken)
    {
        if (_reporter is null) return;
        await _reporter.ClearDiagnosticsAsync(documentUri, cancellationToken);
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing) _reporter?.Dispose();
        base.Dispose(isDisposing);
    }

    private static DocumentDiagnostic ToDiagnostic(FileDiagnosticDto item)
    {
        var line = Math.Max(item.Line - 1, 0);
        var column = item.Column is > 0 ? item.Column.Value - 1 : 0;
        var endColumn = item.EndColumn is > 0 ? item.EndColumn.Value - 1 : -1;
        var range = new Range(startLine: line, startColumn: column, endLine: line, endColumn: endColumn);
        return new DocumentDiagnostic(new Uri(item.Path), range, item.Message)
        {
            ErrorCode = item.Code,
            Severity = item.Severity switch
            {
                "error" => DiagnosticSeverity.Error,
                "warning" => DiagnosticSeverity.Warning,
                "hint" => DiagnosticSeverity.Hint,
                _ => DiagnosticSeverity.Information,
            },
            ProviderName = item.Source,
        };
    }
}
