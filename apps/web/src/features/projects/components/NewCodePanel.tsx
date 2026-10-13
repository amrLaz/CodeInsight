import { API_BASE } from '@/config/env';
import InfoHint from '@/components/common/InfoHint';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { apiJson } from '@/services/api';
import { displayBranchName } from '@/utils/branch-selection';
import { repositoryCommitLink } from '@/utils/evidence-links';
import { gateConditionValue, thresholdRatio } from '@/utils/metric-display';
import type { AnalysisMetrics, GateConditionSnapshot, MqrRatingsSnapshot } from '@/types/api';
import { MetricGrid, ratingReason, type MetricKey } from './AnalysisEvidence';

type Comparison = {
  state: string;
  reason?: string;
  new_count?: number;
  persistent_count?: number;
  resolved_count?: number;
  indeterminate_count?: number;
  coverage?: number | null;
  baseline_id?: string;
  baseline_analysis_id?: string;
  baseline_branch?: string;
  baseline_version?: string;
  baseline_date?: string;
  base_sha?: string;
  head_sha?: string;
  covered_lines?: number;
  executable_lines?: number;
  coverage_state?: 'measured' | 'not_applicable';
  effective_definition?: {
    mode: 'previous_version' | 'number_of_days' | 'reference_branch' | 'specific_analysis';
    number_of_days?: number | null;
    reference_branch?: string | null;
    analysis_id?: string | null;
  } | null;
  effective_source?: 'pull_request_target' | 'manifest_override' | 'legacy_manifest_target' | 'branch' | 'project' | 'global' | 'default';
  ratings?: MqrRatingsSnapshot;
  conditions?: GateConditionSnapshot[];
};
const NEW_CODE_METRICS: readonly MetricKey[] = ['security_rating', 'reliability_rating', 'maintainability_rating', 'security_review_rating', 'coverage', 'duplication'];
const comparisonReasons: Record<string, { title: string; detail: string }> = {
  git_history_unavailable: {
    title: 'Git comparison was not provided',
    detail: 'This analysis does not contain a diff from its target commit. Run a new analysis that publishes the target SHA and Git diff; the target commit must already have a completed analysis.',
  },
  baseline_absent: {
    title: 'Baseline analysis is missing',
    detail: 'The target commit has no completed CodeInsights analysis to use as a baseline.',
  },
  project_version_missing: {
    title: 'Project version is missing',
    detail: 'Previous version requires the publication manifest to provide a project version.',
  },
  previous_version_baseline_absent: {
    title: 'Previous version baseline is missing',
    detail: 'No completed analysis from a different project version is available on this branch.',
  },
  number_of_days_baseline_absent: {
    title: 'Date baseline is missing',
    detail: 'No completed analysis is old enough for the configured period. New Code remains unavailable until one exists.',
  },
  reference_branch_baseline_absent: {
    title: 'Reference branch baseline is missing',
    detail: 'The reference branch has no completed analysis snapshot yet. Its latest snapshot may lag behind the live Git branch.',
  },
  specific_analysis_baseline_absent: {
    title: 'Specific analysis baseline is missing',
    detail: 'The configured analysis is not a completed snapshot of this project and branch.',
  },
  coverage_producer_incomplete: {
    title: 'Coverage producer is unavailable',
    detail: 'The required coverage producer is absent or failed. Missing data is not converted to zero.',
  },
  configured_baseline_absent: {
    title: 'Configured baseline is missing',
    detail: 'The selected code scope points to a baseline that has not been analyzed yet.',
  },
  details_purged: {
    title: 'Comparison details have expired',
    detail: 'Retention removed the detailed data required for this historical comparison.',
  },
};
const API = API_BASE;

export function NewCode({
  analysisId,
  token,
  repoProvider = 'other',
  repoUrl,
  headBranch,
  pullRequestId,
}: {
  analysisId: string;
  token?: string;
  repoProvider?: 'github' | 'azure-devops' | 'other';
  repoUrl?: string;
  headBranch?: string;
  pullRequestId?: string | null;
}) {
  const query = useQuery({
    queryKey: ['comparison', analysisId],
    enabled: !!analysisId,
    queryFn: ({ signal }) => apiJson<Comparison>(`${API}/v1/insights/analyses/${encodeURIComponent(analysisId)}/comparison`, token, { signal }),
  });
  const baseUrl = repositoryCommitLink(repoProvider, repoUrl, query.data?.base_sha);
  const headUrl = repositoryCommitLink(repoProvider, repoUrl, query.data?.head_sha);
  const unavailable = comparisonReasons[query.data?.reason ?? ''] ?? {
    title: 'Comparison is not available',
    detail: `Reason: ${query.data?.reason ?? 'comparison not measured'}.`,
  };
  const newCodeRatings = query.data?.ratings?.new_code;
  const newCodeConditions = (query.data?.conditions ?? []).filter((condition) => condition.scope === 'new_code');
  const coverage = newCodeConditions.find((condition) => condition.metric === 'coverage');
  const duplication = newCodeConditions.find((condition) => condition.metric === 'duplicated_lines_density');
  const ratingMetrics: AnalysisMetrics = {
    security_rating: newCodeRatings?.security.rating,
    reliability_rating: newCodeRatings?.reliability.rating,
    maintainability_rating: newCodeRatings?.maintainability.rating,
    security_review_rating: newCodeRatings?.security_review.rating,
    coverage: coverage?.value,
    duplication: duplication?.value,
  };
  return (
    <section className="panel comparison-panel">
      <h2>
        New Code{' '}
        <InfoHint>
          Git comparison between the selected analysis and its frozen baseline snapshot.
          {query.data?.baseline_analysis_id && (
            <>
              {' '}
              Baseline analysis: <code>{query.data.baseline_analysis_id}</code>.
            </>
          )}
        </InfoHint>
      </h2>
      {!analysisId ? (
        <p role="status">Select an analysis.</p>
      ) : query.isLoading ? (
        <p role="status">Loading comparison…</p>
      ) : query.error ? (
        <p role="alert">{query.error.message}</p>
      ) : query.data?.state !== 'available' ? (
        <div className="empty-state comparison-empty" role="status">
          <h3>{unavailable.title}</h3>
          <p>{unavailable.detail}</p>
          <ComparisonDefinition comparison={query.data} pullRequestId={pullRequestId} />
          {newCodeRatings && <MetricGrid metrics={ratingMetrics} ratings={newCodeRatings} keys={NEW_CODE_METRICS.slice(0, 4)} />}
        </div>
      ) : (
        <>
          <ComparisonSummary comparison={query.data} headBranch={headBranch} pullRequestId={pullRequestId} />
          <p className="comparison-range">
            <CommitReference label={baselineLabel(query.data)} sha={query.data.base_sha} url={baseUrl} />
            <span aria-hidden="true">→</span>
            <span className="sr-only">to</span>
            <CommitReference label={headBranch ? displayBranchName(headBranch) : 'analyzed commit'} sha={query.data.head_sha} url={headUrl} />
          </p>
          <ComparisonDefinition comparison={query.data} pullRequestId={pullRequestId} />
          <MetricGrid metrics={ratingMetrics} ratings={newCodeRatings} keys={NEW_CODE_METRICS} />
          <NewCodeGateEvidence conditions={newCodeConditions} />
          {[coverage, duplication]
            .filter((condition) => condition?.state !== 'measured')
            .map(
              (condition) =>
                condition && (
                  <p className="scope-note" key={condition.metric}>
                    {condition.metric === 'coverage' ? 'Coverage' : 'Duplicated lines density'}:{' '}
                    {condition.state === 'not_applicable' ? 'Not applicable' : `Not available: ${ratingReason(condition.reason)}`}.
                  </p>
                ),
            )}
          <dl className="metric-grid">
            {(
              [
                ['New', query.data.new_count],
                ['Persistent', query.data.persistent_count],
                ['Resolved', query.data.resolved_count],
                ['Indeterminate', query.data.indeterminate_count],
              ] as const
            ).map(([label, value]) => (
              <div className="metric-card" key={label}>
                <dt>{label}</dt>
                <dd>{value ?? 'Not available'}</dd>
              </div>
            ))}
          </dl>
          <p>
            {query.data.coverage_state === 'not_applicable'
              ? 'No executable New Code lines were found, so coverage is not applicable.'
              : `${coverage?.numerator ?? query.data.covered_lines ?? 'Not available'} / ${coverage?.denominator ?? query.data.executable_lines ?? 'Not available'} covered executable lines in the received New Code data.`}
          </p>
        </>
      )}
    </section>
  );
}

const DEFINITION_LABELS = {
  previous_version: 'Previous version',
  number_of_days: 'Number of days',
  reference_branch: 'Reference branch',
  specific_analysis: 'Specific analysis',
} as const;

const SOURCE_LABELS = {
  pull_request_target: 'Forced by pull request',
  manifest_override: 'Forced by the CI manifest',
  legacy_manifest_target: 'Target commit sent by CI',
  branch: 'Branch setting',
  project: 'Project setting',
  global: 'Global setting',
  default: 'Default setting',
} as const;

function sourceLabel(comparison: Comparison, pullRequestId?: string | null) {
  if (!comparison.effective_source) return 'Not available';
  const label = SOURCE_LABELS[comparison.effective_source];
  return comparison.effective_source === 'pull_request_target' && pullRequestId ? `${label} #${pullRequestId}` : label;
}

function formatDate(value: string | undefined) {
  return value ? new Date(value).toLocaleString() : 'Not available';
}

function baselineLabel(comparison: Comparison) {
  return comparison.baseline_branch ? displayBranchName(comparison.baseline_branch) : 'baseline';
}

function CommitReference({ label, sha, url }: { label: string; sha?: string; url: string | null }) {
  const short = sha ? <code title={sha}>{sha.slice(0, 7)}</code> : <span>Not available</span>;
  return (
    <span className="comparison-commit">
      <code>{label}</code>{' '}
      {url ? (
        <a className="repository-reference" href={url} target="_blank" rel="noreferrer">
          {short} ↗
        </a>
      ) : (
        short
      )}
    </span>
  );
}

function ComparisonSummary({ comparison, headBranch, pullRequestId }: { comparison: Comparison; headBranch?: string; pullRequestId?: string | null }) {
  const definition = comparison.effective_definition;
  if (!definition) return null;
  const head = headBranch ? <code>{displayBranchName(headBranch)}</code> : 'this analysis';
  const baseline = <code>{baselineLabel(comparison)}</code>;
  const date = formatDate(comparison.baseline_date);
  let summary: ReactNode;
  if (comparison.effective_source === 'pull_request_target')
    summary = (
      <>
        Changes in {head} not yet in {baseline}, the scope of pull request{pullRequestId ? ` #${pullRequestId}` : ''}.
      </>
    );
  else if (definition.mode === 'reference_branch')
    summary = (
      <>
        Changes in {head} compared with {baseline}.
      </>
    );
  else if (definition.mode === 'previous_version')
    summary = (
      <>
        Changes in {head} since the latest analysis of the previous project version <code>{comparison.baseline_version ?? 'Not available'}</code>.
      </>
    );
  else if (definition.mode === 'number_of_days')
    summary = (
      <>
        Changes in {head} since the latest analysis at least {definition.number_of_days ?? 'Not set'} days old.
      </>
    );
  else
    summary = (
      <>
        Changes in {head} since the configured baseline analysis.
      </>
    );
  return (
    <div className="comparison-summary">
      <p>{summary}</p>
      {definition.mode === 'reference_branch' && (
        <p className="scope-note">
          Baseline: latest completed {baseline} analysis ({date}). It may lag behind the live branch.
        </p>
      )}
    </div>
  );
}

function ComparisonDefinition({ comparison, pullRequestId }: { comparison: Comparison | undefined; pullRequestId?: string | null }) {
  if (!comparison?.effective_definition) return null;
  const definition = comparison.effective_definition;
  const referenceBranch = definition.reference_branch ? displayBranchName(definition.reference_branch) : 'Not set';
  const baselineBranch = comparison.baseline_branch ? displayBranchName(comparison.baseline_branch) : 'Not available';
  const rows: [string, ReactNode][] = [
    ['Definition', DEFINITION_LABELS[definition.mode]],
    ['Why this baseline', sourceLabel(comparison, pullRequestId)],
  ];
  if (definition.mode === 'reference_branch') {
    rows.push(['Reference branch', <code key="reference">{referenceBranch}</code>]);
    if (referenceBranch !== baselineBranch) rows.push(['Baseline branch', <code key="baseline">{baselineBranch}</code>]);
  } else if (definition.mode === 'previous_version') {
    rows.push(['Baseline version', <code key="version">{comparison.baseline_version ?? 'Not available'}</code>]);
  } else if (definition.mode === 'number_of_days') {
    rows.push(['Period', `${definition.number_of_days ?? 'Not set'} days`]);
  } else {
    rows.push(['Baseline analysis', <code key="analysis">{definition.analysis_id ?? 'Not set'}</code>]);
  }
  rows.push(['Baseline date', formatDate(comparison.baseline_date)]);
  return (
    <dl className="comparison-definition">
      {rows.map(([label, value]) => (
        <div key={label}>
          <dt>{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function NewCodeGateEvidence({ conditions }: { conditions: GateConditionSnapshot[] }) {
  const relevant = conditions.filter((condition) => condition.result === 'fail' || condition.result === 'unknown');
  if (!relevant.length) return null;
  const onlyUnknown = relevant.every((condition) => condition.result === 'unknown');
  return (
    <section className={`gate-failures${onlyUnknown ? ' gate-failures-unknown' : ''}`} aria-labelledby="new-code-gate-evidence-title">
      <strong id="new-code-gate-evidence-title">{onlyUnknown ? 'New Code Quality Gate cannot be evaluated' : 'New Code Quality Gate failures'}</strong>
      <ul>
        {relevant.map((condition) => {
          const measured = condition.state === 'measured' && condition.value != null;
          const label = condition.metric
            .split('_')
            .join(' ')
            .replace(/^./, (letter) => letter.toUpperCase());
          return (
            <li className="gate-failure-row" key={`${condition.metric}:${condition.operator}:${condition.threshold}`}>
              <span className="gate-failure-name">
                <span aria-hidden="true">{condition.metric === 'coverage' ? '%' : '!'}</span>
                {label}
              </span>
              <span className="gate-failure-value">{gateConditionValue(condition)}</span>
              {measured && (
                <span className="gate-failure-bar" aria-hidden="true">
                  <span style={{ width: `${thresholdRatio(condition.value!, condition.threshold)}%` }} />
                </span>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

