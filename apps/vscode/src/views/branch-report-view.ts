import * as vscode from 'vscode';
import { reportNodes, type ReportNode, type ViewState } from '../../../core/src/report/report-nodes';

export class BranchReportView implements vscode.TreeDataProvider<ReportNode> {
  private readonly changed = new vscode.EventEmitter<ReportNode | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private roots: ReportNode[] = [];

  setState(state: ViewState): void {
    this.roots = reportNodes(state);
    this.changed.fire(undefined);
  }

  getTreeItem(node: ReportNode): vscode.TreeItem {
    const item = new vscode.TreeItem(
      node.label,
      node.children === undefined ? vscode.TreeItemCollapsibleState.None : vscode.TreeItemCollapsibleState.Expanded,
    );
    if (node.description !== undefined) item.description = node.description;
    if (node.tooltip !== undefined) item.tooltip = node.tooltip;
    if (node.icon !== undefined)
      item.iconPath = new vscode.ThemeIcon(node.icon, node.color === undefined ? undefined : new vscode.ThemeColor(node.color));
    if (node.open !== undefined) item.command = { command: 'codeinsights.openLocalFinding', title: 'Ouvrir', arguments: [node.open] };
    return item;
  }

  getChildren(node?: ReportNode): ReportNode[] {
    return node === undefined ? this.roots : (node.children ?? []);
  }
}
