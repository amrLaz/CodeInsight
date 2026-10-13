import { resolveProjectKey, type ProjectKeySources } from './project-key';

/**
 * Full resolution order: the explicit setting, then the workspace mapping, then the
 * hub lookup by clone URL. Ambiguity is reported rather than guessed — showing the
 * wrong project's gate is worse than showing none.
 */

export type ResolutionSource = 'setting' | 'mapping' | 'repository';

export type Resolution =
  | { state: 'resolved'; projectKey: string; source: ResolutionSource }
  | { state: 'ambiguous'; candidates: string[] }
  | { state: 'unresolved'; reason: 'not-configured' | 'no-repository' | 'unknown-repository' | 'lookup-failed'; detail?: string };

export type Lookup = (repositoryUrl: string) => Promise<{ key: string }[]>;

export async function resolveProject(sources: ProjectKeySources, lookup: Lookup | undefined): Promise<Resolution> {
  const local = resolveProjectKey(sources);
  if (local.state === 'resolved') return local;
  const repositoryUrl = sources.repositoryUrl?.trim() ?? '';
  if (repositoryUrl.length === 0) return { state: 'unresolved', reason: 'no-repository' };
  if (lookup === undefined) return { state: 'unresolved', reason: 'not-configured' };
  let matches: { key: string }[];
  try {
    matches = await lookup(repositoryUrl);
  } catch (error) {
    return { state: 'unresolved', reason: 'lookup-failed', detail: error instanceof Error ? error.message : String(error) };
  }
  const keys = [...new Set(matches.map((match) => match.key).filter((key) => key.length > 0))].sort();
  if (keys.length === 0) return { state: 'unresolved', reason: 'unknown-repository' };
  if (keys.length > 1) return { state: 'ambiguous', candidates: keys };
  return { state: 'resolved', projectKey: keys[0]!, source: 'repository' };
}

export function resolutionLabel(resolution: Resolution): string {
  switch (resolution.state) {
    case 'resolved':
      return resolution.projectKey;
    case 'ambiguous':
      return `plusieurs projets pour ce dépôt : ${resolution.candidates.join(', ')}`;
    default:
      switch (resolution.reason) {
        case 'no-repository':
          return 'aucun dépôt Git détecté';
        case 'unknown-repository':
          return 'aucun projet lié à ce dépôt';
        case 'lookup-failed':
          return 'résolution impossible';
        default:
          return 'projet non configuré';
      }
  }
}
