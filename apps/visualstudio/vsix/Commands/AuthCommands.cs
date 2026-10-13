using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace CodeInsights.VisualStudio.Commands;

[VisualStudioContribution]
internal sealed class SignInCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public SignInCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.SignIn%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.User, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var token = await this.Extensibility.Shell().ShowPromptAsync(
            "Coller le jeton d’accès personnel CodeInsights, créé depuis « Mon compte » dans le hub. L'extension ne crée jamais de jeton elle-même.",
            InputPromptOptions.Default with { Title = "CodeInsights" },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) return;
        var snapshot = await _runtime.SignInAsync(token, cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(
            snapshot.SignedIn ? "Jeton enregistré (DPAPI, utilisateur courant)." : snapshot.Report.Message ?? "Connexion enregistrée.",
            PromptOptions.OK,
            cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class SignOutCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public SignOutCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.SignOut%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.SignOutAsync(cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync("Jeton supprimé.", PromptOptions.OK, cancellationToken);
    }
}
