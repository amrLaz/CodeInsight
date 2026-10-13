import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AnalysisRecord, ProjectSummary } from '@/types/api';
import Projects from './Projects';

const pullRequestBuild = {
  analysis_id: 'pr-build',
  project_key: 'app',
  commit_sha: 'd39334757807d49332362f12c7d3305d982e8a12',
  branch: 'refs/heads/develop',
  pr_id: '51382',
  pr_target_branch: 'refs/heads/master',
  status: 'completed',
  quality_gate: 'fail',
  completeness: 'complete',
  metrics: {},
  gate_failures: [],
  created_at: '2026-10-01T10:27:00Z',
} as unknown as AnalysisRecord;

const olderBuild = { ...pullRequestBuild, analysis_id: 'older-pr', pr_id: '9', quality_gate: 'pass', commit_sha: 'aaaaaaa1111111', created_at: '2026-09-20T08:00:00Z' } as AnalysisRecord;

const summary: ProjectSummary = {
  projectKey: 'app',
  isFavorite: false,
  latest: pullRequestBuild,
  history: [pullRequestBuild],
  passCount: 0,
  failCount: 0,
  unknownCount: 0,
  issueCounts: {},
  branches: [
    { name: 'refs/heads/develop', count: 0, pull_request_count: 2, latest: null },
    // Listed out of order on purpose: the view sorts by pull request number.
    { name: 'pr:9', count: 3, latest: olderBuild, pull_request: { id: '9', source_branch: 'refs/heads/develop', target_branch: 'refs/heads/master' } },
    { name: 'pr:51382', count: 1, latest: pullRequestBuild, pull_request: { id: '51382', source_branch: 'refs/heads/develop', target_branch: 'refs/heads/master' } },
  ],
};

function renderProjects(props: Partial<ComponentProps<typeof Projects>>) {
  return render(
    <Projects
      summaries={[summary]}
      selectedSummary={summary}
      selectedProject="app"
      selectedBranch="refs/heads/develop"
      selectedAnalysisId=""
      favoritePending={false}
      branchLoading={false}
      setSelectedProject={vi.fn()}
      setSelectedBranch={vi.fn()}
      onToggleFavorite={vi.fn()}
      loading={false}
      projectMeta={{}}
      onRefresh={vi.fn()}
      scopeMode="new-code"
      onScopeChange={vi.fn()}
      newCodePanel={<p>New Code panel</p>}
      onOpenNewCode={vi.fn()}
      onOpenIssues={vi.fn()}
      onOpenCodeQuality={vi.fn()}
      {...props}
    />,
  );
}

beforeEach(() => {
  // jsdom has no ResizeObserver; the grid only uses it to detect truncated cells.
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} });
});

describe('Projects with pull request builds', () => {
  it('shows an empty state for a branch built only through pull requests instead of borrowing their verdict', async () => {
    const user = userEvent.setup();
    const setSelectedBranch = vi.fn();
    renderProjects({ setSelectedBranch });

    expect(screen.getByRole('heading', { name: 'No branch analysis yet' })).toBeInTheDocument();
    expect(screen.queryByText(/Quality Gate:/)).not.toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Pull requests from develop, newest first' });
    const [, newest, older] = within(table).getAllByRole('row');
    // Newest first, with the verdict, route, commit and number of analyses of each pull request.
    expect(within(newest).getByRole('rowheader')).toHaveTextContent('#51382');
    expect(newest).toHaveTextContent('develop → master');
    expect(newest).toHaveTextContent('Fail');
    expect(newest).toHaveTextContent('d393347');
    expect(within(older).getByRole('rowheader')).toHaveTextContent('#9');
    expect(older).toHaveTextContent('Pass');
    expect(older).toHaveTextContent('3');
    await user.click(screen.getByRole('button', { name: 'Open pull request #51382 in CodeInsights' }));
    expect(setSelectedBranch).toHaveBeenCalledWith('pr:51382');
  });

  it('names a selected pull request and its route instead of its source branch', () => {
    renderProjects({ selectedBranch: 'pr:51382', selectedAnalysisId: 'pr-build' });

    expect(screen.getByRole('heading', { name: /Metrics for pull request #51382/ })).toHaveTextContent('develop → master');
    expect(screen.queryByRole('heading', { name: 'No branch analysis yet' })).not.toBeInTheDocument();
    expect(screen.getByText('New Code panel')).toBeInTheDocument();
  });
});
