import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { Store } from '../../core/src/report/cache';

/**
 * Last reports kept on disk so Visual Studio can still show a dated cache when the
 * hub is unreachable. The personal token never enters this file.
 */

export class FileStore implements Store {
  private data: Record<string, unknown> = {};

  constructor(private readonly path: string) {}

  async load(): Promise<void> {
    try {
      const raw: unknown = JSON.parse(await readFile(this.path, 'utf8'));
      this.data = raw !== null && typeof raw === 'object' && !Array.isArray(raw) ? (raw as Record<string, unknown>) : {};
    } catch {
      this.data = {};
    }
  }

  get(key: string): unknown {
    return this.data[key];
  }

  async update(key: string, value: unknown): Promise<void> {
    if (value === undefined) delete this.data[key];
    else this.data[key] = value;
    await mkdir(dirname(this.path), { recursive: true });
    await writeFile(this.path, JSON.stringify(this.data), 'utf8');
  }
}
