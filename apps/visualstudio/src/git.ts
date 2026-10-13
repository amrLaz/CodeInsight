import { execFile } from 'node:child_process';
import { access } from 'node:fs/promises';
import { promisify } from 'node:util';
import type { Workspace } from '../../core/src/context/ports';

/**
 * Visual Studio has no vscode.git API. The same git binary the MCP server uses answers
 * the core ports, so the IDE and the agent cannot diverge on the same working tree.
 */

const run = promisify(execFile);
const MAX_BUFFER = 32 * 1024 * 1024;

export class GitUnavailable extends Error {}

export async function git(cwd: string, args: string[]): Promise<string> {
  try {
    const { stdout } = await run('git', args, { cwd, maxBuffer: MAX_BUFFER, windowsHide: true });
    return stdout;
  } catch (error) {
    const code = (error as { code?: unknown }).code;
    if (code === 'ENOENT') throw new GitUnavailable('`git` est introuvable dans le PATH de Visual Studio.');
    throw error;
  }
}

export async function repositoryRoot(cwd: string): Promise<string | undefined> {
  try {
    return (await git(cwd, ['rev-parse', '--show-toplevel'])).trim().replace(/\/+$/, '');
  } catch (error) {
    if (error instanceof GitUnavailable) throw error;
    return undefined;
  }
}

export async function currentBranch(rootPath: string): Promise<string | undefined> {
  try {
    const name = (await git(rootPath, ['rev-parse', '--abbrev-ref', 'HEAD'])).trim();
    return name.length === 0 || name === 'HEAD' ? undefined : name;
  } catch {
    return undefined;
  }
}

export async function headSha(rootPath: string): Promise<string | undefined> {
  try {
    const sha = (await git(rootPath, ['rev-parse', 'HEAD'])).trim();
    return sha.length === 0 ? undefined : sha;
  } catch {
    return undefined;
  }
}

export async function remoteUrl(rootPath: string): Promise<string | undefined> {
  for (const args of [
    ['remote', 'get-url', 'origin'],
    ['remote'],
  ]) {
    try {
      const output = (await git(rootPath, args)).trim();
      if (output.length === 0) continue;
      if (args[0] === 'remote' && args.length === 1) {
        const first = output.split('\n')[0]?.trim();
        if (first === undefined || first.length === 0) continue;
        const url = (await git(rootPath, ['remote', 'get-url', first])).trim();
        if (url.length > 0) return url;
        continue;
      }
      return output;
    } catch {
      continue;
    }
  }
  return undefined;
}

export const nodeWorkspace: Workspace = {
  repositories: async (rootPath) => {
    const root = await repositoryRoot(rootPath);
    if (root === undefined) return undefined;
    return {
      diffWith: async (ref, path) => git(root, path === undefined ? ['diff', ref] : ['diff', ref, '--', path]),
    };
  },
  exists: async (path) => {
    try {
      await access(path);
      return true;
    } catch {
      return false;
    }
  },
};
