import type { RawEvidence, RawEvidenceLocation } from '../api/codeinsights';

/**
 * Secondary locations and code flows carried by a finding. The producer sends them in
 * the SARIF sense: one primary location, related ones, and ordered flow steps. They are
 * displayed as received — no location is inferred, reordered or completed.
 */

export type LocationKind = 'primary' | 'related' | 'flow';

export type IssueLocation = {
  kind: LocationKind;
  path: string | undefined;
  line: number | undefined;
  endLine: number | undefined;
  column: number | undefined;
  endColumn: number | undefined;
  flow: number | undefined;
  thread: number | undefined;
  step: number;
};

export type CodeFlow = { flow: number | undefined; thread: number | undefined; steps: IssueLocation[] };

function positive(value: number | null | undefined): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : undefined;
}

function kindOf(raw: string | undefined): LocationKind {
  return raw === 'primary' || raw === 'related' || raw === 'flow' ? raw : 'related';
}

export function toLocation(raw: RawEvidenceLocation): IssueLocation {
  const path = raw.path === null || raw.path === undefined || raw.path.length === 0 ? undefined : raw.path.replace(/\\/g, '/');
  return {
    kind: kindOf(raw.kind),
    path,
    line: positive(raw.line),
    endLine: positive(raw.end_line),
    column: positive(raw.column),
    endColumn: positive(raw.end_column),
    flow: positive(raw.flow),
    thread: positive(raw.thread),
    step: typeof raw.step === 'number' ? raw.step : 0,
  };
}

export function toLocations(evidence: RawEvidence | null | undefined): IssueLocation[] {
  return (evidence?.locations ?? []).map(toLocation);
}

/** Locations other than the primary one, in the order the producer sent them. */
export function secondary(locations: IssueLocation[]): IssueLocation[] {
  return locations.filter((location) => location.kind !== 'primary');
}

/** Flow steps grouped by flow and thread, each ordered by step. */
export function flows(locations: IssueLocation[]): CodeFlow[] {
  const groups = new Map<string, CodeFlow>();
  for (const location of locations.filter((candidate) => candidate.kind === 'flow')) {
    const key = `${location.flow ?? 0}:${location.thread ?? 0}`;
    const group = groups.get(key);
    if (group === undefined) groups.set(key, { flow: location.flow, thread: location.thread, steps: [location] });
    else group.steps.push(location);
  }
  for (const group of groups.values()) group.steps.sort((a, b) => a.step - b.step);
  return [...groups.values()].sort((a, b) => (a.flow ?? 0) - (b.flow ?? 0) || (a.thread ?? 0) - (b.thread ?? 0));
}

/** Suffix of the issue label, as SARIF viewers write it: `[+3 locations]`. */
export function locationsSuffix(locations: IssueLocation[], truncated: boolean): string {
  const count = secondary(locations).length;
  if (count === 0) return truncated ? ' [locations tronquées]' : '';
  return ` [+${count} location${count > 1 ? 's' : ''}${truncated ? ', tronquées' : ''}]`;
}

/**
 * `step` is the index the hub assigned while normalising the report, so it is rendered
 * as a rank in the displayed order rather than echoed as an internal number.
 */
export function locationLabel(location: IssueLocation, ordinal?: number): string {
  const position = location.line === undefined ? 'position inconnue' : `L${location.line}${location.column === undefined ? '' : `, Col ${location.column}`}`;
  const step = location.kind === 'flow' && ordinal !== undefined ? `Étape ${ordinal} · ` : '';
  return `${step}${location.path ?? 'fichier inconnu'} · ${position}`;
}

/**
 * Range of the primary location when the producer gave columns, so the squiggle covers
 * the expression instead of the whole line. Columns are 1-based in SARIF.
 */
export function primaryRange(
  locations: IssueLocation[],
  line: number | undefined,
): { column: number; endColumn: number | undefined } | undefined {
  const primary = locations.find((location) => location.kind === 'primary' && location.line === line && location.column !== undefined);
  if (primary?.column === undefined) return undefined;
  const endColumn = primary.endColumn !== undefined && primary.endColumn > primary.column ? primary.endColumn : undefined;
  return endColumn === undefined ? { column: primary.column, endColumn: undefined } : { column: primary.column, endColumn };
}
