using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace CodeInsights.VisualStudio.Editor;

[VisualStudioContribution]
internal sealed class TextViewListener : ExtensionPart, ITextViewOpenClosedListener
{
    private readonly DiagnosticsService _diagnostics;

    public TextViewListener(DiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType(DocumentType.KnownValues.Text)],
    };

    public Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken) =>
        _diagnostics.PublishFileAsync(textView.Document.Uri, cancellationToken);

    public Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken) =>
        _diagnostics.ClearAsync(textView.Document.Uri, cancellationToken);
}
