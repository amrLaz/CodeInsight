using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace CodeInsights.VisualStudio.Editor;

#pragma warning disable VSEXTPREVIEW_CODELENS

[VisualStudioContribution]
internal sealed class IssueCodeLensProvider : ExtensionPart, ICodeLensProvider
{
    private readonly CodeInsightsRuntime _runtime;

    public IssueCodeLensProvider(CodeInsightsRuntime runtime)
    {
        _runtime = runtime;
    }

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType(DocumentType.KnownValues.Code)],
    };

    public CodeLensProviderConfiguration CodeLensProviderConfiguration =>
        new("CodeInsights")
        {
            Priority = 400,
        };

    public async Task<CodeLens?> TryCreateCodeLensAsync(CodeElement codeElement, CodeElementContext codeElementContext, CancellationToken token)
    {
        if (codeElement.Kind != CodeElementKind.KnownValues.Type && codeElement.Kind != CodeElementKind.KnownValues.Method)
            return null;
        if (!codeElementContext.Document.Uri.IsFile) return null;
        var context = await _runtime.FileContextAsync(codeElementContext.Document.Uri.LocalPath, token);
        var lens = context.Lenses.FirstOrDefault(item => item.Kind == "issues");
        if (lens is null) return null;
        return new IssueCodeLens(lens.Text);
    }

    private sealed class IssueCodeLens : InvokableCodeLens
    {
        private readonly string _text;

        public IssueCodeLens(string text) => _text = text;

        public override void Dispose()
        {
        }

        public override Task ExecuteAsync(CodeElementContext codeElementContext, IClientContext clientContext, CancellationToken cancelToken) =>
            Task.CompletedTask;

        public override Task<CodeLensLabel> GetLabelAsync(CodeElementContext codeElementContext, CancellationToken token) =>
            Task.FromResult(new CodeLensLabel { Text = _text, Tooltip = "Issues CodeInsights de ce fichier" });
    }
}

#pragma warning restore VSEXTPREVIEW_CODELENS
