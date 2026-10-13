import * as vscode from 'vscode';
import type { DuplicateBlock } from '../../../core/src/api/codeinsights';
import { relativeToRoot } from '../../../core/src/context/paths';
import { duplicateLabel, placeDuplicates } from '../../../core/src/report/duplication';
import type { Hunk } from '../../../core/src/report/drift';

/** Highlights duplicated ranges of the last analysis on the open file. */
export class DuplicationDecorations implements vscode.Disposable {
  private readonly type: vscode.TextEditorDecorationType;
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private count = 0;
  private path: string | undefined;

  constructor() {
    this.type = vscode.window.createTextEditorDecorationType({
      isWholeLine: true,
      overviewRulerColor: new vscode.ThemeColor('editorOverviewRuler.warningForeground'),
      overviewRulerLane: vscode.OverviewRulerLane.Right,
      backgroundColor: new vscode.ThemeColor('diffEditor.unchangedRegionBackground'),
    });
  }

  apply(editor: vscode.TextEditor | undefined, rootPath: string, blocks: DuplicateBlock[], hunks: Hunk[] | undefined, state: string | undefined): void {
    this.clear();
    if (editor === undefined || state !== 'measured') return;
    const path = relativeToRoot(rootPath, editor.document.uri.fsPath);
    if (path === undefined) return;
    const placed = placeDuplicates(path, blocks, hunks);
    this.path = path;
    this.count = placed.length;
    editor.setDecorations(
      this.type,
      placed.map((block) => {
        const start = Math.max(block.start - 1, 0);
        const end = Math.min(Math.max(block.end - 1, start), Math.max(editor.document.lineCount - 1, 0));
        return {
          range: new vscode.Range(start, 0, end, editor.document.lineAt(end).text.length),
          hoverMessage: duplicateLabel(block),
        };
      }),
    );
    this.changed.fire();
  }

  provideCodeLenses(document: vscode.TextDocument, rootPath: string): vscode.CodeLens[] {
    if (this.path === undefined || this.count === 0) return [];
    if (relativeToRoot(rootPath, document.uri.fsPath) !== this.path) return [];
    return [
      new vscode.CodeLens(new vscode.Range(0, 0, 0, 0), {
        title: this.count === 1 ? 'CodeInsights : 1 bloc dupliqué' : `CodeInsights : ${this.count} blocs dupliqués`,
        command: 'codeinsights.qualityInfo',
      }),
    ];
  }

  clear(): void {
    this.count = 0;
    this.path = undefined;
    for (const editor of vscode.window.visibleTextEditors) editor.setDecorations(this.type, []);
    this.changed.fire();
  }

  dispose(): void {
    this.clear();
    this.type.dispose();
    this.changed.dispose();
  }
}
