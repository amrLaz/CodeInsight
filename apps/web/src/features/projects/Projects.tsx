import { Button, Grid, type GridColumn } from '@/components/ui';
import { lazy, Suspense, useMemo, type ReactNode } from 'react';
import InfoHint from '@/components/common/InfoHint';
import type { AnalysisRecord, BranchSummary, GateConditionSnapshot, IssueSeverityFilter, IssueTypeFilter, ProjectMeta, ProjectSummary, ScopeMode } from '@/types/api';
import { analysisGateClass, analysisGateLabel, gateConditionValue, thresholdRatio } from '@/utils/metric-display';
import { repositoryBranchLink, repositoryCommitLink, repositoryPullRequestLink } from '@/utils/evidence-links';
import { MetricGrid, type MetricKey } from './components/AnalysisEvidence';
import ProjectPicker from '@/components/common/ProjectPicker';
import ScopeToggle from '@/components/common/ScopeToggle';
import { analysisSelectionKey, branchPickerOptions, displayBranchName, pullRequestIdOf, pullRequestRoute } from '@/utils/branch-selection';

const MetricsHistory = lazy(() => import('./components/MetricsHistory'));
const PortfolioTrends = lazy(() => import('./components/PortfolioTrends'));

type ProjectsProps = {
  summaries: ProjectSummary[];
  selectedSummary: ProjectSummary | null;
  selectedProject: string;
  selectedBranch: string;
  selectedAnalysisId: string;
  favoritePending: boolean;
  favoriteError?: string | null;
  branchLoading: boolean;
  branchError?: string | null;
  onBranchSearch?: (search: string) => void;
  branchSearchStatus?: string;
  setSelectedProject: (project: string) => void;
  setSelectedBranch: (branch: string) => void;
  onToggleFavorite: (summary: ProjectSummary) => void;
  loading: boolean;
  error?: string | null;
  projectMeta: Record<string, ProjectMeta>;
  onRefresh: () => void;
  scopeMode: ScopeMode;
  onScopeChange: (scope: ScopeMode) => void;
  newCodePanel: ReactNode;
  onOpenNewCode: () => void;
  onOpenIssues: (summary: ProjectSummary, analysis: AnalysisRecord, severity?: IssueSeverityFilter, category?: IssueTypeFilter) => void;
  onOpenCodeQuality: (summary: ProjectSummary, analysis: AnalysisRecord, metric: 'coverage' | 'duplication') => void;
  authToken?: string;
};

const metricIssueCategory: Partial<Record<MetricKey, IssueTypeFilter>> = {
  security_rating: 'vulnerability',
  maintainability_rating: 'code_smell',
  reliability_rating: 'bug',
  security_review_rating: 'security_hotspot',
  vulnerabilities: 'vulnerability',
  security_hotspots: 'security_hotspot',
  code_smells: 'code_smell',
  bugs: 'bug',
  dependency_vulnerabilities: 'dependency',
};
const qualityDetailMetrics = new Set<MetricKey>(['coverage', 'duplication']);
const linkableMetrics = new Set<MetricKey>([...(Object.keys(metricIssueCategory) as MetricKey[]), ...qualityDetailMetrics]);

export default function Projects({
  summaries,
  selectedSummary,
  selectedProject,
  selectedBranch,
  selectedAnalysisId,
  favoritePending,
  favoriteError,
  branchLoading,
  branchError,
  onBranchSearch,
  branchSearchStatus,
  setSelectedProject,
  setSelectedBranch,
  onToggleFavorite,
  loading,
  error,
  projectMeta,
  onRefresh,
  scopeMode,
  onScopeChange,
  newCodePanel,
  onOpenNewCode,
  onOpenIssues,
  onOpenCodeQuality,
  authToken,
}: ProjectsProps) {
  const projectOptions = useMemo(() => summaries.map((summary) => ({ key: summary.projectKey, isFavorite: summary.isFavorite })), [summaries]);
  const branchOptions = useMemo(
    () =>
      branchPickerOptions(
        selectedSummary?.branches ??
          (selectedSummary?.history ?? []).filter((analysis) => !analysis.pr_id).map((analysis) => ({ name: analysis.branch, count: 0, latest: analysis })),
      ),
    [selectedSummary],
  );
  const selectedBranchValue = branchOptions.find((branch) => displayBranchName(branch.key) === displayBranchName(selectedBranch))?.key ?? '';
  const repository = selectedSummary ? projectMeta[selectedSummary.projectKey] : undefined;
  const selectedPullRequestId = pullRequestIdOf(selectedBranchValue);
  const selectedReferenceUrl = selectedPullRequestId
    ? repositoryPullRequestLink(repository?.repoProvider ?? 'other', repository?.repoUrl, selectedPullRequestId)
    : repositoryBranchLink(repository?.repoProvider ?? 'other', repository?.repoUrl, selectedBranchValue);
  return (
    <section className="content-grid">
      <aside className="left-filters">
        <h3>
          Overview{' '}
          <InfoHint>
            Latest branch build for each project on this page and the selected project, across all branches. Pull request builds only describe their pull request; a project analysed only
            through pull requests shows its latest one.
          </InfoHint>
        </h3>
        <p>Projects: {summaries.length}</p>
        <p>Passed: {summaries.filter((item) => item.latest.completeness != null && item.latest.quality_gate === 'pass').length}</p>
        <p>Failed: {summaries.filter((item) => item.latest.completeness != null && item.latest.quality_gate === 'fail').length}</p>
        <p>Unknown: {summaries.filter((item) => item.latest.completeness == null || item.latest.quality_gate === 'unknown').length}</p>
      </aside>
      <div className="project-list">
        {favoriteError && (
          <p className="feedback error" role="alert">
            Favorite could not be updated: {favoriteError}
          </p>
        )}
        {/* Always visible dropdowns */}
        <div className="project-context-selectors">
          <div>
            <ProjectPicker
              label="PROJECT"
              options={projectOptions}
              value={selectedProject}
              onChange={setSelectedProject}
              onToggleFavorite={(projectKey) => {
                const summary = summaries.find((item) => item.projectKey === projectKey);
                if (summary) onToggleFavorite(summary);
              }}
              favoritePending={favoritePending}
            />
          </div>

          <div className="reference-picker">
            <ProjectPicker
              label="BRANCH"
              ariaLabel="Branches"
              className="branch-picker"
              options={branchOptions}
              value={selectedBranchValue}
              onChange={setSelectedBranch}
              disabled={!selectedProject}
              loading={branchLoading}
              loadingText="Loading branches…"
              error={branchError}
              placeholder={selectedProject ? 'No branches available' : 'Select a project first'}
              searchLabel="Search branches"
              searchPlaceholder="Branch name"
              emptyResultsMessage={branchOptions.length === 0 ? 'No branches are available for this project.' : 'No branch matches this search.'}
              onSearchChange={onBranchSearch}
              searchStatus={branchSearchStatus}
              hideOptionKey
            />
            {selectedReferenceUrl && (
              <a
                href={selectedReferenceUrl}
                target="_blank"
                rel="noreferrer"
                aria-label={selectedPullRequestId ? `Open pull request #${selectedPullRequestId} in the repository` : `Open branch ${displayBranchName(selectedBranchValue)} in the repository`}
                title={selectedPullRequestId ? 'Open pull request in the repository' : 'Open branch in the repository'}
              >
                ↗
              </a>
            )}
          </div>
          <Button type="button" className="context-refresh" onClick={onRefresh} disabled={loading}>
            {loading ? 'Refreshing...' : 'Refresh'}
          </Button>
        </div>

        {selectedProject && selectedSummary ? (
          <>
            {(() => {
              const branchMap = new Map<string, AnalysisRecord[]>();
              for (const analysis of selectedSummary.history) {
                const branch = analysisSelectionKey(analysis) || 'master';
                const existing = branchMap.get(branch) ?? [];
                existing.push(analysis);
                branchMap.set(branch, existing);
              }
              const branches =
                selectedSummary.branches ??
                Array.from(branchMap.entries())
                  .map(([branchName, analyses]) => {
                    const sorted = [...analyses].sort((a, b) => b.created_at.localeCompare(a.created_at));
                    return {
                      name: branchName,
                      latest: sorted[0],
                      count: analyses.length,
                    };
                  })
                  .sort((a, b) => b.latest.created_at.localeCompare(a.latest.created_at)) as BranchSummary[];

              const activeBranch = selectedBranch || (branches.length > 0 ? branches[0].name : null);
              const detailedBranch = branches.find((b) => b.name === activeBranch);
              const displayedAnalysis =
                selectedSummary.history.find((analysis) => analysisSelectionKey(analysis) === activeBranch && analysis.analysis_id === selectedAnalysisId) ?? detailedBranch?.latest ?? undefined;
              const pullRequest = detailedBranch?.pull_request;
              const repository = projectMeta[selectedSummary.projectKey];
              const commitUrl = repositoryCommitLink(repository?.repoProvider ?? 'other', repository?.repoUrl, displayedAnalysis?.commit_sha);
              const pullRequestUrl = repositoryPullRequestLink(repository?.repoProvider ?? 'other', repository?.repoUrl, displayedAnalysis?.pr_id);

              return branches.length === 0 ? (
                <p style={{ opacity: 0.7 }}>No branches found for this project</p>
              ) : (
                <>
                  {detailedBranch && !displayedAnalysis && (
                    <BranchWithoutBuild
                      branch={detailedBranch.name}
                      pullRequests={branches.filter((item) => item.pull_request?.source_branch === detailedBranch.name)}
                      repository={repository}
                      onSelect={setSelectedBranch}
                    />
                  )}
                  {detailedBranch && displayedAnalysis && (
                    <div className="analysis-detail">
                      <h3 style={{ margin: '0 0 16px 0' }}>
                        {pullRequest ? `Metrics for pull request #${pullRequest.id}` : `Metrics for ${displayBranchName(detailedBranch.name)}`}
                        {pullRequest && <span className="analysis-detail-scope">{pullRequestRoute(pullRequest)}</span>}
                      </h3>
                      <div className="analysis-context">
                        <p>
                          <strong>Quality Gate:</strong>{' '}
                          <span className={`status-pill ${analysisGateClass(displayedAnalysis.quality_gate, displayedAnalysis.completeness)}`}>
                            {analysisGateLabel(displayedAnalysis.quality_gate, displayedAnalysis.completeness)}
                          </span>
                        </p>
                        <p>
                          <strong>Analysis:</strong> {displayedAnalysis.status}
                        </p>
                        <p>
                          <strong>Commit</strong>{' '}
                          {commitUrl ? (
                            <a className="repository-reference" href={commitUrl} target="_blank" rel="noreferrer" title={displayedAnalysis.commit_sha}>
                              <code>{displayedAnalysis.commit_sha.substring(0, 7)}</code> ↗
                            </a>
                          ) : (
                            <code>{displayedAnalysis.commit_sha || 'Not available'}</code>
                          )}
                        </p>
                        {pullRequestUrl && (
                          <p>
                            <strong>PR</strong>{' '}
                            <a className="repository-reference" href={pullRequestUrl} target="_blank" rel="noreferrer">
                              #{displayedAnalysis.pr_id} ↗
                            </a>
                          </p>
                        )}
                        <p>
                          <strong>Updated</strong> {new Date(displayedAnalysis.created_at).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })}
                        </p>
                        <ScopeToggle value={scopeMode} onChange={onScopeChange} />
                      </div>
                      {scopeMode === 'new-code' ? (
                        newCodePanel
                      ) : (
                        <>
                          <GateFailures analysis={displayedAnalysis} />
                          <NewCodeGateSummary analysis={displayedAnalysis} onOpen={onOpenNewCode} />
                          <MetricGrid
                            metrics={displayedAnalysis.metrics}
                            ratings={displayedAnalysis.gate_snapshot?.ratings?.overall}
                            linkableMetrics={linkableMetrics}
                            metricActionLabel={(metric, label) => (qualityDetailMetrics.has(metric) ? `View details for ${label}` : `View issues related to ${label}`)}
                            onMetricClick={(metric) => {
                              if (metric === 'coverage' || metric === 'duplication') {
                                onOpenCodeQuality(selectedSummary, displayedAnalysis, metric);
                                return;
                              }
                              const category = metricIssueCategory[metric];
                              if (category) onOpenIssues(selectedSummary, displayedAnalysis, 'all', category);
                            }}
                          />
                          <Suspense
                            fallback={
                              <p className="metric-history-status" role="status">
                                Loading metric trends...
                              </p>
                            }
                          >
                            <MetricsHistory history={selectedProjectAnalyses(selectedSummary.history, activeBranch)} />
                          </Suspense>
                        </>
                      )}
                    </div>
                  )}
                </>
              );
            })()}
          </>
        ) : (
          <section className="project-overview">
            <h2>All Projects</h2>
            <Suspense
              fallback={
                <p className="metric-history-status" role="status">
                  Loading portfolio trends...
                </p>
              }
            >
              <PortfolioTrends authToken={authToken} />
            </Suspense>
            {!loading && !summaries.length && (
              <p className="empty-state" role="status">
                {error ? 'Analysis data could not be loaded. Use Refresh to retry.' : 'No analyses available yet.'}
              </p>
            )}
            <div className="project-overview-grid">
              {summaries.map((summary) => (
                <article key={summary.projectKey} className="project-overview-card">
                  <div className="project-card-header">
                    <Button type="button" className="project-open-issues" onClick={() => setSelectedProject(summary.projectKey)}>
                      <strong>{summary.projectKey}</strong>
                      <span>Open metrics →</span>
                    </Button>
                    <FavoriteButton summary={summary} pending={favoritePending} onToggle={onToggleFavorite} />
                  </div>
                  <div className="project-latest-status">
                    <span>{summary.latest.pr_id ? `Latest · PR #${summary.latest.pr_id}` : 'Latest'}</span>
                    <span className={`status-pill ${analysisGateClass(summary.latest.quality_gate, summary.latest.completeness)}`}>
                      {analysisGateLabel(summary.latest.quality_gate, summary.latest.completeness)}
                    </span>
                    <time dateTime={summary.latest.created_at}>{new Date(summary.latest.created_at).toLocaleDateString()}</time>
                  </div>
                  <span>History</span>
                  <span className="project-overview-counts">
                    <span>Passed: {summary.passCount}</span>
                    <span>Failed: {summary.failCount}</span>
                    <span>Unknown: {summary.unknownCount}</span>
                  </span>
                  <MetricGrid
                    measuredOnly
                    metrics={summary.latest.metrics}
                    ratings={summary.latest.gate_snapshot?.ratings?.overall}
                    linkableMetrics={linkableMetrics}
                    metricActionLabel={(metric, label) => (qualityDetailMetrics.has(metric) ? `View details for ${label}` : `View issues related to ${label}`)}
                    onMetricClick={(metric) => {
                      if (metric === 'coverage' || metric === 'duplication') {
                        onOpenCodeQuality(summary, summary.latest, metric);
                        return;
                      }
                      const category = metricIssueCategory[metric];
                      if (category) onOpenIssues(summary, summary.latest, 'all', category);
                    }}
                  />
                </article>
              ))}
            </div>
          </section>
        )}
      </div>
    </section>
  );
}

function FavoriteButton({ summary, pending, onToggle }: { summary: ProjectSummary; pending: boolean; onToggle: (summary: ProjectSummary) => void }) {
  const action = summary.isFavorite ? 'Remove from favorites' : 'Add to favorites';
  return (
    <Button
      type="button"
      className={`project-favorite${summary.isFavorite ? ' active' : ''}`}
      aria-label={`${action}: ${summary.projectKey}`}
      aria-pressed={summary.isFavorite}
      title={action}
      disabled={pending}
      onClick={() => onToggle(summary)}
    >
      <span aria-hidden="true">{summary.isFavorite ? '★' : '☆'}</span>
    </Button>
  );
}

function selectedProjectAnalyses(history: AnalysisRecord[], branch: string | null) {
  return history.filter((analysis) => !branch || analysisSelectionKey(analysis) === branch);
}

type PullRequestRow = { key: string; id: string; route: string; count: number; latest: AnalysisRecord | null };

/** Pull requests are numbered upwards: the newest first. */
function comparePullRequests(left: PullRequestRow, right: PullRequestRow) {
  return Number(right.id) - Number(left.id) || right.id.localeCompare(left.id);
}

/** A branch built only through pull requests has no branch verdict: say so instead of borrowing one. */
function BranchWithoutBuild({
  branch,
  pullRequests,
  repository,
  onSelect,
}: {
  branch: string;
  pullRequests: BranchSummary[];
  repository: ProjectMeta | undefined;
  onSelect: (key: string) => void;
}) {
  const provider = repository?.repoProvider ?? 'other';
  const rows = pullRequests
    .filter((item) => item.pull_request)
    .map((item) => ({ key: item.name, id: item.pull_request!.id, route: pullRequestRoute(item.pull_request!), count: item.count, latest: item.latest }))
    .sort(comparePullRequests);
  const columns: GridColumn<PullRequestRow>[] = [
    {
      id: 'pull-request',
      header: 'Pull request',
      rowHeader: true,
      width: 150,
      value: (row) => `PR #${row.id}`,
      render: (row) => {
        const url = repositoryPullRequestLink(provider, repository?.repoUrl, row.id);
        return url ? (
          <a className="repository-reference" href={url} target="_blank" rel="noreferrer" title={`Open pull request #${row.id} in the repository`}>
            #{row.id} ↗
          </a>
        ) : (
          `#${row.id}`
        );
      },
    },
    { id: 'route', header: 'Source → target', width: 240, value: (row) => row.route, render: (row) => <code>{row.route}</code> },
    {
      id: 'gate',
      header: 'Quality Gate',
      width: 140,
      value: (row) => (row.latest ? analysisGateLabel(row.latest.quality_gate, row.latest.completeness) : 'Not available'),
      render: (row) =>
        row.latest ? (
          <span className={`status-pill ${analysisGateClass(row.latest.quality_gate, row.latest.completeness)}`}>{analysisGateLabel(row.latest.quality_gate, row.latest.completeness)}</span>
        ) : (
          'Not available'
        ),
    },
    {
      id: 'updated',
      header: 'Last analysis',
      width: 170,
      value: (row) => (row.latest ? new Date(row.latest.created_at).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' }) : 'Not available'),
    },
    {
      id: 'commit',
      header: 'Commit',
      width: 120,
      value: (row) => row.latest?.commit_sha.substring(0, 7) || 'Not available',
      tooltip: (row) => row.latest?.commit_sha,
      render: (row) => {
        const sha = row.latest?.commit_sha;
        if (!sha) return 'Not available';
        const url = repositoryCommitLink(provider, repository?.repoUrl, sha);
        return url ? (
          <a className="repository-reference" href={url} target="_blank" rel="noreferrer" title={sha}>
            <code>{sha.substring(0, 7)}</code> ↗
          </a>
        ) : (
          <code title={sha}>{sha.substring(0, 7)}</code>
        );
      },
    },
    { id: 'count', header: 'Analyses', width: 100, value: (row) => row.count },
    {
      id: 'open',
      header: <span className="sr-only">Action</span>,
      label: 'Action',
      width: 110,
      render: (row) => (
        <Button type="button" className="pull-request-open" aria-label={`Open pull request #${row.id} in CodeInsights`} onClick={() => onSelect(row.key)}>
          Open
        </Button>
      ),
    },
  ];
  return (
    // The status covers the explanation only: a live region must not announce a table of actions.
    <div className="empty-state branch-without-build">
      <h3>No branch analysis yet</h3>
      <p role="status">
        <code>{displayBranchName(branch)}</code> has only been analysed through pull requests. A pull request build analyses what the pull request merges, so it never stands in for the
        branch verdict.
      </p>
      {rows.length > 0 && (
        <Grid
          ariaLabel={`Pull requests from ${displayBranchName(branch)}`}
          caption={`Pull requests from ${displayBranchName(branch)}, newest first`}
          className="table-wrap branch-without-build-grid"
          rows={rows}
          columns={columns}
          rowKey={(row) => row.key}
        />
      )}
    </div>
  );
}

const GATE_FAILURE_LABELS: Record<string, { label: string; icon: string }> = {
  security_rating: { label: 'Security rating', icon: '🛡️' },
  maintainability_rating: { label: 'Maintainability rating', icon: '◆' },
  reliability_rating: { label: 'Reliability rating', icon: '◇' },
  security_score: { label: 'Security score', icon: '🛡️' },
  maintainability_score: { label: 'Maintainability score', icon: '◆' },
  reliability_score: { label: 'Reliability score', icon: '◇' },
  vulnerabilities: { label: 'Vulnerabilities', icon: '🛡️' },
  security_hotspots: { label: 'Security hotspots', icon: '🔍' },
  bugs: { label: 'Bugs', icon: '🐞' },
  code_smells: { label: 'Code smells', icon: '◇' },
  coverage: { label: 'Coverage', icon: '%' },
  duplicated_lines_density: { label: 'Duplicated lines', icon: '≠' },
};

function GateFailures({ analysis }: { analysis: AnalysisRecord }) {
  const conditions = (analysis.gate_snapshot?.conditions ?? []).filter((condition) => condition.scope !== 'new_code' && (condition.result === 'fail' || condition.result === 'unknown'));
  const hasStructuredConditions = (analysis.gate_snapshot?.conditions?.length ?? 0) > 0;
  if (!conditions.length && (hasStructuredConditions || !analysis.gate_failures?.length)) return null;
  const hasMeasuredFailure = conditions.some((condition) => condition.result === 'fail');

  return (
    <section className={`gate-failures${conditions.length && !hasMeasuredFailure ? ' gate-failures-unknown' : ''}`} aria-labelledby="gate-failures-title">
      <strong id="gate-failures-title">{conditions.length && !hasMeasuredFailure ? 'Quality Gate cannot be evaluated' : 'Quality Gate failures'}</strong>
      {conditions.length ? (
        <ul>
          {conditions.map((condition) => (
            <GateFailure key={`${condition.scope ?? 'global'}:${condition.metric}:${condition.operator}:${condition.threshold}`} condition={condition} />
          ))}
        </ul>
      ) : (
        <ul className="gate-failures-legacy">
          {analysis.gate_failures.map((failure, index) => (
            <li key={index}>{failure}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

function NewCodeGateSummary({ analysis, onOpen }: { analysis: AnalysisRecord; onOpen: () => void }) {
  const conditions = (analysis.gate_snapshot?.conditions ?? []).filter((condition) => condition.scope === 'new_code' && (condition.result === 'fail' || condition.result === 'unknown'));
  if (!conditions.length) return null;
  const failed = conditions.filter((condition) => condition.result === 'fail').length;
  const unknown = conditions.length - failed;
  const summary = [failed ? `${failed} failed` : '', unknown ? `${unknown} not evaluated` : ''].filter(Boolean).join(', ');
  return (
    <section className="new-code-gate-summary" aria-labelledby="new-code-gate-summary-title">
      <div>
        <strong id="new-code-gate-summary-title">New Code Quality Gate</strong>
        <span>{summary}. Detailed evidence is available in New Code.</span>
      </div>
      <Button type="button" onClick={onOpen}>
        Review New Code
      </Button>
    </section>
  );
}

function GateFailure({ condition }: { condition: GateConditionSnapshot }) {
  const presentation = GATE_FAILURE_LABELS[condition.metric] ?? {
    label: condition.metric
      .split('_')
      .join(' ')
      .replace(/^./, (letter) => letter.toUpperCase()),
    icon: '!',
  };
  const measured = condition.state === 'measured' && condition.value != null;
  const label = condition.scope === 'new_code' ? `New Code ${presentation.label.toLowerCase()}` : presentation.label;

  return (
    <li className="gate-failure-row">
      <span className="gate-failure-name">
        <span aria-hidden="true">{presentation.icon}</span>
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
}
