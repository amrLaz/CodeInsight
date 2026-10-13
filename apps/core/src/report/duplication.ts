import type { DuplicateBlock } from '../api/codeinsights';
import { anchor, type Hunk } from './drift';

/**
 * Duplicated ranges of the last analysis that still map onto the open file. A block
 * whose analysed lines were rewritten is dropped rather than painted on new code.
 */

export type PlacedDuplicate = {
  start: number;
  end: number;
  otherPath: string;
  otherStart: number;
  otherEnd: number;
};

export function blocksFor(path: string, blocks: DuplicateBlock[]): { start: number; end: number; other: { path: string; start: number; end: number } }[] {
  const matches: { start: number; end: number; other: { path: string; start: number; end: number } }[] = [];
  for (const block of blocks) {
    const first = location(block.first);
    const second = location(block.second);
    if (first === undefined || second === undefined) continue;
    if (first.path === path) matches.push({ start: first.start, end: first.end, other: second });
    if (second.path === path && (second.path !== first.path || second.start !== first.start))
      matches.push({ start: second.start, end: second.end, other: first });
  }
  return matches;
}

export function placeDuplicates(
  path: string,
  blocks: DuplicateBlock[],
  hunks: Hunk[] | undefined,
): PlacedDuplicate[] {
  const placed: PlacedDuplicate[] = [];
  for (const block of blocksFor(path, blocks)) {
    const start = hunks === undefined ? { line: block.start, state: 'unverified' as const } : anchor(block.start, hunks);
    const end = hunks === undefined ? { line: block.end, state: 'unverified' as const } : anchor(block.end, hunks);
    if (start.line === undefined || end.line === undefined) continue;
    if (start.state === 'deleted' || start.state === 'approximate' || end.state === 'deleted' || end.state === 'approximate')
      continue;
    placed.push({
      start: start.line,
      end: Math.max(end.line, start.line),
      otherPath: block.other.path,
      otherStart: block.other.start,
      otherEnd: block.other.end,
    });
  }
  return placed;
}

export function duplicateLabel(block: PlacedDuplicate): string {
  return `Bloc dupliqué avec ${block.otherPath}:${block.otherStart}–${block.otherEnd} (analyse)`;
}

function location(raw: DuplicateBlock['first']): { path: string; start: number; end: number } | undefined {
  if (raw?.path === undefined || raw.path.length === 0) return undefined;
  const start = raw.start_line;
  const end = raw.end_line ?? start;
  if (start === undefined || start < 1 || end === undefined || end < 1) return undefined;
  return { path: raw.path.replace(/\\/g, '/'), start, end: Math.max(end, start) };
}
