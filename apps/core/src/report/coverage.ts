import type { CoverageLine } from '../api/codeinsights';
import { relativeToRoot } from '../context/paths';
import { anchor, type Hunk } from './drift';
import { formatMetric, measurementLabel, type MeasurementState } from './measure';

/**
 * Coverage of the last analysis, optionally overlaid with a local LCOV that is newer
 * than that analysis. An unmeasured run is labelled as such, never painted as 0 %.
 */

export type CoverageOrigin = 'analysis' | 'local';

export type PlacedCoverage = { line: number; covered: boolean; origin: CoverageOrigin };

export type CoverageSummary = {
  state: MeasurementState | 'unavailable';
  origin: CoverageOrigin;
  covered: number;
  total: number;
  display: string;
};

export function coverageState(raw: string | undefined): MeasurementState | 'unavailable' {
  if (raw === 'measured' || raw === 'absent' || raw === 'failed' || raw === 'not_measured') return raw;
  if (raw === 'unavailable') return 'unavailable';
  return 'not_measured';
}

export function coverageSummary(
  state: string | undefined,
  lines: CoverageLine[],
  origin: CoverageOrigin,
): CoverageSummary {
  const measured = coverageState(state);
  if (origin === 'analysis' && measured !== 'measured') {
    return { state: measured, origin, covered: 0, total: 0, display: measurementLabel(measured === 'unavailable' ? undefined : measured) };
  }
  const total = lines.length;
  const covered = lines.filter((row) => row.covered).length;
  if (total === 0)
    return {
      state: origin === 'local' ? 'measured' : measured,
      origin,
      covered: 0,
      total: 0,
      display: origin === 'local' ? 'aucune ligne instrumentée' : measurementLabel(measured === 'unavailable' ? undefined : measured),
    };
  return {
    state: 'measured',
    origin,
    covered,
    total,
    display: `${formatMetric(total === 0 ? null : (100 * covered) / total, '%')} (${covered}/${total}${origin === 'local' ? ', LCOV local' : ''})`,
  };
}

/** Replays the analysed coverage onto the working tree; rewritten lines are dropped. */
export function placeCoverage(lines: CoverageLine[], hunks: Hunk[] | undefined, origin: CoverageOrigin): PlacedCoverage[] {
  const placed: PlacedCoverage[] = [];
  for (const row of lines) {
    const mapped = hunks === undefined ? { line: row.line, state: 'unverified' as const } : anchor(row.line, hunks);
    if (mapped.line === undefined) continue;
    if (mapped.state === 'deleted' || mapped.state === 'approximate') continue;
    placed.push({ line: mapped.line, covered: row.covered, origin });
  }
  return placed;
}

/**
 * Reads an LCOV document into repository-relative paths. Records that escape the clone
 * are dropped rather than trusted: the file comes from the workstation, not the hub.
 */
export function parseLcov(text: string, rootPath: string): Map<string, CoverageLine[]> {
  const files = new Map<string, CoverageLine[]>();
  let current: string | undefined;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith('SF:')) {
      current = relativize(line.slice(3), rootPath);
      if (current !== undefined && !files.has(current)) files.set(current, []);
      continue;
    }
    if (line === 'end_of_record') {
      current = undefined;
      continue;
    }
    if (current === undefined || !line.startsWith('DA:')) continue;
    const [number, hits] = line.slice(3).split(',');
    const parsed = Number(number);
    if (!Number.isInteger(parsed) || parsed < 1) continue;
    files.get(current)?.push({ line: parsed, covered: Number(hits) > 0 });
  }
  return files;
}

function relativize(path: string, rootPath: string): string | undefined {
  const normalised = path.replace(/\\/g, '/');
  const relative = relativeToRoot(rootPath, normalised);
  if (relative !== undefined) return relative;
  if (normalised.startsWith('/') || /^[a-zA-Z]:/.test(normalised)) return undefined;
  return normalised.replace(/^\.\//, '');
}

/** Local LCOV wins only when it is strictly newer than the analysed report. */
export function preferLocal(localMtime: number | undefined, analysedAt: string | undefined, now: number): boolean {
  if (localMtime === undefined || !Number.isFinite(localMtime)) return false;
  if (analysedAt === undefined || analysedAt.length === 0) return localMtime <= now;
  const analysed = Date.parse(analysedAt);
  if (Number.isNaN(analysed)) return localMtime <= now;
  return localMtime > analysed && localMtime <= now;
}
