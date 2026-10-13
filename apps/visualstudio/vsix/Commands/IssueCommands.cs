using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace CodeInsights.VisualStudio.Commands;

[VisualStudioContribution]
internal sealed class FilterIssuesCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public FilterIssuesCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.FilterIssues%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Filter, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var shell = this.Extensibility.Shell();
        var severity = await PickAsync(shell, "Criticité", ["all", "blocker", "high", "medium", "low", "info"], cancellationToken);
        if (severity is null) return;
        var category = await PickAsync(
            shell,
            "Catégorie",
            ["all", "bug", "code_smell", "vulnerability", "security_hotspot", "secret", "dependency", "code", "ioc"],
            cancellationToken);
        if (category is null) return;
        var state = await PickAsync(shell, "État", ["all", "open", "confirmed", "resolved", "false_positive", "accepted_risk"], cancellationToken);
        if (state is null) return;
        var scope = await PickAsync(shell, "Périmètre", ["all", "mine"], cancellationToken);
        if (scope is null) return;
        await _runtime.FilterIssuesAsync(new { severity, category, state, mine = scope == "mine" }, cancellationToken);
    }

    internal static async Task<string?> PickAsync(ShellExtensibility shell, string title, string[] values, CancellationToken cancellationToken)
    {
        var choices = values.ToDictionary(value => value, value => value);
        var selected = await shell.ShowPromptAsync(
            title,
            new PromptOptions<string>
            {
                Choices = choices,
                DismissedReturns = values[0],
                Title = $"CodeInsights — {title}",
            },
            cancellationToken);
        return selected;
    }
}

[VisualStudioContribution]
internal sealed class ClearIssueFiltersCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public ClearIssueFiltersCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.ClearIssueFilters%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        await _runtime.ClearIssueFiltersAsync(cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class FilterRulesCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public FilterRulesCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.FilterRules%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Filter, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await _runtime.EnsureReadyAsync(context, cancellationToken);
        var search = await this.Extensibility.Shell().ShowPromptAsync(
            "Identifiant, outil ou titre. Vide : pas de filtre texte.",
            InputPromptOptions.Default with { Title = "CodeInsights — Recherche de règle" },
            cancellationToken);
        if (search is null) return;
        var status = await FilterIssuesCommand.PickAsync(this.Extensibility.Shell(), "État", ["all", "enabled", "disabled", "unmanaged"], cancellationToken);
        if (status is null) return;
        var severity = await FilterIssuesCommand.PickAsync(this.Extensibility.Shell(), "Criticité", ["all", "blocker", "high", "medium", "low", "info"], cancellationToken);
        if (severity is null) return;
        await _runtime.FilterRulesAsync(new { search, status, category = "all", severity }, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class TriageIssueCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public TriageIssueCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.Triage%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.CheckBox, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var snapshot = await _runtime.EnsureReadyAsync(context, cancellationToken);
        var issues = snapshot.Issues.Groups?.SelectMany(group => group.Issues).ToList() ?? [];
        if (issues.Count == 0)
        {
            await this.Extensibility.Shell().ShowPromptAsync("Aucune issue à traiter.", PromptOptions.OK, cancellationToken);
            return;
        }

        var labels = issues.ToDictionary(
            issue => $"{issue.Severity} · {issue.Rule} · {(string.IsNullOrEmpty(issue.Message) ? issue.Rule : issue.Message)}",
            issue => issue.Id);
        var selected = await this.Extensibility.Shell().ShowPromptAsync(
            "Issue à traiter",
            new PromptOptions<int> { Choices = labels, DismissedReturns = -1, Title = "CodeInsights" },
            cancellationToken);
        if (selected < 0) return;
        await _runtime.SelectIssueAsync(selected, cancellationToken);
        var action = await FilterIssuesCommand.PickAsync(
            this.Extensibility.Shell(),
            "Action",
            ["assign", "unassign", "state", "comment"],
            cancellationToken);
        if (action is null) return;
        string? reason = null;
        string? state = null;
        string? expiresAt = null;
        if (action is "state" or "comment")
        {
            reason = await this.Extensibility.Shell().ShowPromptAsync(
                action == "comment" ? "Commentaire visible dans le hub" : "Motif de la décision",
                InputPromptOptions.Default with { Title = "CodeInsights" },
                cancellationToken);
            if (string.IsNullOrWhiteSpace(reason)) return;
        }

        if (action == "state")
        {
            state = await FilterIssuesCommand.PickAsync(
                this.Extensibility.Shell(),
                "Nouvel état",
                ["open", "confirmed", "resolved", "false_positive", "accepted_risk"],
                cancellationToken);
            if (state is null) return;
            if (state is "false_positive" or "accepted_risk")
            {
                expiresAt = await this.Extensibility.Shell().ShowPromptAsync(
                    "Expiration ISO 8601 dans l’année à venir, par exemple 2026-12-31",
                    InputPromptOptions.Default with { Title = "CodeInsights" },
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(expiresAt)) return;
                if (DateTime.TryParse(expiresAt, out var parsed)) expiresAt = parsed.ToUniversalTime().ToString("o");
            }
        }

        var result = await _runtime.TriageAsync(new { issueId = selected, action, state, reason, expiresAt }, cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(result.Notice ?? "Issue mise à jour.", PromptOptions.OK, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class CopyPromptCommand : Command
{
    private readonly CodeInsightsRuntime _runtime;

    public CopyPromptCommand(CodeInsightsRuntime runtime) => _runtime = runtime;

    public override CommandConfiguration CommandConfiguration => new("%CodeInsights.CopyPrompt%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.Copy, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var snapshot = await _runtime.EnsureReadyAsync(context, cancellationToken);
        var issues = snapshot.Issues.Groups?.SelectMany(group => group.Issues).ToList() ?? [];
        if (issues.Count == 0)
        {
            await this.Extensibility.Shell().ShowPromptAsync("Aucune issue dont copier le prompt.", PromptOptions.OK, cancellationToken);
            return;
        }

        var labels = issues.ToDictionary(
            issue => $"{issue.Rule} · {(string.IsNullOrEmpty(issue.Message) ? issue.Path : issue.Message)}",
            issue => issue.Id);
        var selected = await this.Extensibility.Shell().ShowPromptAsync(
            "Issue",
            new PromptOptions<int> { Choices = labels, DismissedReturns = -1, Title = "CodeInsights" },
            cancellationToken);
        if (selected < 0) return;
        var built = await _runtime.PromptAsync(selected, cancellationToken);
        await this.Extensibility.Shell().ShowPromptAsync(
            $"Prompt ({built.Label}) :\n\n{built.Prompt}",
            PromptOptions.OK with { Title = "CodeInsights" },
            cancellationToken);
    }
}
