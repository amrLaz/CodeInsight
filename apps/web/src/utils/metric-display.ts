export function formatMetric(value: number | null | undefined, unit = ''): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 'Not available';
  if (unit === '%' && value > 0 && value < 0.01) return '<0.01%';
  return `${unit === '%' ? value.toLocaleString('en-US', { maximumFractionDigits: 2 }) : value}${unit}`;
}

export function comparableVersions(previous: { profile_id?: string | null; profile_version?: string | null; formula_version?: string | null }, current: typeof previous): boolean {
  return (['profile_id', 'profile_version', 'formula_version'] as const).every((key) => !!current[key] && current[key] !== '0' && current[key] !== 'unknown' && current[key] === previous[key]);
}

export function gateLabel(value: string | null | undefined): string {
  return value === 'pass' ? 'Pass' : value === 'fail' ? 'Fail' : 'Unknown';
}

export function analysisGateLabel(value: string | null | undefined, completeness: string | null | undefined): string {
  const label = gateLabel(value);
  return completeness == null ? 'Unknown' : label;
}

export function analysisGateClass(value: string | null | undefined, completeness: string | null | undefined): string {
  return completeness == null ? 'unknown' : value === 'pass' || value === 'fail' ? value : 'unknown';
}

export function measurementStateLabel(value: string | null | undefined): string {
  if (value === 'measured') return 'Measured';
  if (value === 'absent') return 'Absent';
  if (value === 'failed') return 'Measurement failed';
  return 'Not measured';
}

export function completenessLabel(value: string | null | undefined): string {
  if (value === 'complete') return 'Complete';
  if (value === 'incomplete') return 'Incomplete';
  return 'Not available';
}

export type FindingCategory = 'secret' | 'dependency' | 'code' | 'ioc' | 'unknown' | 'bug' | 'code_smell' | 'vulnerability' | 'security_hotspot';

export function findingCategory(value: string | null | undefined): FindingCategory {
  return value === 'secret' || value === 'dependency' || value === 'code' || value === 'ioc' || value === 'bug' || value === 'code_smell' || value === 'vulnerability' || value === 'security_hotspot'
    ? value
    : 'unknown';
}

export function findingCategoryLabel(value: FindingCategory): string {
  return {
    bug: 'Bug',
    code_smell: 'Code smell',
    vulnerability: 'Vulnerability',
    security_hotspot: 'Security hotspot',
    secret: 'Secret',
    dependency: 'Dependency / CVE',
    code: 'Code',
    ioc: 'IOC',
    unknown: 'Unknown category',
  }[value];
}

export function scopedMetrics<T extends object>(metrics: T, scope: 'overall' | 'new-code'): Partial<T> {
  // ponytail: no baseline/diff contract yet; enable New Code only when real scoped metrics exist.
  return scope === 'overall' ? metrics : {};
}

export function metricPoint(value: number | null | undefined, index: number, count: number, maximum: number) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null;
  return { x: count === 1 ? 180 : 8 + (index / (count - 1)) * 344, y: 140 - (value / maximum) * 130 };
}

export function trendTimeX(value: number, minimum: number, maximum: number, fallback: number): number {
  if (![value, minimum, maximum].every(Number.isFinite) || maximum <= minimum) return fallback;
  return 8 + Math.min(1, Math.max(0, (value - minimum) / (maximum - minimum))) * 344;
}

export function thresholdRatio(value: number, threshold: number): number {
  if (!Number.isFinite(value) || value <= 0) return 0;
  if (!Number.isFinite(threshold) || threshold <= 0) return 100;
  return Math.min(100, (value / threshold) * 100);
}

const PERCENTAGE_GATE_METRICS = new Set(['coverage', 'branch_coverage', 'condition_coverage', 'duplicated_lines_density']);

export function gateConditionValue(condition: {
  metric: string;
  operator: string;
  threshold: number;
  value: number | null;
  state: string;
  display_value?: string | null;
  display_threshold?: string | null;
}): string {
  const unit = PERCENTAGE_GATE_METRICS.has(condition.metric) ? '%' : '';
  const threshold = condition.display_threshold ?? formatMetric(condition.threshold, unit);
  const requirement = condition.operator === '>=' ? `required minimum: ${threshold}` : `allowed maximum: ${threshold}`;
  if (condition.state !== 'measured' || condition.value == null) return `${measurementStateLabel(condition.state)} · ${requirement}`;
  return `${condition.display_value ?? formatMetric(condition.value, unit)} · ${requirement}`;
}

export function downsampleMetrics<T>(items: readonly T[], maxPoints: number, values: (item: T) => readonly (number | null | undefined)[]): { item: T; index: number }[] {
  if (!Number.isInteger(maxPoints) || maxPoints < 2) throw new RangeError('maxPoints must be an integer greater than one');
  if (items.length <= maxPoints) return items.map((item, index) => ({ item, index }));

  const matrix = items.map(values);
  const dimensions = Math.max(0, ...matrix.map((row) => row.length));
  const selected = new Set([0, items.length - 1]);
  const ranges: { min: number; max: number }[] = [];

  // ponytail: page-local O(maxPoints x analyses x metrics) sampling; move to server time buckets if history pages become unbounded.

  for (let dimension = 0; dimension < dimensions; dimension += 1) {
    const measured = matrix
      .map((row, index) => ({ index, value: row[dimension] }))
      .filter((point): point is { index: number; value: number } => typeof point.value === 'number' && Number.isFinite(point.value));
    if (!measured.length) {
      ranges.push({ min: 0, max: 0 });
      continue;
    }
    const min = measured.reduce((left, right) => (right.value < left.value ? right : left));
    const max = measured.reduce((left, right) => (right.value > left.value ? right : left));
    ranges.push({ min: min.value, max: max.value });
    if (selected.size < maxPoints) selected.add(min.index);
    if (selected.size < maxPoints) selected.add(max.index);
  }

  while (selected.size < maxPoints) {
    const ordered = [...selected].sort((left, right) => left - right);
    let best = { index: -1, score: -1, span: -1, center: -1 };
    for (let interval = 1; interval < ordered.length; interval += 1) {
      const left = ordered[interval - 1];
      const right = ordered[interval];
      const span = right - left;
      for (let index = left + 1; index < right; index += 1) {
        const ratio = (index - left) / span;
        let score = 0;
        for (let dimension = 0; dimension < dimensions; dimension += 1) {
          const value = matrix[index][dimension];
          const leftValue = matrix[left][dimension];
          const rightValue = matrix[right][dimension];
          const finite = [value, leftValue, rightValue].map((candidate) => typeof candidate === 'number' && Number.isFinite(candidate));
          if (finite.every(Boolean)) {
            const range = ranges[dimension].max - ranges[dimension].min;
            if (range > 0) score = Math.max(score, Math.abs(value! - (leftValue! + (rightValue! - leftValue!) * ratio)) / range);
          } else if (!finite.every((candidate) => candidate === finite[0])) {
            score = Math.max(score, 1);
          }
        }
        const center = Math.min(index - left, right - index);
        if (score > best.score || (score === best.score && (span > best.span || (span === best.span && center > best.center)))) {
          best = { index, score, span, center };
        }
      }
    }
    if (best.index < 0) break;
    selected.add(best.index);
  }

  return [...selected].sort((left, right) => left - right).map((index) => ({ item: items[index], index }));
}
