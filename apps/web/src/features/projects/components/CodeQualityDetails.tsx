import { API_BASE } from '@/config/env';
import { Button, Grid, TextBox } from '@/components/ui';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { apiFetch } from '@/services/api';
import { evidenceLink, repositoryBranchLink, repositoryCommitLink } from '@/utils/evidence-links';
import { formatMetric } from '@/utils/metric-display';
import type { ProjectMetric } from '@/workspace/navigation-state';
import { displayBranchName } from '@/utils/branch-selection';

type DetailState = 'measured' | 'not_measured' | 'unavailable';
type DetailReason = 'report_not_measured' | 'direct_ingestion' | 'details_purged' | 'details_unavailable' | null;
type MetricState = { state: DetailState; reason: DetailReason };
type CoverageSummary = MetricState & {
  covered_lines: number | null;
  uncovered_lines: number | null;
  total_lines: number | null;
  percentage: number | null;
};
type DuplicationSummary = MetricState & {
  density_percentage: number | null;
  duplicate_blocks: number | null;
  duplicated_lines: number | null;
};
type CodeQualitySummary = {
  analysis_id: string;
  project_key: string;
  branch: string;
  commit_sha: string;
  coverage: CoverageSummary;
  duplication: DuplicationSummary;
};
type CoverageFile = {
  path: string;
  covered_lines: number;
  uncovered_lines: number;
  total_lines: number;
  percentage: number | null;
  source_url: string | null;
};
type CoverageFilesPage = MetricState & { files: CoverageFile[] };
type DuplicateLocation = { path: string; start_line: number; end_line: number; source_url: string | null };
type Duplication = {
  report_id: string;
  tool: string;
  tool_version: string;
  language: string;
  lines: number;
  tokens: number | null;
  first: DuplicateLocation;
  second: DuplicateLocation;
};
type DuplicationsPage = MetricState & { duplications: Duplication[] };
type SourceDocument = {
  analysis_id: string;
  path: string;
  commit_sha: string;
  provider: 'azure-devops';
  source_url: string | null;
  line_count: number;
  content: string;
  coverage: Array<{ line: number; covered: boolean }>;
};
type Page<T> = { body: T; next: string | null };

async function apiPage<T>(url: string, token: string | undefined, signal: AbortSignal): Promise<Page<T>> {
  const response = await apiFetch(url, token, { signal });
  return { body: (await response.json()) as T, next: response.headers.get('X-Next-Cursor') };
}

export default function CodeQualityDetails({
  analysisId,
  metric,
  authToken,
  repoProvider,
  repoUrl,
  projectHref,
  onMetricChange,
  onBack,
}: {
  analysisId: string;
  metric: Exclude<ProjectMetric, ''>;
  authToken?: string;
  repoProvider?: 'github' | 'azure-devops' | 'other';
  repoUrl?: string;
  projectHref: string;
  onMetricChange: (metric: Exclude<ProjectMetric, ''>) => void;
  onBack: () => void;
}) {
  const metrics = ['coverage', 'duplication'] as const;
  const selectMetric = (next: Exclude<ProjectMetric, ''>, focus = false) => {
    onMetricChange(next);
    if (focus) requestAnimationFrame(() => document.getElementById(`code-quality-${next}-tab`)?.focus());
  };
  const summary = useQuery({
    queryKey: ['code-quality', analysisId],
    enabled: !!authToken && !!analysisId,
    queryFn: ({ signal }) =>
      apiFetch(`${API_BASE}/v1/insights/analyses/${encodeURIComponent(analysisId)}/code-quality`, authToken, { signal }).then((response) => response.json() as Promise<CodeQualitySummary>),
  });
  const branchUrl = repositoryBranchLink(repoProvider ?? 'other', repoUrl, summary.data?.branch);
  const commitUrl = repositoryCommitLink(repoProvider ?? 'other', repoUrl, summary.data?.commit_sha);

  return (
    <section className="code-quality-details" aria-labelledby="code-quality-title">
      <header className="code-quality-header">
        <Button type="button" className="detail-back-button" aria-label="Back to project" title="Back to project" onClick={onBack}>
          <span aria-hidden="true">←</span>
        </Button>
        <div>
          <h2 id="code-quality-title">Code quality details</h2>
          {summary.data && (
            <nav className="detail-breadcrumb" aria-label="Code quality context">
              <a
                href={projectHref}
                title={summary.data.project_key}
                onClick={(event) => {
                  event.preventDefault();
                  onBack();
                }}
              >
                {summary.data.project_key}
              </a>
              <span aria-hidden="true">/</span>
              {branchUrl ? (
                <a href={branchUrl} target="_blank" rel="noreferrer" title={`Open ${summary.data.branch} in the repository`}>
                  <code>{displayBranchName(summary.data.branch)}</code>
                </a>
              ) : (
                <code title={summary.data.branch}>{displayBranchName(summary.data.branch)}</code>
              )}
              <span aria-hidden="true">/</span>
              {commitUrl ? (
                <a href={commitUrl} target="_blank" rel="noreferrer" title={`Open commit ${summary.data.commit_sha} in the repository`}>
                  <code>{summary.data.commit_sha}</code>
                </a>
              ) : (
                <code title={summary.data.commit_sha}>{summary.data.commit_sha}</code>
              )}
            </nav>
          )}
        </div>
      </header>

      <div className="code-quality-tabs" role="tablist" aria-label="Code quality metric">
        {metrics.map((candidate) => (
          <Button
            key={candidate}
            type="button"
            role="tab"
            id={`code-quality-${candidate}-tab`}
            aria-controls={`code-quality-${candidate}-panel`}
            aria-selected={metric === candidate}
            tabIndex={metric === candidate ? 0 : -1}
            className={metric === candidate ? 'active' : ''}
            onClick={() => selectMetric(candidate)}
            onKeyDown={(event) => {
              const current = metrics.indexOf(candidate);
              const next =
                event.key === 'ArrowRight'
                  ? metrics[(current + 1) % metrics.length]
                  : event.key === 'ArrowLeft'
                    ? metrics[(current - 1 + metrics.length) % metrics.length]
                    : event.key === 'Home'
                      ? metrics[0]
                      : event.key === 'End'
                        ? metrics[metrics.length - 1]
                        : null;
              if (!next) return;
              event.preventDefault();
              selectMetric(next, true);
            }}
          >
            {candidate === 'coverage' ? 'Coverage' : 'Duplications'}
          </Button>
        ))}
      </div>

      {!analysisId && (
        <p className="code-quality-state" role="alert">
          The selected analysis is missing.
        </p>
      )}
      {analysisId && summary.isLoading && (
        <p className="code-quality-state" role="status">
          Loading code quality details...
        </p>
      )}
      {summary.error && (
        <div className="code-quality-state error" role="alert">
          <p>Code quality details could not be loaded: {summary.error.message}</p>
          <Button type="button" onClick={() => void summary.refetch()}>
            Retry
          </Button>
        </div>
      )}
      {summary.data && metric === 'coverage' && <CoverageDetails analysisId={analysisId} authToken={authToken} summary={summary.data.coverage} />}
      {summary.data && metric === 'duplication' && <DuplicationDetails analysisId={analysisId} authToken={authToken} summary={summary.data.duplication} />}
    </section>
  );
}

function CoverageDetails({ analysisId, authToken, summary }: { analysisId: string; authToken?: string; summary: CoverageSummary }) {
  const [search, setSearch] = useState('');
  const [expandedPath, setExpandedPath] = useState('');
  const files = useInfiniteQuery({
    queryKey: ['code-quality', analysisId, 'coverage-files', search],
    enabled: summary.state === 'measured' && (summary.total_lines ?? 0) > 0,
    initialPageParam: '',
    queryFn: ({ pageParam, signal }) => {
      const params = new URLSearchParams({ search, cursor: pageParam, limit: '50' });
      return apiPage<CoverageFilesPage>(`${API_BASE}/v1/insights/analyses/${encodeURIComponent(analysisId)}/coverage/files?${params}`, authToken, signal);
    },
    getNextPageParam: (lastPage) => lastPage.next || undefined,
  });
  const rows = files.data?.pages.flatMap((page) => page.body.files) ?? [];
  const pageState = files.data?.pages[0]?.body;
  const expandedFile = rows.find((file) => file.path === expandedPath);

  if (summary.state !== 'measured') return <UnavailableState metric="coverage" state={summary.state} reason={summary.reason} />;
  return (
    <div className="code-quality-panel" id="code-quality-coverage-panel" role="tabpanel" aria-labelledby="code-quality-coverage-tab">
      <SummaryValues
        values={[
          ['Coverage', formatMetric(summary.percentage, '%')],
          ['Covered lines', formatMetric(summary.covered_lines)],
          ['Uncovered lines', formatMetric(summary.uncovered_lines)],
          ['Executable lines', formatMetric(summary.total_lines)],
        ]}
      />
      {summary.total_lines === 0 ? (
        <p className="code-quality-state" role="status">
          No executable coverage lines.
        </p>
      ) : (
        <>
          <label className="code-quality-search">
            Search coverage files
            <TextBox value={search} onChange={(event) => setSearch(event.target.value)} type="search" />
          </label>
          {pageState && pageState.state !== 'measured' ? (
            <UnavailableState metric="coverage" state={pageState.state} reason={pageState.reason} />
          ) : files.isLoading ? (
            <p className="code-quality-state" role="status">
              Loading coverage files...
            </p>
          ) : files.error ? (
            <ListError message={files.error.message} retry={() => void files.refetch()} />
          ) : rows.length === 0 ? (
            <p className="code-quality-state" role="status">
              {search ? 'No coverage files match this search.' : 'No coverage files are available.'}
            </p>
          ) : (
            <>
              <Grid<CoverageFile>
                ariaLabel="Coverage files"
                className="table-wrap code-quality-table"
                rows={rows}
                rowKey={(file) => file.path}
                columns={[
                  { id: 'file', header: 'File', rowHeader: true, value: (file) => file.path, render: (file) => <SourceLink url={file.source_url} label={file.path} />, width: 360 },
                  { id: 'coverage', header: 'Coverage', value: (file) => formatMetric(file.percentage, '%') },
                  { id: 'covered', header: 'Covered', value: (file) => file.covered_lines },
                  { id: 'uncovered', header: 'Uncovered', value: (file) => file.uncovered_lines },
                  {
                    id: 'actions',
                    header: <span className="sr-only">Actions</span>,
                    label: 'Actions',
                    render: (file) => {
                      const open = expandedPath === file.path;
                      return (
                        <Button type="button" aria-expanded={open} aria-controls={`coverage-lines-${safeId(file.path)}`} onClick={() => setExpandedPath(open ? '' : file.path)}>
                          {open ? 'Hide code' : 'View code'}
                        </Button>
                      );
                    },
                  },
                ]}
              />
              {expandedFile && (
                <div className="source-viewer-row" id={`coverage-lines-${safeId(expandedFile.path)}`}>
                  <SourceViewer analysisId={analysisId} authToken={authToken} path={expandedFile.path} fallbackUrl={expandedFile.source_url} highlight={{ kind: 'coverage' }} />
                </div>
              )}
            </>
          )}
          {files.hasNextPage && <NextButton label="Next coverage files" pending={files.isFetchingNextPage} action={() => void files.fetchNextPage()} />}
        </>
      )}
    </div>
  );
}

function DuplicationDetails({ analysisId, authToken, summary }: { analysisId: string; authToken?: string; summary: DuplicationSummary }) {
  const [search, setSearch] = useState('');
  const duplicates = useInfiniteQuery({
    queryKey: ['code-quality', analysisId, 'duplications', search],
    enabled: summary.state === 'measured' && (summary.duplicate_blocks ?? 0) > 0,
    initialPageParam: '',
    queryFn: ({ pageParam, signal }) => {
      const params = new URLSearchParams({ search, cursor: pageParam, limit: '50' });
      return apiPage<DuplicationsPage>(`${API_BASE}/v1/insights/analyses/${encodeURIComponent(analysisId)}/duplications?${params}`, authToken, signal);
    },
    getNextPageParam: (lastPage) => lastPage.next || undefined,
  });
  const rows = duplicates.data?.pages.flatMap((page) => page.body.duplications) ?? [];
  const pageState = duplicates.data?.pages[0]?.body;

  if (summary.state !== 'measured') return <UnavailableState metric="duplication" state={summary.state} reason={summary.reason} />;
  return (
    <div className="code-quality-panel" id="code-quality-duplication-panel" role="tabpanel" aria-labelledby="code-quality-duplication-tab">
      <SummaryValues
        values={[
          ['Duplication density', formatMetric(summary.density_percentage, '%')],
          ['Duplicate blocks', formatMetric(summary.duplicate_blocks)],
          ['Duplicated lines', formatMetric(summary.duplicated_lines)],
        ]}
      />
      {(summary.duplicate_blocks ?? 0) === 0 ? (
        <p className="code-quality-state" role="status">
          No duplicated blocks.
        </p>
      ) : (
        <>
          <label className="code-quality-search">
            Search duplicated paths
            <TextBox value={search} onChange={(event) => setSearch(event.target.value)} type="search" />
          </label>
          {pageState && pageState.state !== 'measured' ? (
            <UnavailableState metric="duplication" state={pageState.state} reason={pageState.reason} />
          ) : duplicates.isLoading ? (
            <p className="code-quality-state" role="status">
              Loading duplicated blocks...
            </p>
          ) : duplicates.error ? (
            <ListError message={duplicates.error.message} retry={() => void duplicates.refetch()} />
          ) : rows.length === 0 ? (
            <p className="code-quality-state" role="status">
              No duplicated blocks match this search.
            </p>
          ) : (
            <div className="duplication-list" aria-label="Duplicated blocks">
              {rows.map((duplicate, index) => (
                <DuplicateBlock
                  key={`${duplicate.report_id}:${duplicate.first.path}:${duplicate.first.start_line}:${duplicate.second.path}:${duplicate.second.start_line}:${index}`}
                  analysisId={analysisId}
                  authToken={authToken}
                  duplicate={duplicate}
                  index={index}
                />
              ))}
            </div>
          )}
          {duplicates.hasNextPage && <NextButton label="Next duplicated blocks" pending={duplicates.isFetchingNextPage} action={() => void duplicates.fetchNextPage()} />}
        </>
      )}
    </div>
  );
}

function DuplicateBlock({ analysisId, authToken, duplicate, index }: { analysisId: string; authToken?: string; duplicate: Duplication; index: number }) {
  const [open, setOpen] = useState(false);
  const panelId = `duplicate-source-${safeId(`${duplicate.report_id}-${duplicate.first.path}-${duplicate.first.start_line}-${duplicate.second.path}-${duplicate.second.start_line}`)}`;
  return (
    <article>
      <header>
        <strong>Duplicate block {index + 1}</strong>
        <span>
          <code>{duplicate.language}</code> / {duplicate.lines} lines{duplicate.tokens == null ? '' : ` / ${duplicate.tokens} tokens`}
        </span>
      </header>
      <p className="duplication-provenance">
        Report <code>{duplicate.report_id}</code> /{' '}
        <code>
          {duplicate.tool}
          {duplicate.tool_version ? ` ${duplicate.tool_version}` : ''}
        </code>
      </p>
      <div className="duplication-locations">
        <DuplicateLocationView label="First location" location={duplicate.first} />
        <DuplicateLocationView label="Second location" location={duplicate.second} />
      </div>
      <div className="duplication-source-action">
        <Button type="button" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen((value) => !value)}>
          {open ? 'Hide source comparison' : 'Compare source'}
        </Button>
      </div>
      {open && (
        <div className="duplication-source-grid" id={panelId} aria-label={`Source comparison for duplicate block ${index + 1}`}>
          <SourceViewer
            analysisId={analysisId}
            authToken={authToken}
            path={duplicate.first.path}
            fallbackUrl={duplicate.first.source_url}
            highlight={{ kind: 'duplicate', start: duplicate.first.start_line, end: duplicate.first.end_line, label: 'Duplicate A' }}
          />
          <SourceViewer
            analysisId={analysisId}
            authToken={authToken}
            path={duplicate.second.path}
            fallbackUrl={duplicate.second.source_url}
            highlight={{ kind: 'duplicate', start: duplicate.second.start_line, end: duplicate.second.end_line, label: 'Duplicate B' }}
          />
        </div>
      )}
    </article>
  );
}

const SOURCE_WINDOW = 300;
type SourceHighlight = { kind: 'coverage' } | { kind: 'duplicate'; start: number; end: number; label: string };

function SourceViewer({ analysisId, authToken, path, fallbackUrl, highlight }: { analysisId: string; authToken?: string; path: string; fallbackUrl: string | null; highlight: SourceHighlight }) {
  const [windowStart, setWindowStart] = useState(1);
  const [activeUncovered, setActiveUncovered] = useState<number | null>(null);
  const source = useQuery({
    queryKey: ['code-quality', analysisId, 'source', path],
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ path });
      return apiFetch(`${API_BASE}/v1/insights/analyses/${encodeURIComponent(analysisId)}/source?${params}`, authToken, { signal }).then((response) => response.json() as Promise<SourceDocument>);
    },
    staleTime: Infinity,
    gcTime: 0,
  });
  const allLines = useMemo(() => (source.data ? (source.data.content.length === 0 ? [] : source.data.content.split('\n')) : []), [source.data]);
  const coverage = useMemo(() => new Map(source.data?.coverage.map((line) => [line.line, line.covered]) ?? []), [source.data]);
  const uncovered = useMemo(() => source.data?.coverage.filter((line) => !line.covered).map((line) => line.line) ?? [], [source.data]);

  useEffect(() => {
    if (!source.data) return;
    const focus = highlight.kind === 'coverage' ? (uncovered[0] ?? source.data.coverage[0]?.line ?? 1) : highlight.start;
    setActiveUncovered(highlight.kind === 'coverage' && uncovered.length > 0 ? focus : null);
    setWindowStart(Math.max(1, focus - 5));
  }, [allLines.length, highlight.kind, highlight.kind === 'duplicate' ? highlight.start : 0, source.data, uncovered]);

  if (source.isLoading)
    return (
      <div className="source-viewer-state" role="status">
        Loading source for <code>{path}</code>...
      </div>
    );
  if (source.error)
    return (
      <div className="source-viewer-state error" role="alert">
        <p>Source could not be loaded: {source.error.message}</p>
        <div className="source-viewer-error-actions">
          <Button type="button" onClick={() => void source.refetch()}>
            Retry
          </Button>
          {fallbackUrl && <SourceLink url={fallbackUrl} label="Open in Azure DevOps" />}
        </div>
      </div>
    );
  if (!source.data) return null;
  if (allLines.length === 0)
    return (
      <div className="source-viewer-state" role="status">
        The source file is empty.
      </div>
    );

  const contextEnd = highlight.kind === 'duplicate' ? Math.min(allLines.length, highlight.end + 5) : Math.min(allLines.length, windowStart + SOURCE_WINDOW - 1);
  const start = highlight.kind === 'duplicate' ? Math.max(1, highlight.start - 5) : windowStart;
  const displayed = allLines.slice(start - 1, contextEnd);
  // ponytail: linear lookup stays bounded by the ingestion's 250000 normalized-entry ceiling.
  const currentIndex = activeUncovered == null ? -1 : uncovered.indexOf(activeUncovered);
  const focusUncovered = (line: number) => {
    setActiveUncovered(line);
    setWindowStart(Math.max(1, line - 5));
  };
  return (
    <section className="source-viewer" aria-label={`Source for ${path}`}>
      <header className="source-viewer-header">
        <div>
          <strong>{highlight.kind === 'duplicate' ? highlight.label : 'Coverage source'}</strong>
          <code title={path}>{path}</code>
        </div>
        <SourceLink url={source.data.source_url} label="Open in Azure DevOps" />
      </header>
      {highlight.kind === 'coverage' && (
        <div className="source-viewer-toolbar">
          <div className="source-legend" aria-label="Coverage legend">
            <span className="covered">
              <b aria-hidden="true">✓</b> Covered
            </span>
            <span className="uncovered">
              <b aria-hidden="true">×</b> Uncovered
            </span>
            <span>
              <b aria-hidden="true">·</b> Not executable
            </span>
          </div>
          <div className="source-navigation" aria-label="Source navigation">
            <Button type="button" disabled={currentIndex <= 0} onClick={() => focusUncovered(uncovered[currentIndex - 1])}>
              Previous uncovered
            </Button>
            <Button type="button" disabled={currentIndex < 0 || currentIndex >= uncovered.length - 1} onClick={() => focusUncovered(uncovered[currentIndex + 1])}>
              Next uncovered
            </Button>
            <Button type="button" disabled={windowStart === 1} onClick={() => setWindowStart(Math.max(1, windowStart - SOURCE_WINDOW))}>
              Previous lines
            </Button>
            <Button type="button" disabled={contextEnd >= allLines.length} onClick={() => setWindowStart(windowStart + SOURCE_WINDOW)}>
              Next lines
            </Button>
          </div>
        </div>
      )}
      <p className="source-window-status" role="status">
        Lines {start}-{contextEnd} of {source.data.line_count} at commit <code>{source.data.commit_sha.slice(0, 12)}</code>
      </p>
      <div className="source-code" role="table" aria-label={`${highlight.kind === 'coverage' ? 'Coverage' : highlight.label} source lines`} tabIndex={0}>
        {displayed.map((text, offset) => {
          const line = start + offset;
          const covered = coverage.get(line);
          const duplicated = highlight.kind === 'duplicate' && line >= highlight.start && line <= highlight.end;
          const state = duplicated ? 'duplicate' : covered === true ? 'covered' : covered === false ? 'uncovered' : 'neutral';
          const status = duplicated ? highlight.label : covered === true ? 'Covered' : covered === false ? 'Uncovered' : 'Not executable';
          const active = highlight.kind === 'coverage' && activeUncovered === line;
          return (
            <div role="row" key={line} className={`source-line ${state}${active ? ' active' : ''}`}>
              <span role="cell" className="source-marker" title={status}>
                <span aria-hidden="true">{duplicated ? 'D' : covered === true ? '✓' : covered === false ? '×' : '·'}</span>
                <span className="sr-only">{status}</span>
              </span>
              <span role="rowheader" className="source-line-number">
                {line}
              </span>
              <code role="cell">{text || '\u00a0'}</code>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function SummaryValues({ values }: { values: Array<[string, string]> }) {
  return (
    <dl className="code-quality-summary">
      {values.map(([label, value]) => (
        <div key={label}>
          <dt>{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function UnavailableState({ metric, state, reason }: { metric: 'coverage' | 'duplication'; state: DetailState; reason: DetailReason }) {
  const label = metric === 'coverage' ? 'Coverage' : 'Duplications';
  const message =
    reason === 'details_purged'
      ? 'Native report details were removed by the retention policy.'
      : reason === 'direct_ingestion'
        ? 'This analysis was published directly and has no native report details.'
        : reason === 'report_not_measured' || state === 'not_measured'
          ? `No native ${metric === 'coverage' ? 'coverage' : 'duplication'} report was measured for this analysis.`
          : `${label} details are unavailable for this analysis.`;
  return (
    <div className="code-quality-state" role="status">
      <h3>{label} unavailable</h3>
      <p>{message}</p>
    </div>
  );
}

function DuplicateLocationView({ label, location }: { label: string; location: DuplicateLocation }) {
  return (
    <section>
      <span>{label}</span>
      <SourceLink url={location.source_url} label={location.path} />
      <code>
        Lines {location.start_line}-{location.end_line}
      </code>
    </section>
  );
}

function SourceLink({ url, label }: { url: string | null; label: string }) {
  const safe = evidenceLink(url);
  return safe ? (
    <a href={safe} target="_blank" rel="noreferrer" title={label}>
      {label}
    </a>
  ) : (
    <code>{label}</code>
  );
}

function ListError({ message, retry }: { message: string; retry: () => void }) {
  return (
    <div className="code-quality-state error" role="alert">
      <p>Details could not be loaded: {message}</p>
      <Button type="button" onClick={retry}>
        Retry
      </Button>
    </div>
  );
}

function NextButton({ label, pending, action }: { label: string; pending: boolean; action: () => void }) {
  return (
    <div className="action-row code-quality-next">
      <Button type="button" aria-label={label} disabled={pending} onClick={action}>
        {pending ? 'Loading...' : 'Next'}
      </Button>
    </div>
  );
}

function safeId(value: string) {
  return Array.from(value)
    .map((character) => (/[a-z0-9_-]/i.test(character) ? character : `-${character.codePointAt(0)?.toString(16) ?? ''}-`))
    .join('');
}
