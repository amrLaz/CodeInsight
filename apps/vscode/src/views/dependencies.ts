import * as vscode from 'vscode';
import type { RawComponent } from '../../../core/src/api/codeinsights';
import { relativeToRoot } from '../../../core/src/context/paths';
import { dependencyLabel, manifestKind, matchComponent, parseManifest } from '../../../core/src/report/dependencies';

/** CodeLens and hover on package manifests, bound to the SBOM of the last analysis. */
export class DependencyAnnotations implements vscode.CodeLensProvider, vscode.HoverProvider, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private components: RawComponent[] | undefined;
  private rootPath: string | undefined;

  set(rootPath: string, components: RawComponent[] | undefined): void {
    this.rootPath = rootPath;
    this.components = components;
    this.changed.fire();
  }

  provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    const hits = this.hits(document);
    if (hits === undefined) return [];
    const loaded = this.components !== undefined;
    const lenses: vscode.CodeLens[] = [];
    for (const hit of hits) {
      const match = loaded ? matchComponent(hit.name, hit.kind, this.components ?? []) : undefined;
      const line = Math.max(hit.line - 1, 0);
      lenses.push(
        new vscode.CodeLens(new vscode.Range(line, 0, line, 0), {
          title: `CodeInsights : ${dependencyLabel(match, loaded)}`,
          command: 'codeinsights.qualityInfo',
        }),
      );
    }
    return lenses;
  }

  provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
    const hits = this.hits(document);
    if (hits === undefined) return undefined;
    const hit = hits.find((entry) => entry.line - 1 === position.line);
    if (hit === undefined) return undefined;
    const loaded = this.components !== undefined;
    const match = loaded ? matchComponent(hit.name, hit.kind, this.components ?? []) : undefined;
    const markdown = new vscode.MarkdownString();
    markdown.appendMarkdown(`**CodeInsights** — ${dependencyLabel(match, loaded)}\n\n`);
    if (match?.purl !== undefined) markdown.appendMarkdown(`\`${match.purl}\`\n\n`);
    if (match !== undefined && match.vulnerabilities.length === 0)
      markdown.appendMarkdown("_Aucune CVE n'a été transmise pour ce composant : cela ne prouve pas l'absence de vulnérabilité._\n");
    for (const vulnerability of match?.vulnerabilities ?? []) {
      const severity = vulnerability.severity === undefined ? '' : ` · ${vulnerability.severity}`;
      const source = vulnerability.source === undefined ? '' : ` · ${vulnerability.source}`;
      markdown.appendMarkdown(`- \`${vulnerability.id}\`${severity}${source}\n`);
    }
    return new vscode.Hover(markdown);
  }

  private hits(document: vscode.TextDocument) {
    if (this.rootPath === undefined) return undefined;
    const path = relativeToRoot(this.rootPath, document.uri.fsPath);
    if (path === undefined) return undefined;
    const kind = manifestKind(path);
    if (kind === undefined) return undefined;
    return parseManifest(kind, document.getText());
  }

  dispose(): void {
    this.changed.dispose();
  }
}
