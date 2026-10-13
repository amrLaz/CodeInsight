import * as vscode from 'vscode';
import { statusLabel, type Rule, type RuleGroup } from '../../../core/src/report/rules';

export type RulesState = { kind: 'message'; message: string } | { kind: 'rules'; groups: RuleGroup[] };

type Node = { group: RuleGroup } | { rule: Rule };

/** Read-only catalogue of the project's rules, grouped by producer. */
export class RulesView implements vscode.TreeDataProvider<Node> {
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private state: RulesState = { kind: 'message', message: 'Aucune analyse chargée' };
  private view: vscode.TreeView<Node> | undefined;

  attach(view: vscode.TreeView<Node>): void {
    this.view = view;
  }

  setState(state: RulesState, description: string): void {
    this.state = state;
    if (this.view !== undefined) {
      this.view.description = description;
      this.view.message = state.kind === 'message' ? state.message : '';
    }
    this.changed.fire(undefined);
  }

  getTreeItem(node: Node): vscode.TreeItem {
    if ('group' in node) {
      const item = new vscode.TreeItem(node.group.tool, vscode.TreeItemCollapsibleState.Expanded);
      item.description = `${node.group.rules.length} règle(s)`;
      item.iconPath = new vscode.ThemeIcon('beaker');
      item.contextValue = 'codeinsights.ruleGroup';
      return item;
    }
    const { rule } = node;
    const item = new vscode.TreeItem(rule.ruleId, vscode.TreeItemCollapsibleState.None);
    item.description = `${rule.severity} · ${statusLabel(rule.status)} · ${rule.occurrences}`;
    item.tooltip = new vscode.MarkdownString(
      [`**${rule.title}**`, '', `\`${rule.tool}/${rule.ruleId}\``, '', `${rule.category} · ${rule.language} · ${statusLabel(rule.status)}`].join('\n'),
    );
    item.iconPath = new vscode.ThemeIcon(rule.status === 'disabled' ? 'circle-slash' : 'symbol-ruler');
    item.contextValue = 'codeinsights.rule';
    item.command = { command: 'codeinsights.openRuleInHub', title: 'Ouvrir', arguments: [rule] };
    return item;
  }

  getChildren(node?: Node): Node[] {
    if (this.state.kind === 'message') return [];
    if (node === undefined) return this.state.groups.map((group) => ({ group }));
    if ('group' in node) return node.group.rules.map((rule) => ({ rule }));
    return [];
  }
}
