import * as vscode from 'vscode';
import type { Workspace } from '../../../core/src/context/ports';
import { gitApi } from './git-context';

/**
 * The VS Code side of the core's workspace ports: the repository the user selected in the
 * SCM view, and the editor's file system. Shelling out to `git` here would follow another
 * repository than the one the SCM view shows.
 */
export const vscodeWorkspace: Workspace = {
  repositories: async (rootPath) => {
    const api = await gitApi();
    const repository = api?.repositories.find((candidate) => candidate.rootUri.fsPath === rootPath);
    if (repository?.diffWith === undefined) return undefined;
    const diffWith = repository.diffWith.bind(repository);
    return { diffWith: async (ref, path) => diffWith(ref, path) };
  },
  exists: async (path) => {
    try {
      await vscode.workspace.fs.stat(vscode.Uri.file(path));
      return true;
    } catch {
      return false;
    }
  },
};
