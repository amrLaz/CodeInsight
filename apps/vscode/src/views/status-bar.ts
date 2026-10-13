import * as vscode from 'vscode';
import { gateIcon, gateLabel, type GateVerdict } from '../../../core/src/report/measure';

/** Single status bar entry: verdict of the last analysis of the current branch. */
export class StatusBar implements vscode.Disposable {
  private readonly item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);

  constructor() {
    this.item.command = 'codeinsights.refresh';
    this.setIdle('Non configuré');
    this.item.show();
  }

  setIdle(reason: string): void {
    this.item.text = '$(circle-slash) CodeInsights';
    this.item.tooltip = `CodeInsights : ${reason}`;
  }

  setBusy(): void {
    this.item.text = '$(sync~spin) CodeInsights';
    this.item.tooltip = 'CodeInsights : lecture du rapport de la branche';
  }

  setVerdict(verdict: GateVerdict, detail: string, stale: boolean): void {
    this.item.text = `${gateIcon(verdict)} CodeInsights: ${gateLabel(verdict)}${stale ? ' (cache)' : ''}`;
    this.item.tooltip = detail;
  }

  dispose(): void {
    this.item.dispose();
  }
}
