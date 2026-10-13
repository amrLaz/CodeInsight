import * as vscode from 'vscode';

/**
 * Workspace folder of the current editor, then the first folder. Multi-root workspaces
 * keep one CodeInsights project per folder; guessing across roots would mix gates.
 */
export function activeFolder(editor?: vscode.TextEditor | undefined): vscode.WorkspaceFolder | undefined {
  const folders = vscode.workspace.workspaceFolders;
  if (folders === undefined || folders.length === 0) return undefined;
  const document = editor?.document ?? vscode.window.activeTextEditor?.document;
  if (document !== undefined) {
    const folder = vscode.workspace.getWorkspaceFolder(document.uri);
    if (folder !== undefined) return folder;
  }
  return folders[0];
}
