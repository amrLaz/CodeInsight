import type { RawAnalysis, RawCondition } from '../api/codeinsights';
import { formatMetric, gateVerdict, measurementLabel, type GateVerdict, type MeasurementState } from './measure';

/**
 * Turns one analysis payload into what the IDE displays. The rule inherited from the
 * hub applies here: a measure that was not produced is shown as such, never as zero,
 * and a verdict is only a verdict when the analysis is complete.
 */

export type MetricRow = {
  key: string;
  label: string;
  display: string;
  state: MeasurementState | undefined;
  measured: boolean;
};

export type ConditionRow = {
  metric: string;
  label: string;
  display: string;
  result: 'pass' | 'fail' | 'unknown';
  required: boolean;
};

export type BranchReport = {
  analysisId: string;
  projectKey: string;
  branch: string;
  /** What the analysis describes: the branch, or `PR #N · source → cible` for a pull request build. */
  scope: string;
  pullRequest: { id: string; targetBranch: string | undefined } | undefined;
  commitSha: string;
  createdAt: string | undefined;
  profile: string | undefined;
  verdict: GateVerdict;
  completeness: string;
  technicalState: string;
  metrics: MetricRow[];
  conditions: ConditionRow[];
};

const RATINGS: [string, string][] = [
  ['security_rating', 'Sécurité'],
  ['reliability_rating', 'Fiabilité'],
  ['maintainability_rating', 'Maintenabilité'],
  ['security_review_rating', 'Revue de sécurité'],
];

const COUNTS: [string, string, string][] = [
  ['bugs', 'Bugs', ''],
  ['vulnerabilities', 'Vulnérabilités', ''],
  ['security_hotspots', 'Points sensibles', ''],
  ['code_smells', 'Code smells', ''],
  ['dependency_vulnerabilities', 'Vulnérabilités de dépendances', ''],
  ['coverage', 'Couverture', '%'],
  ['duplication', 'Duplication', '%'],
];

const CONDITION_LABELS: Record<string, string> = {
  maintainability_score: 'Score de maintenabilité',
  security_score: 'Score de sécurité',
  reliability_score: 'Score de fiabilité',
  code_smells: 'Code smells',
  vulnerabilities: 'Vulnérabilités',
  security_hotspots: 'Points sensibles',
  bugs: 'Bugs',
};

function state(analysis: RawAnalysis, key: string): MeasurementState | undefined {
  const raw = analysis.measurements?.[key]?.state;
  return raw === 'measured' || raw === 'absent' || raw === 'failed' || raw === 'not_measured' ? raw : undefined;
}

function numeric(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function conditionRow(condition: RawCondition): ConditionRow {
  const metric = condition.metric ?? 'unknown';
  const result = condition.result === 'pass' || condition.result === 'fail' ? condition.result : 'unknown';
  const value = numeric(condition.value);
  const threshold = numeric(condition.threshold);
  // An unmeasured condition shows why it is unknown instead of an invented comparison.
  const display =
    value === null
      ? measurementLabel(condition.state as MeasurementState | undefined)
      : `${formatMetric(value)} ${condition.operator ?? '?'} ${threshold === null ? '?' : formatMetric(threshold)}`;
  return {
    metric,
    label: CONDITION_LABELS[metric] ?? metric,
    display,
    result,
    required: condition.required === true,
  };
}

function shortBranch(branch: string): string {
  return branch.replace(/^refs\/heads\//, '');
}

/** A pull request build never stands in for its branch: it is named as the pull request it is. */
export function analysisScope(analysis: Pick<RawAnalysis, 'branch' | 'pr_id' | 'pr_target_branch'>): string {
  if (analysis.pr_id === undefined || analysis.pr_id === null || analysis.pr_id.length === 0) return analysis.branch;
  const target = analysis.pr_target_branch ? shortBranch(analysis.pr_target_branch) : 'cible non enregistrée';
  return `PR #${analysis.pr_id} · ${shortBranch(analysis.branch)} → ${target}`;
}

export function branchReport(analysis: RawAnalysis): BranchReport {
  const metrics: MetricRow[] = [];
  for (const [key, label] of RATINGS) {
    const grade = analysis.metrics?.[key];
    const measured = typeof grade === 'string' && grade.length > 0;
    metrics.push({
      key,
      label,
      display: measured ? String(grade) : measurementLabel(state(analysis, key)),
      state: state(analysis, key),
      measured,
    });
  }
  for (const [key, label, unit] of COUNTS) {
    const measure = state(analysis, key);
    const value = numeric(analysis.metrics?.[key]);
    // `measured` with a value is the only case that may print a number, zero included.
    const measured = value !== null && measure !== 'absent' && measure !== 'failed' && measure !== 'not_measured';
    metrics.push({
      key,
      label,
      display: measured ? formatMetric(value, unit) : measurementLabel(measure),
      state: measure,
      measured,
    });
  }
  return {
    analysisId: analysis.analysis_id,
    projectKey: analysis.project_key,
    branch: analysis.branch,
    scope: analysisScope(analysis),
    pullRequest:
      analysis.pr_id === undefined || analysis.pr_id === null || analysis.pr_id.length === 0
        ? undefined
        : { id: analysis.pr_id, targetBranch: analysis.pr_target_branch ?? undefined },
    commitSha: analysis.commit_sha,
    createdAt: analysis.created_at,
    profile:
      analysis.profile_id === undefined || analysis.profile_id.length === 0
        ? undefined
        : `${analysis.profile_id}/${analysis.profile_version ?? '?'}`,
    // A technically failed or incomplete run has no trustworthy verdict, whatever the
    // stored gate says. The hub vocabulary is success | failed | incomplete.
    verdict:
      analysis.technical_state !== undefined && analysis.technical_state !== 'success'
        ? 'unknown'
        : gateVerdict(analysis.quality_gate, analysis.completeness),
    completeness: analysis.completeness ?? 'unknown',
    technicalState: analysis.technical_state ?? 'unknown',
    metrics,
    conditions: (analysis.gate_snapshot?.conditions ?? []).map(conditionRow),
  };
}

/** Short reason shown when the verdict is not a verdict. */
export function verdictReason(report: BranchReport): string | undefined {
  if (report.verdict !== 'unknown') return undefined;
  if (report.technicalState === 'failed' || report.technicalState === 'incomplete')
    return `État technique : ${report.technicalState}`;
  if (report.completeness !== 'complete')
    return `Analyse ${report.completeness === 'unknown' ? 'de complétude inconnue' : report.completeness}`;
  return 'Verdict indisponible';
}
