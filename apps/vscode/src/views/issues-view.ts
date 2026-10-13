import * as vscode from 'vscode';
import { countLabel, severityColor, severityIcon, severityLabel, type Issue, type IssueGroup } from '../../../core/src/report/issues';
import { flows, locationLabel, secondary, type IssueLocation } from '../../../core/src/report/locations';

export type IssuesState =
  | { kind: 'message'; message: string }
  | { kind: 'issues'; groups: IssueGroup[]; truncated: boolean };

type Node = { group: IssueGroup } | { issue: Issue } | { issue: Issue; location: IssueLocation; ordinal?: number };

/** Issues of the analysed branch, grouped by file. Selecting one opens the file at its line. */
export class IssuesView implements vscode.TreeDataProvider<Node> {
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private state: IssuesState = { kind: 'message', message: 'Aucune analyse chargée' };
  private view: vscode.TreeView<Node> | undefined;

  attach(view: vscode.TreeView<Node>): void {
    this.view = view;
  }

  setState(state: IssuesState, description: string): void {
    this.state = state;
    if (this.view !== undefined) {
      this.view.description = description;
      // TreeView.message is typed as a plain string: an empty one clears the banner.
      this.view.message =
        state.kind === 'message' ? state.message : state.truncated ? 'Liste tronquée : affiner les filtres pour tout voir.' : '';
      this.view.badge =
        state.kind === 'issues'
          ? { value: state.groups.reduce((total, group) => total + group.issues.length, 0), tooltip: countLabel(state.groups) }
          : undefined;
    }
    this.changed.fire(undefined);
  }

  getTreeItem(node: Node): vscode.TreeItem {
    if ('group' in node) {
      const item = new vscode.TreeItem(
        node.group.path === undefined ? node.group.label : node.group.label.split('/').slice(-1)[0]!,
        vscode.TreeItemCollapsibleState.Expanded,
      );
      const folder = node.group.path?.split('/').slice(0, -1).join('/') ?? '';
      if (folder.length > 0) item.description = folder;
      item.tooltip = `${node.group.label} — ${node.group.issues.length} issue(s)`;
      item.iconPath = new vscode.ThemeIcon(severityIcon(node.group.worst), new vscode.ThemeColor(severityColor(node.group.worst)));
      item.contextValue = 'codeinsights.group';
      return item;
    }
    if ('location' in node) {
      const item = new vscode.TreeItem(locationLabel(node.location, node.ordinal), vscode.TreeItemCollapsibleState.None);
      item.iconPath = new vscode.ThemeIcon(node.location.kind === 'flow' ? 'arrow-small-right' : 'circle-small');
      item.contextValue = 'codeinsights.location';
      item.command = { command: 'codeinsights.openLocation', title: 'Ouvrir', arguments: [node.location] };
      return item;
    }
    const { issue } = node;
    const hasLocations = secondary(issue.locations).length > 0;
    const item = new vscode.TreeItem(
      `${issue.message.length === 0 ? issue.rule : issue.message}${issue.locationsSuffix}`,
      hasLocations ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None,
    );
    item.description = `${severityLabel(issue.severity)} · ${issue.rule}${issue.line === undefined ? '' : ` · L${issue.line}`}${issue.excepted ? ' · exception' : ''}`;
    item.tooltip = new vscode.MarkdownString(
      [`**${severityLabel(issue.severity)}** · ${issue.category}`, '', issue.message, '', `\`${issue.tool}/${issue.rule}\``].join('\n'),
    );
    item.iconPath = new vscode.ThemeIcon(severityIcon(issue.severity), new vscode.ThemeColor(severityColor(issue.severity)));
    item.contextValue = 'codeinsights.issue';
    item.command = { command: 'codeinsights.openIssue', title: 'Ouvrir', arguments: [issue] };
    return item;
  }

  getChildren(node?: Node): Node[] {
    if (this.state.kind === 'message') return [];
    if (node === undefined) return this.state.groups.map((group) => ({ group }));
    if ('group' in node) return node.group.issues.map((issue) => ({ issue }));
    if ('location' in node) return [];
    // Flow steps keep the producer's order and are ranked from one; related locations follow.
    const steps = flows(node.issue.locations).flatMap((flow) =>
      flow.steps.map((location, index) => ({ issue: node.issue, location, ordinal: index + 1 })),
    );
    const others = secondary(node.issue.locations)
      .filter((location) => location.kind !== 'flow')
      .map((location) => ({ issue: node.issue, location }));
    return [...steps, ...others];
  }
}
