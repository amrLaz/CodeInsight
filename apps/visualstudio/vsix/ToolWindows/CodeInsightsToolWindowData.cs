using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using CodeInsights.VisualStudio.Host;
using CodeInsights.VisualStudio.Runtime;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

namespace CodeInsights.VisualStudio.ToolWindows;

[DataContract]
internal sealed class CodeInsightsToolWindowData : NotifyPropertyChangedObject
{
    private readonly VisualStudioExtensibility _extensibility;
    private readonly CodeInsightsRuntime _runtime;
    private string _status = "CodeInsights";
    private string _reportText = "Ouvrir un dépôt, renseigner l'URL du hub, puis se connecter.";
    private string _issuesText = "Les issues de la dernière analyse apparaissent ici.";
    private string _rulesText = "Le catalogue de règles du projet apparaît ici après connexion.";
    private string _detailsText = "Sélectionner une issue dans la liste pour afficher son détail. CodeInsights n'héberge pas de description de règle : la définition reste celle publiée par le producteur de l'analyse.";

    public CodeInsightsToolWindowData(VisualStudioExtensibility extensibility, CodeInsightsRuntime runtime)
    {
        _extensibility = extensibility;
        _runtime = runtime;
        ReportNodes = [];
        Issues = [];
        Rules = [];
        OpenIssueCommand = new AsyncCommand(OpenIssueAsync);
        _runtime.SnapshotChanged += Apply;
        if (_runtime.LastSnapshot is not null) Apply(_runtime.LastSnapshot);
    }

    [DataMember]
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    [DataMember]
    public string ReportText
    {
        get => _reportText;
        set => SetProperty(ref _reportText, value);
    }

    [DataMember]
    public string IssuesText
    {
        get => _issuesText;
        set => SetProperty(ref _issuesText, value);
    }

    [DataMember]
    public string RulesText
    {
        get => _rulesText;
        set => SetProperty(ref _rulesText, value);
    }

    [DataMember]
    public string DetailsText
    {
        get => _detailsText;
        set => SetProperty(ref _detailsText, value);
    }

    [DataMember]
    public ObservableCollection<DisplayNode> ReportNodes { get; }

    [DataMember]
    public ObservableCollection<DisplayNode> Issues { get; }

    [DataMember]
    public ObservableCollection<DisplayNode> Rules { get; }

    [DataMember]
    public IAsyncCommand OpenIssueCommand { get; }

    private void Apply(Snapshot snapshot)
    {
        Status = snapshot.Status.Stale ? $"{snapshot.Status.Text} (cache)" : snapshot.Status.Text;
        ReportText = FormatReport(snapshot);
        IssuesText = snapshot.IssuesDescription;
        RulesText = snapshot.RulesDescription;
        DetailsText = FormatDetails(snapshot);
        Replace(ReportNodes, Flatten(snapshot.ReportNodes, 0));
        Replace(Issues, FlattenIssues(snapshot));
        Replace(Rules, FlattenRules(snapshot));
    }

    private async Task OpenIssueAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not DisplayNode node) return;
        if (node.IssueId is int id) await _runtime.SelectIssueAsync(id, cancellationToken);
        if (!string.IsNullOrEmpty(node.Path))
        {
            try
            {
                await _runtime.OpenPathAsync(node.Path, node.Line, cancellationToken);
            }
            catch (FileNotFoundException exception)
            {
                await _extensibility.Shell().ShowPromptAsync(exception.Message, Microsoft.VisualStudio.Extensibility.Shell.PromptOptions.OK, cancellationToken);
            }
        }
    }

    private static string FormatReport(Snapshot snapshot)
    {
        if (snapshot.Report.Kind == "message")
            return string.IsNullOrEmpty(snapshot.Report.Detail)
                ? snapshot.Report.Message ?? ""
                : $"{snapshot.Report.Message}\n{snapshot.Report.Detail}";
        return string.Join(
            Environment.NewLine,
            Flatten(snapshot.ReportNodes, 0).Select(node => node.Text));
    }

    private static string FormatDetails(Snapshot snapshot)
    {
        var details = snapshot.Details;
        if (details is null)
            return "Sélectionner une issue dans la liste pour afficher son détail.\n\nCodeInsights n'héberge pas de description de règle : la définition reste celle publiée par le producteur de l'analyse.";
        var lines = new List<string> { details.Title };
        if (!string.IsNullOrEmpty(details.Note)) lines.Add(details.Note);
        foreach (var row in details.Rows) lines.Add($"{row.Label} : {row.Value}");
        if (details.Locations.Count > 0)
        {
            lines.Add("Localisations :");
            lines.AddRange(details.Locations);
        }

        if (!string.IsNullOrEmpty(details.DocumentationUrl)) lines.Add(details.DocumentationUrl);
        lines.Add(details.DescriptionNotice);
        return string.Join(Environment.NewLine, lines);
    }

    private static List<DisplayNode> Flatten(IEnumerable<ReportNodeDto> nodes, int depth)
    {
        var list = new List<DisplayNode>();
        foreach (var node in nodes)
        {
            var text = string.IsNullOrEmpty(node.Description) ? node.Label : $"{node.Label}  {node.Description}";
            var item = new DisplayNode { Text = new string(' ', depth * 2) + text, Path = node.Open?.Path, Line = node.Open?.Line };
            list.Add(item);
            if (node.Children is { Count: > 0 }) list.AddRange(Flatten(node.Children, depth + 1));
        }

        return list;
    }

    private static List<DisplayNode> FlattenIssues(Snapshot snapshot)
    {
        if (snapshot.Issues.Kind == "message")
            return [new DisplayNode { Text = snapshot.Issues.Message ?? "" }];
        var list = new List<DisplayNode>();
        foreach (var group in snapshot.Issues.Groups ?? [])
        {
            list.Add(new DisplayNode { Text = group.Label, Path = group.Path });
            foreach (var issue in group.Issues)
            {
                var label = string.IsNullOrEmpty(issue.Message) ? issue.Rule : issue.Message;
                list.Add(new DisplayNode
                {
                    Text = $"  {issue.Severity} · {label}{issue.LocationsSuffix}",
                    Path = issue.Path,
                    Line = issue.Line,
                    IssueId = issue.Id,
                });
            }
        }

        if (snapshot.Issues.Truncated) list.Add(new DisplayNode { Text = "Liste tronquée : affiner les filtres pour tout voir." });
        return list;
    }

    private static List<DisplayNode> FlattenRules(Snapshot snapshot)
    {
        if (snapshot.Rules.Kind == "message")
            return [new DisplayNode { Text = snapshot.Rules.Message ?? "" }];
        var list = new List<DisplayNode>();
        foreach (var group in snapshot.Rules.Groups ?? [])
        {
            list.Add(new DisplayNode { Text = group.Tool });
            foreach (var rule in group.Rules)
                list.Add(new DisplayNode { Text = $"  {rule.RuleId} · {rule.Title} ({rule.Status}, {rule.Occurrences})" });
        }

        return list;
    }

    private static void Replace(ObservableCollection<DisplayNode> target, List<DisplayNode> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

[DataContract]
internal sealed class DisplayNode
{
    [DataMember]
    public string Text { get; set; } = "";

    [DataMember]
    public string? Path { get; set; }

    [DataMember]
    public int? Line { get; set; }

    [DataMember]
    public int? IssueId { get; set; }
}
