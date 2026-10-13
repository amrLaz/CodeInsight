import type { RawCondition } from '../api/codeinsights';
import { formatMetric, measurementLabel, type MeasurementState } from '../report/measure';
import type { ProducerResult } from './producers';
import type { LocalReport } from './analysis';

/**
 * Lot L7: projects the local run onto the last analysis' gate conditions. The result
 * is an estimate from the workstation producers, never a CI verdict: the labels below
 * deliberately avoid « Pass » and « Fail ».
 */

export type EstimateEffect = 'worsens' | 'improves' | 'no-worsening-detected' | 'unknown';

export type ConditionEstimateResult = 'worsens' | 'improves' | 'unchanged' | 'unknown' | 'not-estimable';

export type ConditionEstimate = {
  metric: string;
  label: string;
  display: string;
  result: ConditionEstimateResult;
  required: boolean;
};

export type GateEstimate = {
  effect: EstimateEffect;
  label: string;
  notice: string;
  conditions: ConditionEstimate[];
};

export type BranchIssue = {
  tool: string;
  rule: string;
  path: string;
  line: number;
  category: string;
  severity: string;
  message: string;
};

const COUNT_METRICS: Record<string, string[]> = {
  bugs: ['bug', 'reliability'],
  vulnerabilities: ['vulnerability', 'secret', 'security'],
  security_hotspots: ['security_hotspot'],
  code_smells: ['code_smell', 'code', 'maintainability', 'best-practice'],
};

const METRIC_LABELS: Record<string, string> = {
  bugs: 'Bugs',
  vulnerabilities: 'Vulnérabilités',
  security_hotspots: 'Points sensibles',
  code_smells: 'Code smells',
};

const PRODUCERS: Record<string, string[]> = {
  bugs: ['semgrep'],
  code_smells: ['semgrep'],
  vulnerabilities: ['semgrep', 'gitleaks'],
  security_hotspots: ['semgrep'],
};

export const ESTIMATE_NOTICE =
  "Estimation locale (semgrep, gitleaks) : ce n'est pas le verdict de la CI. Les autres producteurs du profil ne sont pas simulés. Aucun « pass » local n'est affiché.";

/** Maps a local or analysed finding onto a count metric, or nowhere when it cannot. */
export function metricFor(finding: { tool: string; category: string }): string | undefined {
  if (finding.tool === 'gitleaks') return 'vulnerabilities';
  const category = finding.category.toLowerCase();
  for (const [metric, categories] of Object.entries(COUNT_METRICS)) {
    if (categories.includes(category)) return metric;
  }
  return undefined;
}

export function sameRule(left: { tool: string; rule: string }, right: { tool: string; rule: string }): boolean {
  if (left.tool !== right.tool) return false;
  if (left.rule === right.rule) return true;
  if (right.rule === `${right.tool}/${left.rule}`) return true;
  if (left.rule === `${left.tool}/${right.rule}`) return true;
  return false;
}

/**
 * Analysed issues of the tools we actually ran, on files the developer changed, that
 * the local run no longer reports. That is an estimate of a fix, not a proof.
 */
export function correctedIssues(branch: BranchIssue[], local: LocalReport, changedPaths: Iterable<string>): BranchIssue[] {
  const paths = new Set(changedPaths);
  const measured = new Set(local.producers.filter((producer) => producer.state === 'measured').map((producer) => producer.tool));
  const present = [...local.introduced, ...local.elsewhere];
  return branch.filter((issue) => {
    if (!measured.has(issue.tool) || !paths.has(issue.path)) return false;
    return !present.some((finding) => sameRule(issue, finding) && finding.path === issue.path);
  });
}

export function estimateLabel(effect: EstimateEffect): string {
  switch (effect) {
    case 'worsens':
      return 'aggravation probable';
    case 'improves':
      return 'amélioration possible';
    case 'no-worsening-detected':
      return 'aucune aggravation détectée';
    default:
      return 'non conclusive';
  }
}

export function estimateNotice(producers: ProducerResult[]): string {
  const measured = producers.filter((producer) => producer.state === 'measured').map((producer) => producer.tool);
  const missing = producers.filter((producer) => producer.state !== 'measured');
  if (measured.length === 0)
    return `${ESTIMATE_NOTICE} Aucun producteur local n'a mesuré : l'estimation ne conclut rien.`;
  if (missing.length === 0) return `${ESTIMATE_NOTICE} Producteurs locaux : ${measured.join(', ')}.`;
  return `${ESTIMATE_NOTICE} Producteurs locaux : ${measured.join(', ')}. Manquants : ${missing
    .map((producer) => `${producer.tool} (${producer.state === 'absent' ? 'absent' : 'en échec'})`)
    .join(', ')}.`;
}

function holds(operator: string, value: number, threshold: number): boolean {
  switch (operator) {
    case '<=':
      return value <= threshold;
    case '<':
      return value < threshold;
    case '>=':
      return value >= threshold;
    case '>':
      return value > threshold;
    default:
      return false;
  }
}

function knownOperator(operator: string): boolean {
  return operator === '<=' || operator === '<' || operator === '>=' || operator === '>';
}

function countByMetric(findings: { tool: string; category: string }[]): Map<string, number> {
  const counts = new Map<string, number>();
  for (const finding of findings) {
    const metric = metricFor(finding);
    if (metric === undefined) continue;
    counts.set(metric, (counts.get(metric) ?? 0) + 1);
  }
  return counts;
}

function measuredTools(producers: ProducerResult[], tools: string[]): boolean {
  return tools.every((tool) => producers.some((producer) => producer.tool === tool && producer.state === 'measured'));
}

function numeric(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function conditionLabel(condition: RawCondition): string {
  const metric = condition.metric ?? 'unknown';
  return METRIC_LABELS[metric] ?? metric;
}

function compareResult(operator: string, value: number, threshold: number): 'under' | 'over' {
  return holds(operator, value, threshold) ? 'under' : 'over';
}

/**
 * Confronts the local introduced/corrected counts with the last analysis' conditions.
 * A missing workstation producer makes the related condition unknown rather than green.
 */
export function estimateGate(
  conditions: RawCondition[],
  report: LocalReport,
  corrected: BranchIssue[],
): GateEstimate {
  const introduced = countByMetric(report.introduced);
  const fixed = countByMetric(corrected);
  const rows: ConditionEstimate[] = [];
  let worsens = false;
  let improves = false;
  let unknownRequired = false;
  const nothingMeasured = report.producers.every((producer) => producer.state !== 'measured');

  for (const condition of conditions) {
    const metric = condition.metric ?? 'unknown';
    const required = condition.required === true;
    const operator = condition.operator ?? '';
    const threshold = numeric(condition.threshold);
    const value = numeric(condition.value);
    const state = condition.state as MeasurementState | undefined;
    const tools = PRODUCERS[metric];
    if (tools === undefined || !knownOperator(operator) || threshold === null) {
      rows.push({
        metric,
        label: conditionLabel(condition),
        display: 'non estimable localement',
        result: 'not-estimable',
        required,
      });
      continue;
    }
    const delta = (introduced.get(metric) ?? 0) - (fixed.get(metric) ?? 0);
    const toolsReady = measuredTools(report.producers, tools);
    if (nothingMeasured || (delta <= 0 && !toolsReady)) {
      const row: ConditionEstimate = {
        metric,
        label: conditionLabel(condition),
        display: toolsReady
          ? 'aucun producteur local disponible'
          : `producteur local manquant (${tools.filter((tool) => !report.producers.some((producer) => producer.tool === tool && producer.state === 'measured')).join(', ') || tools.join(', ')})`,
        result: 'unknown',
        required,
      };
      rows.push(row);
      if (required) unknownRequired = true;
      continue;
    }
    if (value === null || (state !== undefined && state !== 'measured')) {
      const extra = delta === 0 ? '' : ` · ${delta > 0 ? '+' : ''}${delta} locale(s)`;
      rows.push({
        metric,
        label: conditionLabel(condition),
        display: `${measurementLabel(state)}${extra}`,
        result: delta > 0 ? 'worsens' : 'unknown',
        required,
      });
      if (delta > 0 && required) worsens = true;
      else if (required) unknownRequired = true;
      continue;
    }
    const estimated = Math.max(0, value + delta);
    const before = compareResult(operator, value, threshold);
    const after = compareResult(operator, estimated, threshold);
    const display = `${formatMetric(value)} → ${formatMetric(estimated)} ${operator} ${formatMetric(threshold)}`;
    let result: ConditionEstimateResult = 'unchanged';
    if (before === 'under' && after === 'over') result = 'worsens';
    else if (before === 'over' && after === 'under') result = 'improves';
    else if (delta > 0 && after === 'over') result = 'worsens';
    rows.push({ metric, label: conditionLabel(condition), display, result, required });
    if (required && result === 'worsens') worsens = true;
    if (required && result === 'improves') improves = true;
    if (required && !toolsReady && result !== 'worsens') unknownRequired = true;
  }

  const effect: EstimateEffect = nothingMeasured
    ? 'unknown'
    : worsens
      ? 'worsens'
      : unknownRequired
        ? 'unknown'
        : improves
          ? 'improves'
          : 'no-worsening-detected';
  return { effect, label: estimateLabel(effect), notice: estimateNotice(report.producers), conditions: rows };
}


