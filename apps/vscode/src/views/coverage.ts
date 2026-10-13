import * as vscode from 'vscode';
import type { CoverageLine } from '../../../core/src/api/codeinsights';
import { relativeToRoot } from '../../../core/src/context/paths';
import { coverageSummary, placeCoverage, type CoverageOrigin, type PlacedCoverage } from '../../../core/src/report/coverage';
import type { Hunk } from '../../../core/src/report/drift';

/**
 * Gutter of the last analysis' coverage (or a newer local LCOV). Unmeasured coverage
 * is not painted: a green gutter would read as 100 % when the producer did not run.
 */
export class CoverageDecorations implements vscode.Disposable {
  private readonly covered: vscode.TextEditorDecorationType;
  private readonly missed: vscode.TextEditorDecorationType;
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private placed: PlacedCoverage[] = [];
  private summaryDisplay = '';
  private path: string | undefined;

  constructor() {
    this.covered = vscode.window.createTextEditorDecorationType({
      isWholeLine: false,
      overviewRulerColor: new vscode.ThemeColor('editorOverviewRuler.addedForeground'),
      overviewRulerLane: vscode.OverviewRulerLane.Left,
      dark: { before: { contentText: ' ', backgroundColor: '#197047', margin: '0 6px 0 0', width: '3px' } },
      light: { before: { contentText: ' ', backgroundColor: '#197047', margin: '0 6px 0 0', width: '3px' } },
    });
    this.missed = vscode.window.createTextEditorDecorationType({
      isWholeLine: false,
      overviewRulerColor: new vscode.ThemeColor('editorOverviewRuler.deletedForeground'),
      overviewRulerLane: vscode.OverviewRulerLane.Left,
      dark: { before: { contentText: ' ', backgroundColor: '#b42318', margin: '0 6px 0 0', width: '3px' } },
      light: { before: { contentText: ' ', backgroundColor: '#b42318', margin: '0 6px 0 0', width: '3px' } },
    });
  }

  apply(
    editor: vscode.TextEditor | undefined,
    rootPath: string,
    lines: CoverageLine[],
    hunks: Hunk[] | undefined,
    origin: CoverageOrigin,
    state: string | undefined,
  ): void {
    this.clear();
    if (editor === undefined) return;
    const path = relativeToRoot(rootPath, editor.document.uri.fsPath);
    if (path === undefined) return;
    const summary = coverageSummary(state, lines, origin);
    this.summaryDisplay = summary.state === 'measured' ? `Couverture : ${summary.display}` : `Couverture : ${summary.display}`;
    this.path = path;
    if (summary.state !== 'measured') {
      this.changed.fire();
      return;
    }
    this.placed = placeCoverage(lines, hunks, origin);
    const covered: vscode.DecorationOptions[] = [];
    const missed: vscode.DecorationOptions[] = [];
    for (const row of this.placed) {
      const line = Math.max(row.line - 1, 0);
      if (line >= editor.document.lineCount) continue;
      const range = new vscode.Range(line, 0, line, 0);
      (row.covered ? covered : missed).push({ range, hoverMessage: row.covered ? 'Ligne couverte (analyse)' : 'Ligne non couverte (analyse)' });
    }
    editor.setDecorations(this.covered, covered);
    editor.setDecorations(this.missed, missed);
    this.changed.fire();
  }

  provideCodeLenses(document: vscode.TextDocument, rootPath: string): vscode.CodeLens[] {
    if (this.path === undefined || this.summaryDisplay.length === 0) return [];
    if (relativeToRoot(rootPath, document.uri.fsPath) !== this.path) return [];
    return [
      new vscode.CodeLens(new vscode.Range(0, 0, 0, 0), {
        title: `CodeInsights : ${this.summaryDisplay}`,
        command: 'codeinsights.qualityInfo',
      }),
    ];
  }

  clear(): void {
    this.placed = [];
    this.summaryDisplay = '';
    this.path = undefined;
    for (const editor of vscode.window.visibleTextEditors) {
      editor.setDecorations(this.covered, []);
      editor.setDecorations(this.missed, []);
    }
    this.changed.fire();
  }

  dispose(): void {
    this.clear();
    this.covered.dispose();
    this.missed.dispose();
    this.changed.dispose();
  }
}
