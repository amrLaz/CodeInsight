using CodeInsights.VisualStudio.Auth;
using CodeInsights.VisualStudio.Editor;
using CodeInsights.VisualStudio.Host;
using CodeInsights.VisualStudio.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace CodeInsights.VisualStudio;

[VisualStudioContribution]
internal sealed class CodeInsightsExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "CodeInsights.VisualStudio.8f3c9e2a-4d71-4b8e-9c1a-7e6f5d4c3b2a",
            version: this.ExtensionAssemblyVersion,
            publisherName: "upcoop",
            displayName: "CodeInsights",
            description: "Rapport CodeInsights de la branche courante et analyse locale des changements."),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSettingsObservers();
        serviceCollection.AddSingleton<TokenStore>();
        serviceCollection.AddSingleton<HostClient>();
        serviceCollection.AddScoped<CodeInsightsRuntime>();
        serviceCollection.AddScoped<DiagnosticsService>();
    }
}
