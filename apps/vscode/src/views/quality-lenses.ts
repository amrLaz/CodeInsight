import * as vscode from 'vscode';
import { CoverageDecorations } from './coverage';
import { DuplicationDecorations } from './duplication';

/**
 * Single CodeLens provider so coverage and duplication share the first line without
 * registering two providers that would fight over the same range.
 */
export class QualityLenses implements vscode.CodeLensProvider, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private rootPath = '';

  constructor(
    private readonly coverage: CoverageDecorations,
    private readonly duplication: DuplicationDecorations,
  ) {
    coverage.onDidChangeCodeLenses(() => this.changed.fire());
    duplication.onDidChangeCodeLenses(() => this.changed.fire());
  }

  setRoot(rootPath: string): void {
    this.rootPath = rootPath;
    this.changed.fire();
  }

  provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    if (this.rootPath.length === 0) return [];
    return [
      ...this.coverage.provideCodeLenses(document, this.rootPath),
      ...this.duplication.provideCodeLenses(document, this.rootPath),
    ];
  }

  dispose(): void {
    this.changed.dispose();
  }
}
