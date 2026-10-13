import type { RawFinding } from '../api/codeinsights';
import { locationsSuffix, toLocations, type IssueLocation } from './locations';
import { palette } from './palette';

/**
 * Issue list of the current branch: server-side filters, severity normalisation and
 * grouping by file. The severity vocabulary is the one the hub screens use
 * (apps/web/src/components/Issues.tsx); diverging here would rank issues differently
 * in the IDE and in the hub for the same analysis.
 */

export type Severity = 'blocker' | 'high' | 'medium' | 'low' | 'info';
export type SeverityFilter = Severity | 'all';
export type CategoryFilter = 'all' | 'bug' | 'code_smell' | 'vulnerability' | 'security_hotspot' | 'secret' | 'dependency' | 'code' | 'ioc';
export type StateFilter = 'all' | 'open' | 'confirmed' | 'resolved' | 'false_positive' | 'accepted_risk';

export type IssueFilters = { severity: SeverityFilter; category: CategoryFilter; state: StateFilter; mine: boolean };

export const defaultIssueFilters: IssueFilters = { severity: 'all', category: 'all', state: 'all', mine: false };

export type Issue = {
  id: number;
  severity: Severity;
  category: string;
  rule: string;
  tool: string;
  message: string;
  path: string | undefined;
  line: number | undefined;
  excepted: boolean;
  documentationUrl: string | undefined;
  /** The hub keeps the analysed source for this issue, so it can build the AI prompt. */
  promptAvailable: boolean;
  promptUnavailableReason: string | undefined;
  locations: IssueLocation[];
  locationsTruncated: boolean;
  /** `[+3 locations]`, empty when the producer sent only the primary one. */
  locationsSuffix: string;
};

export type IssueGroup = { path: string | undefined; label: string; issues: Issue[]; worst: Severity };

const ORDER: Severity[] = ['blocker', 'high', 'medium', 'low', 'info'];

export function severity(raw: string | undefined): Severity {
  switch ((raw ?? '').toLowerCase()) {
    case 'critical':
    case 'error':
    case 'blocker':
      return 'blocker';
    case 'high':
      return 'high';
    case 'warning':
    case 'medium':
      return 'medium';
    case 'low':
    case 'convention':
      return 'low';
    default:
      return 'info';
  }
}

export function severityLabel(value: Severity): string {
  switch (value) {
    case 'blocker':
      return 'Blocker';
    case 'high':
      return 'High';
    case 'medium':
      return 'Medium';
    case 'low':
      return 'Low';
    default:
      return 'Info';
  }
}

export function severityIcon(value: Severity): string {
  return value === 'blocker' || value === 'high' ? 'error' : value === 'medium' ? 'warning' : 'info';
}

/**
 * Theme colour id painting the icon of a severity. The `charts.*` palette is the one
 * the themes guarantee to keep readable on both light and dark backgrounds; high takes
 * the deep red contributed by the extension, so it stays distinct from the brighter
 * blocker red it shares the `error` icon with.
 */
export function severityColor(value: Severity): string {
  switch (value) {
    case 'blocker':
      return palette.fail;
    case 'high':
      return palette.high;
    case 'medium':
      return palette.watch;
    case 'low':
      return palette.info;
    default:
      return palette.muted;
  }
}

export function toIssue(raw: RawFinding): Issue {
  const path = raw.path === null || raw.path === undefined || raw.path.length === 0 ? undefined : raw.path.replace(/\\/g, '/');
  const line = typeof raw.line === 'number' && raw.line > 0 ? raw.line : undefined;
  const documentation = raw.evidence?.documentation_url;
  const locations = toLocations(raw.evidence);
  const truncated = raw.evidence?.locations_truncated === true;
  return {
    id: raw.id,
    severity: severity(raw.severity),
    category: raw.category ?? 'unknown',
    rule: raw.rule_id ?? 'unknown',
    tool: raw.tool ?? 'unknown',
    message: raw.message ?? '',
    path,
    line,
    excepted: raw.excepted === true,
    documentationUrl: documentation === null || documentation === undefined || documentation.length === 0 ? undefined : documentation,
    locations,
    locationsTruncated: truncated,
    locationsSuffix: locationsSuffix(locations, truncated),
    promptAvailable: raw.prompt_available === true,
    promptUnavailableReason:
      raw.prompt_unavailable_reason === null || raw.prompt_unavailable_reason === undefined || raw.prompt_unavailable_reason.length === 0
        ? undefined
        : raw.prompt_unavailable_reason,
  };
}

/** Groups by file, worst severity first, then by path and line: the reading order of a review. */
export function groupIssues(issues: Issue[]): IssueGroup[] {
  const groups = new Map<string, Issue[]>();
  for (const issue of issues) {
    const key = issue.path ?? '';
    const bucket = groups.get(key);
    if (bucket === undefined) groups.set(key, [issue]);
    else bucket.push(issue);
  }
  const result: IssueGroup[] = [];
  for (const [key, bucket] of groups) {
    bucket.sort((a, b) => ORDER.indexOf(a.severity) - ORDER.indexOf(b.severity) || (a.line ?? 0) - (b.line ?? 0) || a.id - b.id);
    const worst = bucket.reduce<Severity>((acc, issue) => (ORDER.indexOf(issue.severity) < ORDER.indexOf(acc) ? issue.severity : acc), 'info');
    result.push({
      path: key.length === 0 ? undefined : key,
      label: key.length === 0 ? 'Localisation indisponible' : key,
      issues: bucket,
      worst,
    });
  }
  result.sort((a, b) => ORDER.indexOf(a.worst) - ORDER.indexOf(b.worst) || (a.path ?? '').localeCompare(b.path ?? ''));
  return result;
}

/** Server-side filter query; the IDE never filters a page it already truncated. */
export function issuesQuery(filters: IssueFilters): string {
  const params = new URLSearchParams();
  params.set('limit', '100');
  if (filters.severity !== 'all') params.set('severity', filters.severity);
  if (filters.category !== 'all') params.set('category', filters.category);
  if (filters.state !== 'all') params.set('state', filters.state);
  if (filters.mine) params.set('mine', 'true');
  return params.toString();
}

/** One line describing the active filters, shown as the view description. */
export function filtersLabel(filters: IssueFilters): string {
  const parts: string[] = [];
  if (filters.severity !== 'all') parts.push(severityLabel(filters.severity));
  if (filters.category !== 'all') parts.push(filters.category);
  if (filters.state !== 'all') parts.push(filters.state);
  if (filters.mine) parts.push('mes issues');
  return parts.length === 0 ? 'toutes les issues' : parts.join(' · ');
}

export function countLabel(groups: IssueGroup[]): string {
  const issues = groups.reduce((total, group) => total + group.issues.length, 0);
  const blockers = groups.reduce((total, group) => total + group.issues.filter((issue) => issue.severity === 'blocker').length, 0);
  if (issues === 0) return 'aucune issue';
  const files = groups.length === 1 ? '1 fichier' : `${groups.length} fichiers`;
  const base = `${issues === 1 ? '1 issue' : `${issues} issues`} · ${files}`;
  return blockers === 0 ? base : `${base} · ${blockers === 1 ? '1 bloquante' : `${blockers} bloquantes`}`;
}
