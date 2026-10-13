import * as vscode from 'vscode';
import { details, detailsHtml } from '../../../core/src/report/details';
import type { AnchorState } from '../../../core/src/report/drift';
import type { Issue } from '../../../core/src/report/issues';

/** Details of the selected issue, rendered with the editor's own theme variables. */
export class DetailsView implements vscode.WebviewViewProvider {
  private view: vscode.WebviewView | undefined;
  private current: { issue: Issue; state: AnchorState | undefined; commitSha: string | undefined } | undefined;

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = { enableScripts: false };
    this.render();
  }

  show(issue: Issue | undefined, state: AnchorState | undefined, commitSha: string | undefined): void {
    this.current = issue === undefined ? undefined : { issue, state, commitSha };
    this.render();
  }

  private render(): void {
    if (this.view === undefined) return;
    this.view.webview.html = detailsHtml(
      this.current === undefined ? undefined : details(this.current.issue, this.current.state, this.current.commitSha),
    );
  }
}
