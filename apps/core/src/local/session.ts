import type { Workspace } from '../context/ports';
import { repositoryFile } from '../context/paths';
import { addedLines, type ChangedFile } from './diff';
import { buildReport, type Exception, type LocalReport } from './analysis';
import { isPathConfig, runGitleaks, runSemgrep } from './runner';
import type { LocalFinding } from './producers';

/**
 * One local run: the pending diff, the producers, then the report. Nothing leaves the
 * machine — the hub is only read, for the project's exceptions and disabled rules.
 */
export type LocalSettings = {
  semgrepConfigs: string[];
  useDocker: boolean;
  images: { semgrep: string; gitleaks: string };
  timeoutMs: number;
  maxFiles: number;
};

export type LocalRun = { report: LocalReport; changed: ChangedFile[]; baseRef: string };

/** Diff of the pending change against the merge base with the target branch. */
export async function pendingDiff(
  workspace: Workspace,
  rootPath: string,
  targetBranch: string,
): Promise<{ files: ChangedFile[]; baseRef: string } | undefined> {
  const repository = await workspace.repositories(rootPath);
  if (repository === undefined) return undefined;
  for (const ref of [`${targetBranch}...`, targetBranch, 'HEAD']) {
    try {
      // `diffWith(ref)` compares that revision with the working tree, uncommitted work included.
      const diff = await repository.diffWith(ref === `${targetBranch}...` ? targetBranch : ref);
      return { files: addedLines(diff), baseRef: ref === `${targetBranch}...` ? targetBranch : ref };
    } catch {
      continue;
    }
  }
  return undefined;
}

export async function analyseChanges(
  workspace: Workspace,
  rootPath: string,
  targetBranch: string,
  settings: LocalSettings,
  hub: { exceptions: Exception[]; disabledRules: string[] },
  log: (message: string) => void,
  progress?: (message: string) => void,
): Promise<LocalRun | undefined> {
  const diff = await pendingDiff(workspace, rootPath, targetBranch);
  if (diff === undefined) return undefined;
  // Only files that still exist locally are handed to a scanner.
  const files: string[] = [];
  for (const file of diff.files.slice(0, settings.maxFiles)) {
    const resolved = repositoryFile(rootPath, file.path);
    if (resolved === undefined) continue;
    if (await workspace.exists(resolved)) files.push(file.path);
  }
  // A rule file that does not exist here is dropped: semgrep would fail on it, and a
  // failure reads as "something went wrong" when the truth is "no rules for this repo".
  const configs: string[] = [];
  for (const config of settings.semgrepConfigs) {
    if (!isPathConfig(config)) {
      configs.push(config);
      continue;
    }
    const resolved = config.startsWith('/') ? config : `${rootPath.replace(/\/+$/, '')}/${config.replace(/^\.\//, '')}`;
    if (await workspace.exists(resolved)) configs.push(config);
    else log(`règles semgrep introuvables : ${config}`);
  }
  const options = {
    rootPath,
    files,
    semgrepConfigs: configs,
    useDocker: settings.useDocker,
    images: settings.images,
    timeoutMs: settings.timeoutMs,
    log,
  };
  progress?.('semgrep');
  const semgrep = await runSemgrep(options);
  progress?.('gitleaks');
  const gitleaks = await runGitleaks(options);
  return {
    report: buildReport([semgrep, gitleaks], diff.files, hub.exceptions, hub.disabledRules),
    changed: diff.files,
    baseRef: diff.baseRef,
  };
}

export type { LocalFinding, LocalReport };
