import * as vscode from 'vscode';
import { repositoryFile } from '../../../core/src/context/paths';

/**
 * Maps a repository-relative finding path onto the open workspace. The hub stores
 * paths relative to the repository root, so the root of the Git repository — not the
 * workspace folder — is the anchor; they differ whenever a subfolder is opened.
 */
export async function locate(rootPath: string, relativePath: string): Promise<vscode.Uri | undefined> {
  const file = repositoryFile(rootPath, relativePath);
  if (file === undefined) return undefined;
  const uri = vscode.Uri.file(file);
  try {
    await vscode.workspace.fs.stat(uri);
    return uri;
  } catch {
    return undefined;
  }
}

/** Opens a file at a line, keeping the line visible without stealing the selection. */
export async function reveal(uri: vscode.Uri, line: number | undefined): Promise<void> {
  const document = await vscode.workspace.openTextDocument(uri);
  const editor = await vscode.window.showTextDocument(document, { preview: true });
  if (line === undefined) return;
  // The analysed line may no longer exist in the working tree; clamp instead of failing.
  const target = Math.min(Math.max(line - 1, 0), Math.max(document.lineCount - 1, 0));
  const range = document.lineAt(target).range;
  editor.selection = new vscode.Selection(range.start, range.start);
  editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
}
