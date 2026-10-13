/**
 * Findings are anchored to a line of the analysed commit; the working tree has moved on.
 * Rather than trusting the stored line or dropping the issue, the diff between the two
 * revisions is replayed line by line: an unchanged line is mapped exactly, a rewritten
 * or removed one is reported as approximate. Reading only the hunk headers would be
 * safe but far too coarse — git's three context lines would make unchanged code
 * approximate too.
 */

export type Hunk = { oldStart: number; newStart: number; ops: string };

export type AnchorState = 'exact' | 'shifted' | 'approximate' | 'unverified' | 'deleted';

export type Anchor = { line: number | undefined; state: AnchorState };

const HEADER = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;

/**
 * Reads a unified diff into hunks whose `ops` keeps one character per body line:
 * a space for context, `-` for the analysed side only, `+` for the working tree only.
 */
export function parseHunks(diff: string): Hunk[] {
  const hunks: Hunk[] = [];
  let current: { oldStart: number; newStart: number; ops: string[] } | undefined;
  for (const line of diff.split('\n')) {
    const match = HEADER.exec(line);
    if (match !== null) {
      if (current !== undefined) hunks.push({ oldStart: current.oldStart, newStart: current.newStart, ops: current.ops.join('') });
      current = { oldStart: Number(match[1]), newStart: Number(match[2]), ops: [] };
      continue;
    }
    if (current === undefined) continue;
    const marker = line.charAt(0);
    // `\ No newline at end of file` and the trailing empty split are not body lines.
    if (marker === '+' || marker === '-' || marker === ' ') current.ops.push(marker);
    else if (line.length === 0) current.ops.push(' ');
  }
  if (current !== undefined) hunks.push({ oldStart: current.oldStart, newStart: current.newStart, ops: current.ops.join('') });
  return hunks.sort((a, b) => a.oldStart - b.oldStart);
}

/**
 * Maps a line of the analysed revision onto the working tree.
 * `diff` is `git diff <analysed sha> -- <path>`: old side analysed, new side local.
 */
export function anchor(line: number | undefined, hunks: Hunk[] | undefined): Anchor {
  if (line === undefined || line < 1) return { line: undefined, state: 'unverified' };
  if (hunks === undefined) return { line, state: 'unverified' };
  let delta = 0;
  for (const hunk of hunks) {
    if (line < hunk.oldStart) break;
    let oldCursor = hunk.oldStart;
    let newCursor = hunk.newStart;
    for (const op of hunk.ops) {
      if (op === '+') {
        newCursor += 1;
        continue;
      }
      if (oldCursor === line)
        return op === ' '
          ? { line: newCursor, state: newCursor === line ? 'exact' : 'shifted' }
          : // The analysed line itself was removed: the nearest surviving position is a guess.
            { line: Math.max(newCursor, 1), state: hunk.ops.includes('+') ? 'approximate' : 'deleted' };
      oldCursor += 1;
      if (op === ' ') newCursor += 1;
    }
    delta = newCursor - oldCursor;
  }
  const mapped = line + delta;
  return mapped < 1 ? { line: 1, state: 'approximate' } : { line: mapped, state: delta === 0 ? 'exact' : 'shifted' };
}

export function anchorNote(state: AnchorState): string | undefined {
  switch (state) {
    case 'approximate':
      return 'position approximative : la ligne analysée a changé';
    case 'deleted':
      return 'position approximative : la ligne analysée a été supprimée';
    case 'unverified':
      return 'position non vérifiée : le commit analysé est indisponible localement';
    default:
      return undefined;
  }
}
