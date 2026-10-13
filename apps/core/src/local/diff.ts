/**
 * Lines the developer actually added, extracted from `git diff`. The local analysis is
 * scoped to them: reporting pre-existing findings as "introduced by your change" would
 * be wrong, and re-reporting the whole file is the noise that makes such tools ignored.
 */

export type ChangedFile = { path: string; addedLines: number[] };

const FILE = /^\+\+\+ b\/(.+)$/;
const REMOVED = /^\+\+\+ \/dev\/null$/;
const HEADER = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/;

/**
 * Reads a unified diff into the new-side line numbers of every added line.
 * Deleted files carry no added line and are therefore absent from the result.
 */
export function addedLines(diff: string): ChangedFile[] {
  const files: ChangedFile[] = [];
  let current: ChangedFile | undefined;
  let cursor = 0;
  for (const line of diff.split('\n')) {
    if (REMOVED.test(line)) {
      current = undefined;
      continue;
    }
    const file = FILE.exec(line);
    if (file !== null) {
      current = { path: file[1]!.replace(/\\/g, '/'), addedLines: [] };
      files.push(current);
      continue;
    }
    const header = HEADER.exec(line);
    if (header !== null) {
      cursor = Number(header[1]);
      continue;
    }
    if (current === undefined) continue;
    const marker = line.charAt(0);
    if (marker === '+') {
      current.addedLines.push(cursor);
      cursor += 1;
    } else if (marker === ' ' || line.length === 0) cursor += 1;
    // A '-' line only exists on the old side and does not move the new cursor.
  }
  return files.filter((entry) => entry.addedLines.length > 0);
}

/** Fast membership test per file, used to keep only findings on added lines. */
export function addedIndex(files: ChangedFile[]): Map<string, Set<number>> {
  return new Map(files.map((file) => [file.path, new Set(file.addedLines)]));
}

export function isAdded(index: Map<string, Set<number>>, path: string | undefined, line: number | undefined): boolean {
  if (path === undefined || line === undefined) return false;
  return index.get(path)?.has(line) === true;
}
