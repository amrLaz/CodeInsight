import { Button, Grid } from '@/components/ui';
import type { AnalysisMetrics, AnalysisRecord, GateConditionSnapshot, Measurement, MqrRating, MqrScopeRatings, TechnicalState } from '@/types/api';
import { completenessLabel, formatMetric, measurementStateLabel, gateLabel } from '@/utils/metric-display';

export const METRIC_FIELDS = [
  ['security_rating', 'Security', ''],
  ['reliability_rating', 'Reliability', ''],
  ['maintainability_rating', 'Maintainability', ''],
  ['security_review_rating', 'Security review', ''],
  ['coverage', 'Coverage', '%'],
  ['duplication', 'Duplications', '%'],
  ['vulnerabilities', 'Vulnerabilities', ''],
  ['security_hotspots', 'Hotspots', ''],
  ['code_smells', 'Code Smells', ''],
  ['bugs', 'Bugs', ''],
  ['dependency_vulnerabilities', 'SCA Dep. Vulns', ''],
] as const;

export type MetricKey = (typeof METRIC_FIELDS)[number][0];

const RATING_DETAILS: Record<Extract<MetricKey, `${string}_rating`>, { key: keyof MqrScopeRatings }> = {
  security_rating: { key: 'security' },
  reliability_rating: { key: 'reliability' },
  maintainability_rating: { key: 'maintainability' },
  security_review_rating: { key: 'security_review' },
};

const RATING_EVIDENCE_METRICS: Partial<Record<MetricKey, keyof MqrScopeRatings>> = {
  vulnerabilities: 'security',
  bugs: 'reliability',
  code_smells: 'maintainability',
  security_hotspots: 'security_review',
};

export function MetricGrid({
  metrics,
  ratings,
  keys,
  onMetricClick,
  metricActionLabel,
  linkableMetrics,
  measuredOnly = false,
}: {
  metrics: AnalysisMetrics;
  ratings?: MqrScopeRatings;
  keys?: readonly MetricKey[];
  onMetricClick?: (metric: MetricKey) => void;
  metricActionLabel?: (metric: MetricKey, label: string) => string;
  linkableMetrics?: ReadonlySet<MetricKey>;
  measuredOnly?: boolean;
}) {
  const sourceFields = keys ? METRIC_FIELDS.filter(([key]) => keys.includes(key)) : METRIC_FIELDS.filter(([key]) => measuredOnly || key !== 'security_hotspots');
  const selected = sourceFields.filter(([key]) => !RATING_EVIDENCE_METRICS[key] || !ratings?.[RATING_EVIDENCE_METRICS[key]]);
  const deduplicatesEvidence = selected.length !== sourceFields.length;
  const fields = measuredOnly
    ? selected.filter(([key]) => (isRatingKey(key) ? !!(ratings?.[RATING_DETAILS[key].key].rating ?? metrics[key]) : typeof metrics[key] === 'number' && Number.isFinite(metrics[key])))
    : selected;
  if (fields.length === 0) return <p className="metric-grid-empty">No metrics measured in the latest analysis.</p>;
  const ratingsUnavailable = !ratings && fields.some(([key]) => isRatingKey(key));
  return (
    <>
      <dl className={`metric-grid${measuredOnly ? ' metric-grid-compact' : ''}${deduplicatesEvidence ? ' metric-grid-deduplicated' : ''}`}>
        {fields.map(([key, label, unit]) => {
          const detail = isRatingKey(key) ? ratings?.[RATING_DETAILS[key].key] : undefined;
          const rating = detail?.rating ?? (isRatingKey(key) ? metrics[key] : null);
          const available = isRatingKey(key) ? typeof rating === 'string' : typeof metrics[key] === 'number' && Number.isFinite(metrics[key]);
          return (
            <div className={`metric-card${available ? '' : ' is-unavailable'}${isRatingKey(key) ? ' is-rating' : ''}${typeof rating === 'string' ? ` rating-${rating}` : ''}`} key={key}>
              <dt>{label}</dt>
              <dd>
                {available ? (
                  isRatingKey(key) ? (
                    rating
                  ) : (
                    formatMetric(metrics[key], unit)
                  )
                ) : (
                  <>
                    <span aria-hidden="true">-</span>
                    <span className="sr-only">Not available</span>
                  </>
                )}
              </dd>
              {isRatingKey(key) && detail && <RatingEvidence metric={key} rating={detail} />}
              {key === 'security_review_rating' && !detail && typeof metrics.security_hotspots === 'number' && Number.isFinite(metrics.security_hotspots) && (
                <p className="metric-rating-counter">{formatMetric(metrics.security_hotspots)} hotspots</p>
              )}
              {onMetricClick && (!linkableMetrics || linkableMetrics.has(key)) && (
                <Button type="button" className="metric-card-link" onClick={() => onMetricClick(key)} aria-label={metricActionLabel?.(key, label) ?? `View issues related to ${label}`} />
              )}
            </div>
          );
        })}
      </dl>
      {ratingsUnavailable && !measuredOnly && <p className="metric-grid-note">MQR ratings were not calculated for this analysis.</p>}
    </>
  );
}

function RatingEvidence({ metric, rating }: { metric: Extract<MetricKey, `${string}_rating`>; rating: MqrRating }) {
  const data = rating.data;
  const count =
    metric === 'security_rating'
      ? `${data.finding_count} vulnerabilities`
      : metric === 'reliability_rating'
        ? `${data.finding_count} bugs`
        : metric === 'maintainability_rating'
          ? `${data.finding_count} code smells${data.debt_ratio_percent == null ? '' : ` · ${formatMetric(data.debt_ratio_percent, '%')} debt ratio`}`
          : `${data.reviewed_hotspots ?? 0} / ${data.total_hotspots ?? 0} hotspots · ${data.reviewed_percent == null ? '-' : formatMetric(data.reviewed_percent, '%')} reviewed`;
  return (
    <>
      <p className="metric-rating-counter">{count}</p>
      {!rating.rating && <p className="metric-rating-help">Reason: {ratingReason(rating.reason)}.</p>}
    </>
  );
}

function isRatingKey(key: MetricKey): key is Extract<MetricKey, `${string}_rating`> {
  return key.endsWith('_rating');
}

export function ratingReason(reason?: string | null) {
  return (
    (
      {
        analyzer_failed: 'the analyzer failed',
        analyzer_not_executed: 'the analyzer was not executed',
        no_applicable_rules: 'the profile contains no applicable rule',
        unclassified_rules: 'one or more rules are absent from the exact catalogue',
        severity_unclassified: 'one or more severities are not classified',
        partial_remediation_model: 'at least one code smell has no remediation cost',
        code_lines_not_measured: 'code lines were not measured',
        new_code_lines_not_measured: 'new code lines were not measured',
        baseline_absent: 'the baseline analysis is missing',
        configured_baseline_absent: 'the configured baseline is missing',
        git_history_unavailable: 'Git comparison data is missing',
        comparison_unavailable: 'Git comparison is unavailable',
        finding_identity_indeterminate: 'finding identity is ambiguous',
      } as Record<string, string>
    )[reason ?? ''] ??
    (reason?.split('_').join(' ') || 'the measurement is incomplete')
  );
}

export default function AnalysisEvidence({ analysis }: { analysis: AnalysisRecord }) {
  const measurements = Object.entries(analysis.measurements ?? {}).map(([dimension, measurement]) => ({ dimension, measurement }));
  const conditions = analysis.gate_snapshot?.conditions ?? [];
  const review = analysis.gate_snapshot?.security_review;
  const configuration = analysis.gate_snapshot?.configuration;
  const remediation = analysis.gate_snapshot?.remediation;
  const completeness = analysis.completeness ?? 'incomplete';
  const technicalState = analysis.technical_state ?? analysis.gate_snapshot?.technical_state ?? 'incomplete';
  const technicalStateLabel: Record<TechnicalState, string> = {
    success: 'Successful',
    failed: 'Failed',
    incomplete: 'Incomplete',
  };

  return (
    <section className="analysis-evidence" aria-label="Measurements and reproducible verdict">
      <div className="analysis-evidence-heading">
        <h4>Calculation evidence</h4>
        <span className={`status-pill completeness-${completeness}`}>Completeness: {completenessLabel(completeness)}</span>
        <span className={`status-pill technical-${technicalState}`}>Technical status: {technicalStateLabel[technicalState] ?? technicalState}</span>
      </div>
      <p className="scope-note">
        Profile <code>{analysis.profile_id ?? 'Not available'}</code> · version <code>{analysis.profile_version ?? 'Not available'}</code> · formula{' '}
        <code>{analysis.formula_version ?? 'Not available'}</code>
      </p>

      {measurements.length > 0 ? (
        <Grid<{ dimension: string; measurement: Measurement }>
          ariaLabel="Measurement status"
          caption="Measurements received for this analysis"
          className="table-wrap"
          rows={measurements}
          rowKey={(row) => row.dimension}
          columns={[
            { id: 'measurement', header: 'Measurement', rowHeader: true, value: (row) => row.dimension, render: (row) => <code>{row.dimension}</code> },
            {
              id: 'status',
              header: 'Status',
              value: (row) => measurementStateLabel(row.measurement.state),
              render: (row) => <span className={`status-pill measurement-${row.measurement.state}`}>{measurementStateLabel(row.measurement.state)}</span>,
            },
            {
              id: 'value',
              header: 'Value',
              value: (row) => (row.measurement.state === 'measured' ? formatMetric(analysis.gate_snapshot?.metrics?.[row.dimension] ?? row.measurement.value) : 'Not available'),
            },
            {
              id: 'tool',
              header: 'Tool',
              value: (row) => (row.measurement.tool ? `${row.measurement.tool}${row.measurement.tool_version ? ` ${row.measurement.tool_version}` : ''}` : 'Not available'),
              render: (row) =>
                row.measurement.tool ? (
                  <>
                    <code>{row.measurement.tool}</code>
                    {row.measurement.tool_version ? ` ${row.measurement.tool_version}` : ''}
                  </>
                ) : (
                  'Not available'
                ),
            },
          ]}
        />
      ) : (
        <p className="feedback">This historical analysis does not provide detailed measurement status.</p>
      )}

      {conditions.length > 0 ? (
        <Grid<GateConditionSnapshot & { rowKey: string }>
          ariaLabel="Quality Gate conditions"
          caption={`${analysis.gate_snapshot?.gate_name ?? 'Quality Gate'} — applied snapshot`}
          className="table-wrap"
          rows={conditions.map((condition, index) => ({ ...condition, rowKey: `${condition.metric}:${index}` }))}
          rowKey={(condition) => condition.rowKey}
          columns={[
            { id: 'measurement', header: 'Measurement', rowHeader: true, value: (condition) => condition.metric, render: (condition) => <code>{condition.metric}</code> },
            {
              id: 'condition',
              header: 'Condition',
              value: (condition) => `${condition.operator} ${condition.display_threshold ?? condition.threshold}`,
              render: (condition) => (
                <code>
                  {condition.operator} {condition.display_threshold ?? condition.threshold}
                </code>
              ),
            },
            { id: 'scope', header: 'Scope', value: (condition) => (condition.scope === 'new_code' ? 'New Code' : 'Overall code') },
            {
              id: 'value',
              header: 'Value',
              render: (condition) => (
                <>
                  {condition.state === 'measured'
                    ? (condition.display_value ?? formatMetric(condition.value))
                    : condition.state === 'not_applicable'
                      ? 'Not applicable'
                      : measurementStateLabel(condition.state)}
                  {condition.denominator != null && <div>{`${formatMetric(condition.numerator)} / ${formatMetric(condition.denominator)}`}</div>}
                  {condition.reason && <div>{condition.reason}</div>}
                </>
              ),
              tooltip: (condition) =>
                [condition.display_value ?? condition.value, condition.denominator != null ? `${formatMetric(condition.numerator)} / ${formatMetric(condition.denominator)}` : null, condition.reason]
                  .filter(Boolean)
                  .join(' · '),
            },
            {
              id: 'provenance',
              header: 'Provenance',
              render: (condition) => (
                <>
                  {condition.tool ? (
                    <>
                      <code>{condition.tool}</code>
                      {condition.tool_version ? ` ${condition.tool_version}` : ''}
                    </>
                  ) : (
                    (condition.provenance ?? 'Not available')
                  )}
                  {condition.scope === 'new_code' && (
                    <div>
                      Baseline: <code>{condition.baseline_id ?? 'Not available'}</code>
                      <br />
                      <code>{condition.base_sha ?? 'Not available'}</code> → <code>{condition.head_sha ?? 'Not available'}</code>
                    </div>
                  )}
                </>
              ),
              tooltip: (condition) =>
                `${condition.tool ? `${condition.tool}${condition.tool_version ? ` ${condition.tool_version}` : ''}` : (condition.provenance ?? 'Not available')}${condition.scope === 'new_code' ? ` · Baseline: ${condition.baseline_id ?? 'Not available'} · ${condition.base_sha ?? 'Not available'} → ${condition.head_sha ?? 'Not available'}` : ''}`,
            },
            {
              id: 'result',
              header: 'Result',
              value: (condition) => (condition.result === 'not_applicable' ? 'Not applicable' : gateLabel(condition.result)),
              render: (condition) => <span className={`status-pill ${condition.result}`}>{condition.result === 'not_applicable' ? 'Not applicable' : gateLabel(condition.result)}</span>,
            },
          ]}
        />
      ) : (
        <p className="feedback">No structured Quality Gate snapshot is available for this analysis.</p>
      )}

      {review && (
        <section aria-label="Applied security review policy">
          <h4>Security review — publication snapshot</h4>
          <p>
            {review.scope === 'new_code' ? 'New Code' : 'Overall code'} ·{' '}
            {review.state === 'not_applicable' ? 'Not applicable' : review.state === 'measured' ? formatMetric(review.value, '%') : 'Not measured'} ({review.numerator} / {review.denominator} hotspots
            reviewed) · threshold: {review.threshold == null ? 'Informational' : formatMetric(review.threshold, '%')}.{' '}
            {review.result === 'not_applicable' ? 'Not applicable' : gateLabel(review.result)}
          </p>
          {review.reason && <p className="scope-note">Reason: {review.reason}</p>}
          <p className="scope-note">
            Decisions frozen on {new Date(review.captured_at).toLocaleString()}. {review.applied_exception_decision_ids.length} approved exception(s) applied. A new review applies to later
            publications.
          </p>
          {!!review.decisions?.length && (
            <details>
              <summary>Decisions retained in this verdict</summary>
              <ul>
                {review.decisions.map((decision) => (
                  <li key={decision.decision_id}>
                    <a href={`?finding_id=${encodeURIComponent(decision.finding_id)}`}>Finding record</a> · decision <code>{decision.decision_id}</code> · version {decision.version} · {decision.state}{' '}
                    / {decision.hotspot_review} · {decision.branch}
                    <p>
                      {decision.reason ?? 'Review without exception'}
                      {decision.expires_at ? ` · expires ${new Date(decision.expires_at).toLocaleString()}` : ''}
                    </p>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </section>
      )}
      {configuration && (
        <section aria-label="Effective analyzer configuration">
          <h4>Analyzer configuration</h4>
          <p>
            {(
              { matched: 'Configuration matches', missing: 'Configuration evidence missing', drift: 'Configuration drift', not_configured: 'No executable configuration published' } as Record<
                string,
                string
              >
            )[configuration.state] ?? configuration.state}
          </p>
          <p className="scope-note">
            Expected fingerprint: <code>{configuration.expected_hash}</code>
            <br />
            Effective fingerprint: <code>{configuration.effective_hash}</code>
            {configuration.reason && (
              <>
                <br />
                {configuration.reason}
              </>
            )}
          </p>
        </section>
      )}
      {remediation && (
        <section aria-label="Documented remediation estimate">
          <h4>Estimated remediation effort</h4>
          <p>
            {remediation.estimated_seconds == null ? 'Not measured' : `${formatMetric(remediation.estimated_seconds / 60)} estimated minutes`} · {remediation.modeled_findings} finding(s) with a model,{' '}
            {remediation.unmodeled_findings} without one.
          </p>
          <p className="scope-note">
            {remediation.state === 'partial' ? 'Partial estimate. ' : ''}Scope: {remediation.scope === 'global' ? 'overall code' : remediation.scope}. Model <code>{remediation.model}</code>, profile{' '}
            {remediation.profile_id}/{remediation.profile_version}. This duration is the producer's estimate.
          </p>
          {!!remediation.documentation_sources.length && (
            <ul>
              {remediation.documentation_sources.map((source) => (
                <li key={source}>
                  <a href={source} target="_blank" rel="noopener noreferrer">
                    Producer remediation model
                  </a>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
      {!!analysis.tool_executions?.length && (
        <p className="scope-note">
          Tools:{' '}
          {analysis.tool_executions
            .map((execution) => `${execution.tool}${execution.version ? ` ${execution.version}` : ''} (${execution.status}${execution.error_code ? `, ${execution.error_code}` : ''})`)
            .join(' · ')}
        </p>
      )}
    </section>
  );
}
