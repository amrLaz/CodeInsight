import { Button } from '@/components/ui';
import { useState } from 'react';
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import InfoHint from '@/components/common/InfoHint';
import { METRIC_FIELDS, type MetricKey } from './AnalysisEvidence';
import { comparableVersions, downsampleMetrics, formatMetric } from '@/utils/metric-display';
import type { AnalysisRecord } from '@/types/api';

type MetricGroup = {
  id: 'ratings' | 'percentages' | 'findings';
  label: string;
  unit: string;
  maximum?: number;
  metrics: readonly MetricKey[];
};

type ChartRow = Record<string, number | string | null | boolean> & {
  timestamp: number;
  analysisId: string;
};

type ChartSeries = {
  metric: MetricKey;
  dataKey: string;
  index: number;
};

const GROUPS: readonly MetricGroup[] = [
  {
    id: 'ratings',
    label: 'Ratings',
    unit: '',
    maximum: 5,
    metrics: ['security_rating', 'reliability_rating', 'maintainability_rating', 'security_review_rating'],
  },
  {
    id: 'percentages',
    label: 'Percentages',
    unit: '%',
    maximum: 100,
    metrics: ['coverage', 'duplication'],
  },
  {
    id: 'findings',
    label: 'Findings',
    unit: '',
    metrics: ['vulnerabilities', 'security_hotspots', 'code_smells', 'bugs', 'dependency_vulnerabilities'],
  },
];

const LINE_COLORS = ['var(--primary)', 'var(--warning)', 'var(--severity-low)', 'var(--danger)', 'var(--text-primary)'] as const;
const LINE_DASHES = [undefined, '7 4', '2 3', '10 3 2 3', '1 4'] as const;
const MAX_CHART_POINTS = 32;

export default function MetricsHistory({ history }: { history: AnalysisRecord[] }) {
  const [selectedGroup, setSelectedGroup] = useState<MetricGroup['id']>('ratings');
  const analyses = [...history].sort((left, right) => left.created_at.localeCompare(right.created_at));
  if (!analyses.length) return <p className="empty-state">No analysis is available for this branch.</p>;

  const group = GROUPS.find(({ id }) => id === selectedGroup) ?? GROUPS[0];
  const sampled = downsampleMetrics(analyses, MAX_CHART_POINTS, (analysis) => group.metrics.map((key) => metricChartValue(analysis, key, group.id)));
  const maximum = group.maximum ?? Math.max(1, ...group.metrics.flatMap((key) => analyses.map((analysis) => metricChartValue(analysis, key, group.id)).filter(isFiniteNumber)));
  const measuredAnalyses = analyses.filter((analysis) => group.metrics.some((key) => isFiniteNumber(metricChartValue(analysis, key, group.id)))).length;
  const statusMessage =
    measuredAnalyses === 0
      ? 'No metrics in this group are available for this branch.'
      : measuredAnalyses === 1
        ? 'Only one analysis contains metrics in this group. Another measured analysis is needed to show a trend.'
        : null;
  const chart = buildChart(sampled, analyses, group);

  return (
    <section className="metrics-history" aria-labelledby="metrics-history-title">
      <h3 id="metrics-history-title">
        Metric trends{' '}
        <InfoHint>
          Metrics with the same unit and scale are grouped. Long histories retain representative points, including their bounds and extrema. Missing data or a version change interrupts a line.
        </InfoHint>
      </h3>
      {sampled.length < analyses.length && (
        <p className="metrics-history-sampling">
          Showing {sampled.length} representative points from {analyses.length} analyses on this page.
        </p>
      )}
      <div className="metric-history-tabs" role="tablist" aria-label="Metric group">
        {GROUPS.map(({ id, label }) => (
          <Button
            key={id}
            type="button"
            role="tab"
            id={`metric-tab-${id}`}
            aria-controls={`metric-panel-${id}`}
            aria-selected={selectedGroup === id}
            className={selectedGroup === id ? 'active' : ''}
            onClick={() => setSelectedGroup(id)}
          >
            {label}
          </Button>
        ))}
      </div>
      <div className="metric-history-panel" role="tabpanel" id={`metric-panel-${group.id}`} aria-labelledby={`metric-tab-${group.id}`}>
        <ul className="metric-history-legend" aria-label={`${group.label} current values`}>
          {group.metrics.map((key, index) => {
            const [, label, unit] = metricField(key);
            const latest = latestMetric(analyses, key, group.id);
            return (
              <li key={key}>
                <span
                  className="metric-history-swatch"
                  style={{
                    borderColor: LINE_COLORS[index % LINE_COLORS.length],
                    borderTopStyle: index === 0 ? 'solid' : 'dashed',
                  }}
                  aria-hidden="true"
                />
                <span>{label}</span>
                <strong>{latest ? formatChartValue(latest.value, group.id, unit) : 'Not available'}</strong>
              </li>
            );
          })}
        </ul>
        {statusMessage ? (
          <p className="metric-history-status" role="status">
            {statusMessage}
          </p>
        ) : (
          <figure className="metric-history-chart" aria-label={`${group.label} trend across ${measuredAnalyses} measured analyses`}>
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={chart.rows} margin={{ top: 12, right: 18, bottom: 12, left: 4 }} accessibilityLayer>
                <CartesianGrid vertical={false} stroke="var(--border)" />
                <XAxis
                  dataKey="timestamp"
                  type="number"
                  scale="time"
                  domain={['dataMin', 'dataMax']}
                  tickFormatter={(value) => new Date(value).toLocaleDateString()}
                  minTickGap={36}
                  tick={{ fill: 'var(--text-secondary)', fontSize: 12 }}
                  tickLine={{ stroke: 'var(--text-tertiary)' }}
                  axisLine={{ stroke: 'var(--border)' }}
                />
                <YAxis
                  domain={group.id === 'ratings' ? [1, 5] : [0, maximum]}
                  ticks={group.id === 'ratings' ? [1, 2, 3, 4, 5] : group.id === 'percentages' ? [0, 25, 50, 75, 100] : undefined}
                  tickFormatter={(value) => (group.id === 'ratings' ? ratingLabel(value) : `${value}${group.unit}`)}
                  allowDecimals={false}
                  width={44}
                  tick={{ fill: 'var(--text-secondary)', fontSize: 12 }}
                  tickLine={false}
                  axisLine={false}
                />
                <Tooltip
                  labelFormatter={(value) => new Date(Number(value)).toLocaleString()}
                  formatter={(value, name) => [formatChartValue(Number(value), group.id, group.unit), String(name)]}
                  cursor={{ stroke: 'var(--text-tertiary)', strokeDasharray: '3 3' }}
                  contentStyle={{
                    background: 'var(--bg-lighter)',
                    border: '1px solid var(--border)',
                    borderRadius: '6px',
                    color: 'var(--text-primary)',
                  }}
                  labelStyle={{ color: 'var(--text-secondary)' }}
                  itemStyle={{ color: 'var(--text-primary)' }}
                />
                {chart.series.map(({ metric, dataKey, index }) => {
                  const [, label] = metricField(metric);
                  return (
                    <Line
                      key={dataKey}
                      type="linear"
                      dataKey={dataKey}
                      name={label}
                      stroke={LINE_COLORS[index % LINE_COLORS.length]}
                      strokeDasharray={LINE_DASHES[index % LINE_DASHES.length]}
                      strokeWidth={2}
                      dot={{ r: 3, strokeWidth: 1 }}
                      activeDot={{ r: 5 }}
                      connectNulls={false}
                      isAnimationActive={false}
                    />
                  );
                })}
              </LineChart>
            </ResponsiveContainer>
          </figure>
        )}
      </div>
    </section>
  );
}

function buildChart(sampled: { item: AnalysisRecord; index: number }[], analyses: readonly AnalysisRecord[], group: MetricGroup): { rows: ChartRow[]; series: ChartSeries[] } {
  const rows = sampled.map(
    ({ item: analysis }) =>
      ({
        timestamp: Date.parse(analysis.created_at),
        analysisId: analysis.analysis_id,
      }) as ChartRow,
  );
  const series: ChartSeries[] = [];
  group.metrics.forEach((key, index) => {
    let segment = 0;
    sampled.forEach(({ item: analysis, index: analysisIndex }, sampledIndex) => {
      if (sampledIndex > 0 && !continuousMetric(analyses, key, group.id, sampled[sampledIndex - 1].index, analysisIndex)) segment += 1;
      const value = metricChartValue(analysis, key, group.id);
      if (!isFiniteNumber(value)) return;
      const dataKey = `${key}:${segment}`;
      rows[sampledIndex][dataKey] = value;
      if (!series.some((item) => item.dataKey === dataKey)) series.push({ metric: key, dataKey, index });
    });
  });
  return { rows, series };
}

function metricField(key: MetricKey) {
  return METRIC_FIELDS.find(([field]) => field === key)!;
}

function latestMetric(analyses: readonly AnalysisRecord[], key: MetricKey, group: MetricGroup['id']) {
  for (let index = analyses.length - 1; index >= 0; index -= 1) {
    const value = metricChartValue(analyses[index], key, group);
    if (isFiniteNumber(value)) return { value };
  }
  return null;
}

function formatChartValue(value: number, group: MetricGroup['id'], unit: string) {
  return group === 'ratings' ? ratingLabel(value) : formatMetric(value, unit);
}

function ratingLabel(value: number) {
  return value === 5 ? 'A' : value === 4 ? 'B' : value === 3 ? 'C' : value === 2 ? 'D' : 'E';
}

function isFiniteNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function continuousMetric(analyses: readonly AnalysisRecord[], key: MetricKey, group: MetricGroup['id'], start: number, end: number): boolean {
  for (let index = start; index <= end; index += 1) {
    if (!isFiniteNumber(metricChartValue(analyses[index], key, group))) return false;
    if (index > start && !comparableVersions(analyses[index - 1], analyses[index])) return false;
  }
  return true;
}

function metricChartValue(analysis: AnalysisRecord, key: MetricKey, group: MetricGroup['id']): number | null | undefined {
  const value = analysis.metrics[key];
  if (group !== 'ratings') return typeof value === 'number' ? value : null;
  return value === 'A' ? 5 : value === 'B' ? 4 : value === 'C' ? 3 : value === 'D' ? 2 : value === 'E' ? 1 : null;
}
