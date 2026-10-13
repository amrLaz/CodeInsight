import * as vscode from 'vscode';
import { repositoryFile } from '../../../core/src/context/paths';
import type { LocalFinding } from '../../../core/src/local/producers';
import { severityLabel } from '../../../core/src/report/issues';

const SEVERITY: Record<string, vscode.DiagnosticSeverity> = {
  blocker: vscode.DiagnosticSeverity.Error,
  high: vscode.DiagnosticSeverity.Error,
  medium: vscode.DiagnosticSeverity.Warning,
  low: vscode.DiagnosticSeverity.Information,
  info: vscode.DiagnosticSeverity.Hint,
};

/**
 * Findings of the local run, published in their own collection and labelled
 * `CodeInsights (local)`. Keeping them apart from the analysed ones matters: these come
 * from two producers on a workstation, not from the pipeline that computes the gate.
 */
export class LocalAnnotations implements vscode.Disposable {
  private readonly collection = vscode.languages.createDiagnosticCollection('codeinsights.local');

  publish(rootPath: string, findings: LocalFinding[]): void {
    this.collection.clear();
    const byFile = new Map<string, vscode.Diagnostic[]>();
    for (const finding of findings) {
      const file = repositoryFile(rootPath, finding.path);
      if (file === undefined) continue;
      const line = Math.max(finding.line - 1, 0);
      const endLine = Math.max((finding.endLine ?? finding.line) - 1, line);
      const diagnostic = new vscode.Diagnostic(
        new vscode.Range(
          line,
          Math.max((finding.column ?? 1) - 1, 0),
          endLine,
          finding.endColumn === undefined ? Number.MAX_SAFE_INTEGER : Math.max(finding.endColumn - 1, 0),
        ),
        `${finding.message.length === 0 ? finding.rule : finding.message} [${severityLabel(finding.severity)}]`,
        SEVERITY[finding.severity] ?? vscode.DiagnosticSeverity.Information,
      );
      diagnostic.source = 'CodeInsights (local)';
      diagnostic.code = finding.rule;
      const bucket = byFile.get(file);
      if (bucket === undefined) byFile.set(file, [diagnostic]);
      else bucket.push(diagnostic);
    }
    for (const [file, diagnostics] of byFile) this.collection.set(vscode.Uri.file(file), diagnostics);
  }

  clear(): void {
    this.collection.clear();
  }

  dispose(): void {
    this.collection.dispose();
  }
}
