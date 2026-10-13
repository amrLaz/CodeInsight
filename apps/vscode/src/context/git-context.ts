import * as vscode from 'vscode';

/**
 * Reads the repository, branch and commit of a workspace folder through the
 * built-in Git extension. Shelling out to `git` is avoided so the extension
 * follows the repository the user actually selected in the SCM view.
 */
export type GitContext = {
  folder: vscode.WorkspaceFolder;
  rootPath: string;
  branch?: string;
  headSha?: string;
  repositoryUrl?: string;
};

type Repository = {
  rootUri: vscode.Uri;
  state: { HEAD?: { name?: string; commit?: string }; remotes: { name: string; fetchUrl?: string; pushUrl?: string }[] };
  /** Diff between a revision and the working tree, for one path or for the whole tree. */
  diffWith?: (ref: string, path?: string) => Promise<string>;
};

export type GitApi = { repositories: Repository[]; onDidOpenRepository: vscode.Event<Repository>; onDidCloseRepository: vscode.Event<Repository> };

export async function gitApi(): Promise<GitApi | undefined> {
  const extension = vscode.extensions.getExtension<{ getAPI(version: 1): GitApi }>('vscode.git');
  if (extension === undefined) return undefined;
  const exports = extension.isActive ? extension.exports : await extension.activate();
  return exports.getAPI(1);
}

export async function gitContext(folder: vscode.WorkspaceFolder): Promise<GitContext | undefined> {
  const api = await gitApi();
  if (api === undefined) return undefined;
  const repository = api.repositories.find((candidate) => folder.uri.fsPath.startsWith(candidate.rootUri.fsPath));
  if (repository === undefined) return undefined;
  const remote = repository.state.remotes.find((candidate) => candidate.name === 'origin') ?? repository.state.remotes[0];
  const context: GitContext = { folder, rootPath: repository.rootUri.fsPath };
  const branch = repository.state.HEAD?.name;
  if (branch !== undefined) context.branch = branch;
  const commit = repository.state.HEAD?.commit;
  if (commit !== undefined) context.headSha = commit;
  const url = remote?.fetchUrl ?? remote?.pushUrl;
  if (url !== undefined) context.repositoryUrl = url;
  return context;
}
