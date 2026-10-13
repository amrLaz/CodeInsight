import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NewCode } from './NewCodePanel';

const base = '5458a5ac9a789dca9d3c239261b5f4512ea78b06';
const head = 'd39334757807d49332362f12c7d3305d982e8a12';

function stub(body: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => Response.json(body)),
  );
}

function renderNewCode(props: { headBranch?: string; pullRequestId?: string | null } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NewCode analysisId="a1" token="session" repoProvider="azure-devops" repoUrl="https://dev.azure.com/org/project/_git/repo" {...props} />
    </QueryClientProvider>,
  );
}

function definitionRows() {
  const list = document.querySelector('.comparison-definition') as HTMLElement;
  return within(list)
    .getAllByRole('term')
    .map((term) => [term.textContent, term.nextElementSibling?.textContent]);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('NewCode', () => {
  it('explains a pull request comparison as the changes the branch brings to its target', async () => {
    stub({
      state: 'available',
      baseline_analysis_id: '053bccdb-90f4-48ec-bef0-66d085c201bd',
      baseline_branch: 'refs/heads/master',
      baseline_date: '2026-09-30T11:06:30Z',
      base_sha: base,
      head_sha: head,
      effective_definition: { mode: 'reference_branch', reference_branch: 'refs/heads/master' },
      effective_source: 'pull_request_target',
    });
    renderNewCode({ headBranch: 'develop', pullRequestId: '51382' });

    const summary = await screen.findByText(/not yet in/);
    expect(summary.textContent).toBe('Changes in develop not yet in master, the scope of pull request #51382.');
    expect(screen.getByText(/It may lag behind the live branch/)).toBeTruthy();
    const range = document.querySelector('.comparison-range') as HTMLElement;
    expect(range.textContent).toContain('master 5458a5a');
    expect(range.textContent).toContain('develop d393347');
    expect(within(range).getByTitle(base)).toBeTruthy();
    expect(definitionRows()).toEqual([
      ['Definition', 'Reference branch'],
      ['Why this baseline', 'Forced by pull request #51382'],
      ['Reference branch', 'master'],
      ['Baseline date', new Date('2026-09-30T11:06:30Z').toLocaleString()],
    ]);
  });

  it('keeps the baseline branch visible when it differs from the configured reference', async () => {
    stub({
      state: 'available',
      baseline_branch: 'refs/heads/main',
      base_sha: base,
      head_sha: head,
      effective_definition: { mode: 'reference_branch', reference_branch: 'refs/heads/master' },
      effective_source: 'project',
    });
    renderNewCode({ headBranch: 'refs/heads/feature/x' });

    expect((await screen.findByText(/compared with/)).textContent).toBe('Changes in feature/x compared with main.');
    expect(definitionRows()).toEqual([
      ['Definition', 'Reference branch'],
      ['Why this baseline', 'Project setting'],
      ['Reference branch', 'master'],
      ['Baseline branch', 'main'],
      ['Baseline date', 'Not available'],
    ]);
  });

  it('shows the baseline version only for the previous version mode and keeps its absence explicit', async () => {
    stub({
      state: 'available',
      baseline_branch: 'develop',
      base_sha: base,
      head_sha: head,
      effective_definition: { mode: 'previous_version' },
      effective_source: 'default',
    });
    renderNewCode({ headBranch: 'develop' });

    expect((await screen.findByText(/previous project version/)).textContent).toBe('Changes in develop since the latest analysis of the previous project version Not available.');
    expect(screen.queryByText(/It may lag behind/)).toBeNull();
    expect(definitionRows()).toEqual([
      ['Definition', 'Previous version'],
      ['Why this baseline', 'Default setting'],
      ['Baseline version', 'Not available'],
      ['Baseline date', 'Not available'],
    ]);
  });
});
