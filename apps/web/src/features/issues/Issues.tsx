import { API_BASE } from '@/config/env';
import { Button, Checkbox, ListBox, TextArea, TextBox } from '@/components/ui';
import InfoHint from '@/components/common/InfoHint';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiJson, apiPage } from '@/services/api';
import { findingCategory, findingCategoryLabel } from '@/utils/metric-display';
import { SLA_STATUSES } from '@/utils/sla';
import type { IssueSeverityFilter, IssueTypeFilter, ProjectMeta, ProjectSummary, ScopeMode } from '@/types/api';
import ProjectHeader from './components/ProjectHeader';
import { FindingReview, FINDING_STATES, type Team } from './components/IssueReview';
import { evidenceLink, repositoryBranchLink, repositoryCommitLink, ruleDefinitionLink } from '@/utils/evidence-links';
import type { IssueFilters } from '@/workspace/navigation-state';
import DialogCloseButton from '@/components/common/DialogCloseButton';
type Props = {
  authToken?: string;
  isAdmin: boolean;
  selectedProject: string;
  selectedBranch: string;
  selectedAnalysisId: string;
  branchLoading: boolean;
  branchError?: string | null;
  onBranchSearch?: (search: string) => void;
  branchSearchStatus?: string;
  selectedSummary: ProjectSummary | null;
  summaries: ProjectSummary[];
  projectMeta: Record<string, ProjectMeta>;
  setSelectedProject: (value: string) => void;
  setSelectedBranch: (value: string) => void;
  setSelectedAnalysisId: (value: string) => void;
  downloadExport: (url: string, filename: string) => Promise<void>;
  filters: IssueFilters;
  onFiltersChange: (filters: IssueFilters) => void;
  scopeMode: ScopeMode;
  onScopeChange: (scope: ScopeMode) => void;
  refreshing: boolean;
  onRefresh: () => void;
  newCodePanel: ReactNode;
};
type EvidenceLocation = { kind: string; flow?: number; thread?: number; step: number; path?: string; line?: number; end_line?: number; source_url?: string | null };
type FindingEvidence = {
  driver_version?: string;
  documentation_url?: string;
  documentation_source?: string;
  documentation_version?: string;
  documentation_status?: string;
  locations?: EvidenceLocation[];
  locations_truncated?: boolean;
};
type IssueOccurrence = {
  id: number;
  finding_id: string | null;
  tool: string;
  original_tool?: string;
  tool_version?: string;
  rule_id: string;
  severity: string;
  category: string;
  message: string;
  path: string | null;
  line: number | null;
  source_url?: string | null;
  evidence?: FindingEvidence | null;
  prompt_available: boolean;
  prompt_unavailable_reason?: string | null;
};

export default function Issues({
  authToken,
  isAdmin,
  selectedProject,
  selectedBranch,
  selectedAnalysisId,
  branchLoading,
  branchError,
  onBranchSearch,
  branchSearchStatus,
  selectedSummary,
  summaries,
  projectMeta,
  setSelectedProject,
  setSelectedBranch,
  setSelectedAnalysisId,
  downloadExport,
  filters,
  onFiltersChange,
  scopeMode,
  onScopeChange,
  refreshing,
  onRefresh,
  newCodePanel,
}: Props) {
  const [expandedIssueId, setExpandedIssueId] = useState<string | null>(null);
  const promptDialog = useRef<HTMLDialogElement>(null);
  const [promptState, setPromptState] = useState<{ issueId: string; prompt: string; loading: boolean; error: string | null; copied: boolean } | null>(null);
  useEffect(() => {
    if (!promptState) return;
    promptDialog.current?.showModal();
  }, [promptState?.issueId]);
  useEffect(() => {
    if (!promptState?.copied) return;
    const timeout = window.setTimeout(() => {
      setPromptState((current) => current ? { ...current, copied: false } : current);
    }, 2000);
    return () => window.clearTimeout(timeout);
  }, [promptState?.copied]);
  const closePrompt = () => {
    promptDialog.current?.close();
    setPromptState(null);
  };
  const loadPrompt = async (issueId: string) => {
    setPromptState({ issueId, prompt: '', loading: true, error: null, copied: false });
    try {
      const result = await apiJson<{ prompt: string }>(`${API_BASE}/v1/issues/${encodeURIComponent(issueId)}/prompt`, authToken);
      setPromptState({ issueId, prompt: result.prompt, loading: false, error: null, copied: false });
    } catch (error) {
      setPromptState({ issueId, prompt: '', loading: false, error: error instanceof Error ? error.message : 'Unable to generate the prompt.', copied: false });
    }
  };
  const { severity: issueSeverityFilter, category: issueTypeFilter, rule: issueRuleFilter, file: issueFileFilter, mine, state, team: teamId, sla } = filters;
  const updateFilters = (patch: Partial<IssueFilters>) => onFiltersChange({ ...filters, ...patch });
  const [teamPage, setTeamPage] = useState({ project: '', cursor: '' });
  const teamCursor = teamPage.project === selectedProject ? teamPage.cursor : '';
  const teams = useQuery({
    queryKey: ['collaboration', 'teams', selectedProject, teamCursor],
    enabled: !!selectedProject && scopeMode === 'overall',
    queryFn: ({ signal }) => apiPage<Team>(`${API_BASE}/v1/collaboration/teams?${new URLSearchParams({ project_key: selectedProject, cursor: teamCursor })}`, authToken, { signal }),
  });
  const filterKey = [selectedAnalysisId, issueSeverityFilter, issueTypeFilter, issueRuleFilter, issueFileFilter, mine, state, teamId, sla].join('|');
  const [page, setPage] = useState({ key: '', cursor: '' });
  const cursor = page.key === filterKey ? page.cursor : '';
  const exportQuery = new URLSearchParams({
    severity: issueSeverityFilter,
    category: issueTypeFilter,
    rule: issueRuleFilter,
    path: issueFileFilter,
    mine: mine ? 'true' : '',
    state,
    team_id: teamId,
    sla_status: sla,
  });
  const issuesQuery = useQuery({
    queryKey: ['findings', filterKey, cursor],
    enabled: !!selectedAnalysisId && scopeMode === 'overall',
    queryFn: ({ signal }) =>
      apiPage<IssueOccurrence>(
        `${API_BASE}/v1/insights/analyses/${encodeURIComponent(selectedAnalysisId)}/findings?${new URLSearchParams({ severity: issueSeverityFilter, category: issueTypeFilter, rule: issueRuleFilter, path: issueFileFilter, mine: mine ? 'true' : '', state, team_id: teamId, sla_status: sla, cursor })}`,
        authToken,
        { signal },
      ),
  });
  const issuesLoading = issuesQuery.isLoading;
  const issuesError = issuesQuery.error?.message ?? null;
  const selectedAnalysis = selectedSummary?.history.find((item) => item.analysis_id === selectedAnalysisId);
  const repository = projectMeta[selectedProject];
  const branchUrl = repositoryBranchLink(repository?.repoProvider ?? 'other', repository?.repoUrl, selectedAnalysis?.branch);
  const filteredIssueDetails = useMemo(
    () =>
      (issuesQuery.data?.items ?? []).map((issue) => ({
        id: String(issue.id),
        finding_id: issue.finding_id,
        file_path: issue.path ?? '',
        line: issue.line,
        end_line: issue.evidence?.locations?.find((location) => location.kind === 'primary')?.end_line,
        rule: issue.rule_id,
        message: issue.message,
        severity: issue.severity.toUpperCase(),
        category: findingCategory(issue.category),
        tool: issue.tool,
        commit_sha: selectedAnalysis?.commit_sha ?? '',
        branch: selectedAnalysis?.branch ?? '',
        source_url: evidenceLink(issue.source_url),
        evidence: issue.evidence,
        documentation_url: evidenceLink(issue.evidence?.documentation_url),
        producer: issue.original_tool ?? issue.tool,
        tool_version: issue.evidence?.driver_version ?? issue.tool_version,
        prompt_available: issue.prompt_available,
        prompt_unavailable_reason: issue.prompt_unavailable_reason,
      })),
    [issuesQuery.data, selectedAnalysis],
  );
  const allIssueDetails = filteredIssueDetails;

  const projectHeader = (
    <div style={{ gridColumn: '1 / -1' }}>
      <ProjectHeader
        selectedSummary={selectedSummary}
        allSummaries={summaries}
        onProjectChange={setSelectedProject}
        onBranchChange={setSelectedBranch}
        selectedBranch={selectedBranch}
        branchLoading={branchLoading}
        branchError={branchError}
        onBranchSearch={onBranchSearch}
        branchSearchStatus={branchSearchStatus}
        selectedAnalysisId={selectedAnalysisId}
        onAnalysisChange={setSelectedAnalysisId}
        repoProvider={repository?.repoProvider}
        repoUrl={repository?.repoUrl}
        scopeMode={scopeMode}
        onScopeChange={onScopeChange}
        refreshing={refreshing}
        onRefresh={onRefresh}
      />
    </div>
  );

  // New Code has no findings filter on the API: the comparison replaces the list, under the same header.
  if (scopeMode === 'new-code') {
    return (
      <section className="two-pane-screen issues-screen">
        {projectHeader}
        <div style={{ gridColumn: '1 / -1' }}>{newCodePanel}</div>
      </section>
    );
  }

  return (
    <section className="two-pane-screen issues-screen">
      {/* ── Project Header ──────────────────────────────── */}
      {projectHeader}

      {/* ── Left sidebar filters ──────────────────────────────── */}
      <aside className="issues-sidebar">
        <h3>Filters</h3>
        <div className="issue-filter-group">
          <label className="collaboration-checkbox">
            <Checkbox type="checkbox" checked={mine} onChange={(event) => updateFilters({ mine: event.target.checked })} /> Assigned to me
          </label>
          <label>
            Review status
            <ListBox value={state} onChange={(event) => updateFilters({ state: event.target.value })}>
              <option value="">Any status</option>
              {Object.entries(FINDING_STATES).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </ListBox>
          </label>
          <label>
            SLA status
            <ListBox value={sla} onChange={(event) => updateFilters({ sla: event.target.value })}>
              <option value="">Any SLA status</option>
              {Object.entries(SLA_STATUSES).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </ListBox>
          </label>
          <label>
            Assigned team
            <ListBox value={teamId} disabled={!teams.isSuccess} onChange={(event) => updateFilters({ team: event.target.value })}>
              <option value="">Any team</option>
              {teamId && !teams.data?.items.some((item) => item.id === teamId) && <option value={teamId}>Selected team from another page</option>}
              {teams.data?.items.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name}
                </option>
              ))}
            </ListBox>
          </label>
          {teams.error && <p role="alert">{teams.error.message}</p>}
          {teamCursor && (
            <Button type="button" onClick={() => setTeamPage({ project: selectedProject, cursor: '' })}>
              First page
            </Button>
          )}
          {teams.data?.next && (
            <Button type="button" onClick={() => setTeamPage({ project: selectedProject, cursor: teams.data!.next! })}>
              Next
            </Button>
          )}
        </div>

        <div className="issue-filter-group">
          <p className="issue-filter-label">Category</p>
          {(['all', 'bug', 'code_smell', 'vulnerability', 'security_hotspot', 'secret', 'dependency', 'code', 'ioc', 'unknown'] as IssueTypeFilter[]).map((t) => (
            <Button key={t} type="button" aria-pressed={issueTypeFilter === t} className={`issue-filter-btn${issueTypeFilter === t ? ' active' : ''}`} onClick={() => updateFilters({ category: t })}>
              {t === 'all' ? 'All categories' : findingCategoryLabel(t)}
            </Button>
          ))}
        </div>

        <div className="issue-filter-group">
          <p className="issue-filter-label">Severity</p>
          {(['all', 'blocker', 'high', 'medium', 'low', 'info'] as IssueSeverityFilter[]).map((sev) => (
            <Button
              key={sev}
              type="button"
              aria-pressed={issueSeverityFilter === sev}
              className={`issue-filter-btn${issueSeverityFilter === sev ? ' active' : ''}`}
              onClick={() => updateFilters({ severity: sev })}
            >
              {sev === 'all' ? 'All severities' : sev.charAt(0).toUpperCase() + sev.slice(1)}
            </Button>
          ))}
        </div>

        <div className="issue-filter-group">
          <p className="issue-filter-label">Rule</p>
          <TextBox aria-label="Filter by rule" placeholder="Filter by rule…" value={issueRuleFilter} onChange={(e) => updateFilters({ rule: e.target.value })} />
        </div>

        <div className="issue-filter-group">
          <p className="issue-filter-label">File</p>
          <TextBox aria-label="Filter by file path" placeholder="Filter by file path…" value={issueFileFilter} onChange={(e) => updateFilters({ file: e.target.value })} />
        </div>

        <Button
          type="button"
          style={{ marginTop: '0.75rem', width: '100%' }}
          onClick={() => {
            onFiltersChange({ severity: 'all', category: 'all', rule: '', file: '', mine: false, state: '', team: '', sla: '' });
            setTeamPage({ project: selectedProject, cursor: '' });
          }}
        >
          Reset filters
        </Button>
      </aside>

      {/* ── Right issues list ─────────────────────────────────── */}
      <div className="issues-main">
        <header className="issues-toolbar">
          <div className="issues-toolbar-summary">
            <h2>
              Issues{' '}
              <InfoHint>
                Findings detected in the selected analysis. The list contains only detailed findings; a file summary never creates an artificial issue. Code links open the exact analyzed commit.
              </InfoHint>
            </h2>
          </div>
          <div className="issues-export-actions">
            <Button
              type="button"
              className="icon-button"
              aria-label="Export filtered issues as CSV"
              title="Export CSV"
              disabled={!selectedAnalysisId}
              onClick={() => void downloadExport(`${API_BASE}/v1/insights/analyses/${encodeURIComponent(selectedAnalysisId)}/findings/export/csv?${exportQuery}`, `${selectedProject}-${selectedAnalysisId}-issues.csv`)}
            >
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M12 3v12m0 0 4-4m-4 4-4-4M5 18v2h14v-2" />
              </svg>
            </Button>
          </div>
        </header>

        {!selectedAnalysisId ? (
          <div className="issues-empty" role="status">
            {selectedBranch
              ? 'No branch analysis yet: this branch has only been analysed through pull requests. Select one of its pull requests in Branch.'
              : 'Select a project and a branch to list its findings.'}
          </div>
        ) : issuesLoading ? (
          <p className="feedback" role="status">
            Loading file findings…
          </p>
        ) : issuesError ? (
          <p className="feedback error" role="alert">
            {issuesError}.{' '}
            <Button type="button" onClick={() => void issuesQuery.refetch()}>
              Retry
            </Button>
          </p>
        ) : filteredIssueDetails.length === 0 ? (
          <div className="issues-empty">{allIssueDetails.length === 0 ? 'No file findings available in the loaded analyses for this scope.' : 'No issues match the current filters.'}</div>
        ) : (
          <div className="issues-list">
            {filteredIssueDetails.map((issue) => {
              const severityLower = issue.severity.toLowerCase();
              const sevClass =
                severityLower === 'critical' || severityLower === 'error' || severityLower === 'blocker'
                  ? 'sev-blocker'
                  : severityLower === 'high'
                    ? 'sev-high'
                    : severityLower === 'warning' || severityLower === 'medium'
                      ? 'sev-medium'
                      : severityLower === 'low' || severityLower === 'convention'
                        ? 'sev-low'
                        : 'sev-info';
              const shortFile = issue.file_path.split('/').slice(-2).join('/');
              const lineLabel = issue.end_line && issue.end_line !== issue.line ? `L${issue.line}–${issue.end_line}` : issue.line == null ? 'Line not available' : `L${issue.line}`;
              const isExpanded = expandedIssueId === issue.id;

              const definitionUrl = issue.documentation_url ?? ruleDefinitionLink(issue.tool, issue.rule);
              const issueBranchUrl = repositoryBranchLink(repository?.repoProvider ?? 'other', repository?.repoUrl, issue.branch);
              const issueCommitUrl = repositoryCommitLink(repository?.repoProvider ?? 'other', repository?.repoUrl, issue.commit_sha);
              const relatedLocations = issue.evidence?.locations?.filter((location) => location.kind === 'related') ?? [];
              const flowLocations = issue.evidence?.locations?.filter((location) => location.kind === 'flow') ?? [];
              const evidenceGroups: { heading: string; locations: EvidenceLocation[] }[] = [
                { heading: 'Related locations', locations: relatedLocations },
                { heading: 'Code flow', locations: flowLocations },
              ];
              return (
                <article key={issue.id} className={`issue-card ${sevClass}${isExpanded ? ' expanded' : ''}`}>
                  <div className="issue-card-header">
                    <div className="issue-card-left">
                      <span className={`sev-badge ${sevClass}`}>{issue.severity}</span>
                    </div>
                    <div className="issue-card-body">
                      <p className="issue-message">{issue.message}</p>
                      <p className="issue-meta">
                        <span className="issue-rule">
                          <span>{findingCategoryLabel(issue.category)}</span>
                          <span>{issue.tool}</span>
                          <code>{issue.rule}</code>
                        </span>
                        <span className="issue-revision">
                          {issueBranchUrl ? <a href={issueBranchUrl} target="_blank" rel="noreferrer"><code>{issue.branch}</code></a> : <code>{issue.branch}</code>}
                          {' @ '}
                          {issueCommitUrl ? <a href={issueCommitUrl} target="_blank" rel="noreferrer"><code>{issue.commit_sha.slice(0, 7)}</code></a> : <code>{issue.commit_sha.slice(0, 7) || 'Not available'}</code>}
                        </span>
                        {issue.source_url ? (
                          <a href={issue.source_url} target="_blank" rel="noreferrer" className="issue-file-link" title={issue.file_path}>
                            <span className="issue-file">{shortFile}</span>
                            <span className="issue-file-colon">:</span>
                            <span className="issue-line">{lineLabel}</span>
                            <span className="issue-ext-icon">↗</span>
                          </a>
                        ) : (
                          <>
                            <span className="issue-file" title={issue.file_path}>
                              {shortFile}
                            </span>
                            <span className="issue-line">{lineLabel}</span>
                          </>
                        )}
                      </p>
                    </div>
                    <Button
                      type="button"
                      className="issue-expand-btn"
                      aria-expanded={isExpanded}
                      aria-label={isExpanded ? 'Collapse file details' : 'Expand file details'}
                      onClick={() => setExpandedIssueId(isExpanded ? null : issue.id)}
                      title={isExpanded ? 'Collapse file details' : 'Expand file details'}
                    >
                      {isExpanded ? '▾' : '▸'}
                    </Button>
                  </div>

                  {isExpanded && (
                    <div className="issue-details">
                      <div className="issue-actions">
                        {issue.source_url ? (
                          <a href={issue.source_url} target="_blank" rel="noreferrer" className="btn-open-repo">
                            Open at analyzed commit
                          </a>
                        ) : (
                          <span>Code link unavailable for this analysis.</span>
                        )}
                        <Button
                          type="button"
                          disabled={!issue.prompt_available}
                          title={issue.prompt_available ? undefined : `Prompt not available: ${issue.prompt_unavailable_reason ?? 'source missing'}`}
                          onClick={() => void loadPrompt(issue.id)}
                        >
                          Generate prompt
                        </Button>
                        {!issue.prompt_available && <span>Prompt not available ({issue.prompt_unavailable_reason ?? 'source missing'}).</span>}
                      </div>
                      <section className="issue-guidance" aria-label="Rule guidance">
                        <h4>Rule guidance</h4>
                        <p>
                          <strong>{issue.producer}</strong> · <code>{issue.rule}</code> · analyzer {issue.tool_version ?? 'unknown version'}
                        </p>
                        {definitionUrl ? (
                          <>
                            <a href={definitionUrl} target="_blank" rel="noreferrer">
                              Read the official rule guidance ↗
                            </a>
                            <InfoHint>
                              {issue.documentation_url
                                ? `Link supplied by the producer in the report (${issue.evidence?.documentation_source ?? 'unspecified provenance'}). Documentation version: ${issue.evidence?.documentation_version ?? 'unknown'}.`
                                : 'Link built from the recognized official public registry for this identifier. CodeInsights does not store rule definitions.'}
                            </InfoHint>
                          </>
                        ) : (
                          <p>Rule documentation is not available.</p>
                        )}
                      </section>
                      {(relatedLocations.length > 0 || flowLocations.length > 0 || issue.evidence?.locations_truncated) && (
                        <section className="issue-context" aria-label="Report locations and flows">
                          <h4>Contextual evidence</h4>
                          {evidenceGroups.map(({ heading, locations }) =>
                            locations.length > 0 ? (
                              <div key={heading}>
                                <p>{heading} · {locations.length}</p>
                                <ol>
                                  {locations.map((location, index) => {
                                    const link = evidenceLink(location.source_url);
                                    const label = `${location.path ?? 'File not available'} · ${location.line == null ? 'line not available' : `L${location.line}${location.end_line ? `–${location.end_line}` : ''}`}`;
                                    return (
                                      <li key={index}>
                                        {location.kind === 'flow' && (
                                          <span>
                                            Flow {location.flow} · thread {location.thread} · step {location.step}:{' '}
                                          </span>
                                        )}
                                        {link ? (
                                          <a href={link} target="_blank" rel="noreferrer">
                                            {label}
                                          </a>
                                        ) : (
                                          <span>{label}</span>
                                        )}
                                      </li>
                                    );
                                  })}
                                </ol>
                              </div>
                            ) : null,
                          )}
                          {issue.evidence?.locations_truncated && <p role="status">Evidence list truncated at the import limit.</p>}
                        </section>
                      )}
                      <FindingReview
                        key={`${selectedAnalysisId}:${issue.id}`}
                        findingId={issue.finding_id}
                        occurrenceId={Number(issue.id)}
                        projectKey={selectedProject}
                        authToken={authToken}
                        isAdmin={isAdmin}
                        branchUrl={branchUrl}
                      />
                    </div>
                  )}
                </article>
              );
            })}
          </div>
        )}
        {(cursor || issuesQuery.data?.next) && (
          <div className="action-row pagination-row" aria-label="Issue pages">
            {cursor && (
              <Button type="button" onClick={() => setPage({ key: filterKey, cursor: '' })}>
                First page
              </Button>
            )}
            {issuesQuery.data?.next && (
              <Button type="button" onClick={() => setPage({ key: filterKey, cursor: issuesQuery.data!.next! })}>
                Next
              </Button>
            )}
          </div>
        )}
      </div>
      {promptState && (
        <dialog ref={promptDialog} className="config-modal prompt-modal" aria-labelledby="prompt-dialog-title" onCancel={(event) => { event.preventDefault(); closePrompt(); }}>
          <div className="config-modal-header">
            <h3 id="prompt-dialog-title">Contextual AI prompt</h3>
            <div className="prompt-modal-actions">
              <Button
                type="button"
                className={`icon-button prompt-copy-button${promptState.copied ? ' copied' : ''}`}
                aria-label={promptState.copied ? 'Prompt copied' : 'Copy prompt'}
                title={promptState.copied ? 'Prompt copied' : 'Copy prompt'}
                disabled={!promptState.prompt}
                onClick={async () => {
                  try {
                    await navigator.clipboard.writeText(promptState.prompt);
                    setPromptState((current) => current ? { ...current, copied: true, error: null } : current);
                  } catch {
                    setPromptState((current) => current ? { ...current, copied: false, error: 'Could not copy to the clipboard.' } : current);
                  }
                }}
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <rect x="8" y="8" width="11" height="11" rx="2" />
                  <path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" />
                </svg>
                <span role="status">{promptState.copied ? 'Copied' : 'Copy'}</span>
              </Button>
              <DialogCloseButton onClick={closePrompt} />
            </div>
          </div>
          {promptState.loading && <p role="status">Generating prompt…</p>}
          {promptState.error && <p className="feedback error" role="alert">{promptState.error}</p>}
          {promptState.prompt && <TextArea aria-label="AI prompt" readOnly value={promptState.prompt} rows={18} />}
        </dialog>
      )}
    </section>
  );
}
