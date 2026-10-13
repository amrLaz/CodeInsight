import type { RepositoryAccess } from './ports';
import { parseHunks, type Hunk } from '../report/drift';

/**
 * Diff between the analysed commit and the working tree, read through the host's
 * repository access. The analysed commit is often absent from a shallow or stale clone:
 * that case returns undefined so positions are labelled unverified rather than invented.
 */
export class DiffSource {
  private readonly cache = new Map<string, Hunk[] | undefined>();

  constructor(
    private readonly rootPath: string,
    private readonly repositories: RepositoryAccess,
  ) {}

  clear(): void {
    this.cache.clear();
  }

  async hunks(sha: string, path: string): Promise<Hunk[] | undefined> {
    const key = `${sha}:${path}`;
    if (this.cache.has(key)) return this.cache.get(key);
    const value = await this.read(sha, path);
    this.cache.set(key, value);
    return value;
  }

  private async read(sha: string, path: string): Promise<Hunk[] | undefined> {
    if (sha.length === 0) return undefined;
    const repository = await this.repositories(this.rootPath);
    if (repository === undefined) return undefined;
    try {
      return parseHunks(await repository.diffWith(sha, path));
    } catch {
      // Unknown revision, unreadable path or an older Git extension: no mapping.
      return undefined;
    }
  }
}
