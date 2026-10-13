/**
 * Last report kept per project and branch so the IDE still shows something when the
 * hub is unreachable. A cached report is always presented with its age: a stale
 * verdict passed off as current would be worse than no verdict at all.
 */

/** Matches vscode.Memento structurally — `Thenable` is the host's name for `PromiseLike`. */
export type Store = {
  get(key: string): unknown;
  update(key: string, value: unknown): PromiseLike<void> | void;
};

export type Cached<T> = { value: T; storedAt: number };

export function cacheKey(projectKey: string, branch: string): string {
  return `codeinsights.report:${projectKey}:${branch}`;
}

export function issuesCacheKey(projectKey: string, branch: string): string {
  return `codeinsights.issues:${projectKey}:${branch}`;
}

export async function writeCache<T>(store: Store, key: string, value: T, now: number): Promise<void> {
  await store.update(key, { value, storedAt: now } satisfies Cached<T>);
}

export function readCache<T>(store: Store, key: string): Cached<T> | undefined {
  const raw = store.get(key);
  if (raw === null || typeof raw !== 'object') return undefined;
  const candidate = raw as Partial<Cached<T>>;
  if (typeof candidate.storedAt !== 'number' || candidate.value === undefined) return undefined;
  return { value: candidate.value as T, storedAt: candidate.storedAt };
}

/** Human age of a cached entry, used to label offline data. */
export function ageLabel(storedAt: number, now: number): string {
  const seconds = Math.max(0, Math.round((now - storedAt) / 1000));
  if (seconds < 90) return "il y a moins d'une minute";
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `il y a ${minutes} minutes`;
  const hours = Math.round(minutes / 60);
  if (hours < 36) return `il y a ${hours} heures`;
  return `il y a ${Math.round(hours / 24)} jours`;
}
