export type RatingDimension = 'security' | 'reliability' | 'maintainability' | 'security_review';
type RatingCounts = { a: number; b: number; c: number; d: number; e: number; not_rated: number };
export type PortfolioDay = {
  day: string;
  projects: number;
  ratings: Record<RatingDimension, RatingCounts>;
  findings: { projects: number; blocker: number | null; high: number | null; medium: number | null; low: number | null; info: number | null; unknown: number | null } | null;
  sla: { projects: number; breached: number | null; due_soon: number | null } | null;
  coverage_projects: number;
  duplication_projects: number;
};
export type PortfolioProject = {
  project_key: string;
  day: string;
  analysis_id: string;
  source: 'live' | 'reconstructed';
  security_rating: string | null;
  reliability_rating: string | null;
  maintainability_rating: string | null;
  security_review_rating: string | null;
  coverage: number | null;
  coverage_start: number | null;
  duplication: number | null;
  duplication_start: number | null;
  start_day: string | null;
  blocker_findings: number | null;
  high_findings: number | null;
  sla_breached: number | null;
};
export type PortfolioResponse = { from: string; to: string; projects_without_main_branch: number; days: PortfolioDay[]; projects: PortfolioProject[] };
export type TrendSeries = { key: string; label: string; color: string; dash?: string };
export type TrendRow = Record<string, number | null> & { timestamp: number; measured: number | null };

// A line is told apart by its dash as well as its color, never by color alone.
const RATING_SERIES: readonly TrendSeries[] = [
  { key: 'a', label: 'A', color: 'var(--success)' },
  { key: 'b', label: 'B', color: 'var(--success)', dash: '7 4' },
  { key: 'c', label: 'C', color: 'var(--warning)' },
  { key: 'd', label: 'D', color: 'var(--severity-high)', dash: '7 4' },
  { key: 'e', label: 'E', color: 'var(--danger)' },
  { key: 'not_rated', label: 'Not rated', color: 'var(--severity-info)', dash: '2 3' },
];
const FINDING_SERIES: readonly TrendSeries[] = [
  { key: 'blocker', label: 'Blocker', color: 'var(--danger)' },
  { key: 'high', label: 'High', color: 'var(--severity-high)', dash: '7 4' },
  { key: 'medium', label: 'Medium', color: 'var(--warning)' },
  { key: 'low', label: 'Low', color: 'var(--severity-low)', dash: '7 4' },
  { key: 'info', label: 'Info', color: 'var(--severity-info)', dash: '2 3' },
  { key: 'unknown', label: 'Unknown severity', color: 'var(--text-tertiary)', dash: '10 3 2 3' },
];
const SLA_SERIES: readonly TrendSeries[] = [
  { key: 'breached', label: 'Breached', color: 'var(--danger)' },
  { key: 'due_soon', label: 'Due soon', color: 'var(--warning)', dash: '7 4' },
];

const timestamp = (day: string) => Date.parse(`${day}T00:00:00Z`);

/**
 * One row per day. Ratings count projects per letter; findings and SLA sum the projects measured that
 * day, and a day without any measured project stays null so the line breaks instead of dropping to zero.
 */
export function portfolioChart(days: readonly PortfolioDay[], view: 'ratings' | 'findings' | 'sla', dimension: RatingDimension = 'security') {
  const series = view === 'ratings' ? RATING_SERIES : view === 'findings' ? FINDING_SERIES : SLA_SERIES;
  const rows = days.map((day): TrendRow => {
    const values = view === 'ratings' ? day.ratings[dimension] : view === 'findings' ? day.findings : day.sla;
    const measured = view === 'ratings' ? day.projects : ((view === 'findings' ? day.findings : day.sla)?.projects ?? null);
    const row: TrendRow = { timestamp: timestamp(day.day), measured };
    for (const item of series) row[item.key] = values ? ((values as Record<string, number | null>)[item.key] ?? null) : null;
    return row;
  });
  return { rows, series, measuredDays: rows.filter((row) => row.measured != null && row.measured > 0).length };
}

/** Change over the period in percentage points; unknown when either end was not measured. */
export function portfolioDelta(start: number | null, end: number | null): number | null {
  return start == null || end == null ? null : end - start;
}
