import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PortfolioTrends from './PortfolioTrends';

const ratings = { a: 1, b: 1, c: 0, d: 0, e: 0, not_rated: 0 };
const days = ['2026-09-28', '2026-09-29'].map((day) => ({
  day,
  projects: 2,
  ratings: { security: ratings, reliability: ratings, maintainability: ratings, security_review: ratings },
  findings: { projects: 2, blocker: 1, high: 2, medium: 0, low: 0, info: 0, unknown: 0 },
  sla: null,
  coverage_projects: 1,
  duplication_projects: 1,
}));
const project = (key: string, coverage: number | null, start: number | null) => ({
  project_key: key,
  day: '2026-09-29',
  analysis_id: key + '-a',
  source: 'live',
  security_rating: 'A',
  reliability_rating: 'A',
  maintainability_rating: 'A',
  security_review_rating: null,
  coverage,
  coverage_start: start,
  duplication: 3,
  duplication_start: 3,
  start_day: '2026-09-28',
  blocker_findings: 1,
  high_findings: 1,
  sla_breached: null,
});

function stub(body: unknown) {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return Response.json(body);
    }),
  );
  return urls;
}

function renderTrends() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PortfolioTrends authToken="session" />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  // jsdom has no ResizeObserver; charts and grids only use it to measure themselves.
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} });
});
afterEach(() => vi.unstubAllGlobals());

describe('PortfolioTrends', () => {
  it('explains that trends need a first snapshot', async () => {
    stub({ from: '2026-07-02', to: '2026-09-29', projects_without_main_branch: 1, days: [], projects: [] });
    renderTrends();
    expect(await screen.findByText(/No daily snapshot yet/)).toBeInTheDocument();
    expect(screen.getByText('1 project(s) without a main branch are not included.')).toBeInTheDocument();
  });

  it('requests the chosen period and says when SLA was never observed', async () => {
    const urls = stub({ from: '2026-09-23', to: '2026-09-29', projects_without_main_branch: 0, days, projects: [] });
    const user = userEvent.setup();
    renderTrends();
    await user.click(await screen.findByRole('tab', { name: 'SLA' }));
    expect(screen.getByText(/SLA counts are recorded from the day this feature runs/)).toBeInTheDocument();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Period' }), '30');
    expect(urls[urls.length - 1]).toMatch(/portfolio\?days=30$/);
  });

  it('lists the lowest coverage first and never invents a change for an unmeasured start', async () => {
    stub({ from: '2026-09-23', to: '2026-09-29', projects_without_main_branch: 0, days, projects: [project('beta', 82, 70), project('alpha', 40, null), project('gamma', null, null)] });
    const user = userEvent.setup();
    renderTrends();
    await user.click(await screen.findByRole('tab', { name: 'Coverage & duplication' }));
    const rows = within(screen.getByRole('region', { name: 'Coverage and duplication per project' })).getAllByRole('row').slice(1);
    expect(rows.map((row) => within(row).getByRole('rowheader').textContent)).toEqual(['alpha', 'beta', 'gamma']);
    expect(rows[0]).toHaveTextContent('40%');
    expect(rows[1]).toHaveTextContent('+12 pts');
    expect(rows[2]).toHaveTextContent('Not measured');
  });
});
