/**
 * Resolves which CodeInsights project a workspace folder belongs to. Until the
 * hub exposes a repository lookup (lot L1), the setting and the workspace
 * mapping file are the only sources; guessing from a folder name would silently
 * report another project's gate.
 */

/**
 * `.codeinsights/vscode.json`, versionné avec le dépôt : c'est la configuration du projet
 * lui-même, celle que lisent l'IDE et l'agent. `projectKey` lie le dépôt entier ; `projects`
 * lie un dossier ou une URL de clone, ce dont un monorepo a besoin.
 */
export type ProjectMapping = { projectKey?: string; projects?: Record<string, string> };

export type ProjectKeySources = {
  setting?: string;
  mapping?: ProjectMapping;
  folderName?: string;
  repositoryUrl?: string;
};

export type ProjectKeyResolution =
  | { state: 'resolved'; projectKey: string; source: 'setting' | 'mapping' }
  | { state: 'unresolved'; reason: 'not-configured' };

/** Normalises a repository URL so that SSH, HTTPS and trailing `.git` forms compare equal. */
export function normalizeRepositoryUrl(url: string): string {
  const trimmed = url.trim().replace(/\/+$/, '').replace(/\.git$/i, '').replace(/\/+$/, '');
  if (trimmed.length === 0) return '';
  const ssh = /^[^@\s]+@([^:]+):(.+)$/.exec(trimmed);
  const normalized = ssh === null ? trimmed : `https://${ssh[1]}/${ssh[2]}`;
  return normalized.replace(/^ssh:\/\//i, 'https://').toLowerCase();
}

export function resolveProjectKey(sources: ProjectKeySources): ProjectKeyResolution {
  const setting = sources.setting?.trim() ?? '';
  if (setting.length > 0) return { state: 'resolved', projectKey: setting, source: 'setting' };

  const projects = sources.mapping?.projects ?? {};
  const repository = sources.repositoryUrl === undefined ? '' : normalizeRepositoryUrl(sources.repositoryUrl);
  for (const [key, value] of Object.entries(projects)) {
    const candidate = key.trim();
    if (candidate.length === 0 || typeof value !== 'string') continue;
    if (candidate === sources.folderName) return { state: 'resolved', projectKey: value, source: 'mapping' };
    if (repository.length > 0 && normalizeRepositoryUrl(candidate) === repository)
      return { state: 'resolved', projectKey: value, source: 'mapping' };
  }

  // Le dépôt se nomme lui-même, après ses dossiers : dans un monorepo qui déclare les deux,
  // la correspondance de dossier est la plus précise et doit l'emporter sur la racine.
  const declared = sources.mapping?.projectKey?.trim() ?? '';
  if (declared.length > 0) return { state: 'resolved', projectKey: declared, source: 'mapping' };

  return { state: 'unresolved', reason: 'not-configured' };
}
