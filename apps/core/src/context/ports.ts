/**
 * The two accesses to the workstation that the shared core needs but cannot implement:
 * reading a diff and telling whether a file exists. VS Code answers them through its
 * built-in Git extension and `workspace.fs`, the MCP server through `git` and `node:fs`.
 * A port rather than two implementations of the analysis itself: the IDE and the agent
 * must never conclude differently on the same working tree.
 */

export type Repository = {
  /** Diff between a revision and the working tree, for one path or for the whole tree. */
  diffWith(ref: string, path?: string): Promise<string>;
};

/** Repository of a working tree, or undefined when none is readable there. */
export type RepositoryAccess = (rootPath: string) => Promise<Repository | undefined>;

/** Existence of an absolute path on the workstation. Never throws: absent is a result. */
export type FileProbe = (path: string) => Promise<boolean>;

export type Workspace = { repositories: RepositoryAccess; exists: FileProbe };
