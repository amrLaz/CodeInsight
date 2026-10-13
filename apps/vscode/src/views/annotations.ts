import * as vscode from 'vscode';
import { anchor as anchorFor, anchorNote, type Anchor, type AnchorState } from '../../../core/src/report/drift';
import { diagnosticSpec, type DiagnosticSpec } from '../../../core/src/report/diagnostics';
import { severityLabel, type Issue, type IssueGroup } from '../../../core/src/report/issues';
import { locationLabel, primaryRange, secondary } from '../../../core/src/report/locations';
import { repositoryFile } from '../../../core/src/context/paths';
import type { DiffSource } from '../../../core/src/context/git-diff';

type Anchored = { issue: Issue; anchor: Anchor; spec: DiagnosticSpec };

const SEVERITY: Record<DiagnosticSpec['severity'], vscode.DiagnosticSeverity> = {
  error: vscode.DiagnosticSeverity.Error,
  warning: vscode.DiagnosticSeverity.Warning,
  information: vscode.DiagnosticSeverity.Information,
  hint: vscode.DiagnosticSeverity.Hint,
};

/**
 * Publishes the issues of the analysed branch into the Problems panel, the hovers and
 * the CodeLens of each file. Positions are re-anchored on the working tree; an issue
 * that cannot be placed with certainty says so instead of pointing at the wrong line.
 */
export class Annotations implements vscode.CodeLensProvider, vscode.HoverProvider, vscode.Disposable {
  private readonly collection = vscode.languages.createDiagnosticCollection('codeinsights');
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private byPath = new Map<string, Issue[]>();
  private anchored = new Map<string, Anchored[]>();
  private context: { rootPath: string; commitSha: string; diffs: DiffSource } | undefined;

  /** Replaces the published set; anchoring then resolves file by file. */
  async publish(
    groups: IssueGroup[],
    context: { rootPath: string; commitSha: string; diffs: DiffSource },
    eagerFiles: number,
  ): Promise<void> {
    this.context = context;
    this.byPath = new Map();
    this.anchored = new Map();
    this.collection.clear();
    for (const group of groups) if (group.path !== undefined) this.byPath.set(group.path, group.issues);
    let resolved = 0;
    for (const path of this.byPath.keys()) {
      // Files beyond the eager budget are anchored the first time they are opened.
      await this.refresh(path, resolved < eagerFiles);
      resolved += 1;
    }
    this.changed.fire();
  }

  /** Recomputes one file, reading its diff only when allowed to. */
  async refresh(path: string, withDiff: boolean): Promise<void> {
    const context = this.context;
    const issues = this.byPath.get(path);
    if (context === undefined || issues === undefined) return;
    const file = repositoryFile(context.rootPath, path);
    if (file === undefined) return;
    const hunks = withDiff ? await context.diffs.hunks(context.commitSha, path) : undefined;
    const anchored: Anchored[] = [];
    for (const issue of issues) {
      const placed = anchorFor(issue.line, hunks);
      const spec = diagnosticSpec(issue, placed);
      if (spec !== undefined) anchored.push({ issue, anchor: placed, spec });
    }
    this.anchored.set(path, anchored);
    const uri = vscode.Uri.file(file);
    this.collection.set(
      uri,
      anchored.map((entry) => {
        const line = Math.max(entry.spec.line - 1, 0);
        // Columns are only trusted when the position itself is: a shifted line keeps its
        // column, a rewritten one would point the squiggle at unrelated code.
        const exact = entry.anchor.state === 'exact' || entry.anchor.state === 'shifted';
        const range = exact ? primaryRange(entry.issue.locations, entry.issue.line) : undefined;
        const diagnostic = new vscode.Diagnostic(
          range === undefined
            ? new vscode.Range(line, 0, line, Number.MAX_SAFE_INTEGER)
            : new vscode.Range(line, range.column - 1, line, range.endColumn === undefined ? Number.MAX_SAFE_INTEGER : range.endColumn - 1),
          entry.spec.message,
          SEVERITY[entry.spec.severity],
        );
        diagnostic.source = 'CodeInsights';
        diagnostic.code =
          entry.spec.documentationUrl === undefined
            ? entry.spec.code
            : { value: entry.spec.code, target: vscode.Uri.parse(entry.spec.documentationUrl) };
        if (entry.spec.unnecessary) diagnostic.tags = [vscode.DiagnosticTag.Unnecessary];
        // Secondary locations become native related information: the Problems panel lists
        // them under the issue and each one is navigable.
        const related: vscode.DiagnosticRelatedInformation[] = [];
        for (const location of secondary(entry.issue.locations)) {
          if (location.path === undefined || location.line === undefined) continue;
          const file = repositoryFile(context.rootPath, location.path);
          if (file === undefined) continue;
          const target = Math.max(location.line - 1, 0);
          related.push(
            new vscode.DiagnosticRelatedInformation(
              new vscode.Location(
                vscode.Uri.file(file),
                new vscode.Range(target, Math.max((location.column ?? 1) - 1, 0), target, Number.MAX_SAFE_INTEGER),
              ),
              locationLabel(location),
            ),
          );
        }
        if (related.length > 0) diagnostic.relatedInformation = related;
        return diagnostic;
      }),
    );
    this.changed.fire();
  }

  /** Called when a document opens: its issues are re-anchored with the real diff. */
  async resolve(uri: vscode.Uri): Promise<void> {
    const context = this.context;
    if (context === undefined) return;
    for (const path of this.byPath.keys()) {
      const file = repositoryFile(context.rootPath, path);
      if (file !== undefined && vscode.Uri.file(file).fsPath === uri.fsPath) {
        await this.refresh(path, true);
        return;
      }
    }
  }

  private entries(uri: vscode.Uri): Anchored[] {
    const context = this.context;
    if (context === undefined) return [];
    for (const [path, anchored] of this.anchored) {
      const file = repositoryFile(context.rootPath, path);
      if (file !== undefined && vscode.Uri.file(file).fsPath === uri.fsPath) return anchored;
    }
    return [];
  }

  /** Anchor state of one issue, so the details panel can label a drifting position. */
  anchorState(issue: Issue): AnchorState | undefined {
    for (const anchored of this.anchored.values()) {
      const match = anchored.find((entry) => entry.issue.id === issue.id);
      if (match !== undefined) return match.anchor.state;
    }
    return undefined;
  }

  provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    const anchored = this.entries(document.uri);
    if (anchored.length === 0) return [];
    const blockers = anchored.filter((entry) => entry.issue.severity === 'blocker').length;
    const approximate = anchored.filter((entry) => entry.anchor.state !== 'exact' && entry.anchor.state !== 'shifted').length;
    const parts = [anchored.length === 1 ? '1 issue' : `${anchored.length} issues`];
    if (blockers > 0) parts.push(blockers === 1 ? '1 bloquante' : `${blockers} bloquantes`);
    if (approximate > 0) parts.push(`${approximate} position(s) incertaine(s)`);
    return [
      new vscode.CodeLens(new vscode.Range(0, 0, 0, 0), {
        title: `CodeInsights : ${parts.join(' · ')}`,
        command: 'codeinsights.focusIssues',
      }),
    ];
  }

  provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
    const matches = this.entries(document.uri).filter((entry) => entry.spec.line - 1 === position.line);
    if (matches.length === 0) return undefined;
    const markdown = new vscode.MarkdownString();
    markdown.isTrusted = true;
    for (const entry of matches) {
      const note = anchorNote(entry.anchor.state);
      markdown.appendMarkdown(`**CodeInsights — ${severityLabel(entry.issue.severity)}** · ${entry.issue.category}\n\n`);
      markdown.appendMarkdown(`${entry.issue.message}\n\n`);
      markdown.appendMarkdown(`\`${entry.issue.tool}/${entry.issue.rule}\`${entry.issue.excepted ? ' · exception acceptée' : ''}\n\n`);
      if (note !== undefined) markdown.appendMarkdown(`_${note}_\n\n`);
      if (entry.issue.documentationUrl !== undefined) markdown.appendMarkdown(`[Définition de la règle](${entry.issue.documentationUrl})\n\n`);
      markdown.appendMarkdown('---\n\n');
    }
    return new vscode.Hover(markdown);
  }

  dispose(): void {
    this.collection.dispose();
    this.changed.dispose();
  }
}
