import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import SlaSettings from './SlaSettings';
import { ToastProvider } from '@/context/ToastProvider';

const project = 'Up.France.CodeInsights';
const global = { scope_key: 'global', project_key: null, blocker_days: 7, high_days: 30, medium_days: 90, low_days: 180, info_days: null, version: 1, updated_by: 'migration', updated_at: '2026-09-29T00:00:00Z' };
const counts = { breached: 2, due_soon: 1, on_track: 5, suspended: 0, closed: 3, not_applicable: 4, not_configured: 0, indeterminate: 1, pending: 0 };

function stubApi(requests: Array<{ url: string; init?: RequestInit }>, override: unknown = null) {
  // jsdom has no ResizeObserver; Grid only uses it to detect truncated cells.
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
      unobserve() {}
    },
  );
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      requests.push({ url, init });
      if (init?.method === 'DELETE') return new Response(null, { status: 204 });
      if (init?.method === 'PUT') return Response.json({ ...global, ...JSON.parse(String(init.body)), version: 2 });
      if (url.endsWith('/sla/policy')) return Response.json(global);
      return Response.json({ project_key: project, main_branch: 'refs/heads/main', override, effective: override ?? global, counts });
    }),
  );
}

function renderSettings() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <SlaSettings authToken="session" projects={[project]} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SlaSettings', () => {
  it('sends an emptied delay as no deadline and rejects values out of bounds', async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    stubApi(requests);
    const user = userEvent.setup();
    renderSettings();
    const region = await screen.findByRole('region', { name: 'Global policy' });
    const blocker = await within(region).findByRole('textbox', { name: 'Blocker (days)' });
    expect(blocker).toHaveValue('7');
    expect(within(region).getByRole('textbox', { name: 'Info (days)' })).toHaveValue('');

    await user.clear(blocker);
    await user.type(blocker, '0');
    expect(within(region).getByRole('button', { name: 'Save global policy' })).toBeDisabled();
    await user.clear(blocker);
    await user.click(within(region).getByRole('button', { name: 'Save global policy' }));

    await waitFor(() => expect(requests.some((request) => request.init?.method === 'PUT')).toBe(true));
    const saved = requests.find((request) => request.init?.method === 'PUT');
    expect(saved?.url).toMatch(/\/v1\/sla\/policy$/);
    expect(JSON.parse(String(saved?.init?.body))).toEqual({ version: 1, blocker_days: null, high_days: 30, medium_days: 90, low_days: 180, info_days: null });
  });

  it('creates a project override from the effective values and shows counts per status', async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    stubApi(requests);
    const user = userEvent.setup();
    renderSettings();
    const region = await screen.findByRole('region', { name: 'Project override' });
    expect(await within(region).findByText(/follows the global policy/)).toBeInTheDocument();
    expect(within(region).getByRole('rowheader', { name: 'Breached' }).closest('tr')).toHaveTextContent('2');
    expect(within(region).queryByRole('button', { name: 'Remove override' })).not.toBeInTheDocument();

    const high = within(region).getByRole('textbox', { name: 'High (days)' });
    expect(high).toHaveValue('30');
    await user.clear(high);
    await user.type(high, '14');
    await user.click(within(region).getByRole('button', { name: 'Create override' }));

    await waitFor(() => expect(requests.some((request) => request.init?.method === 'PUT')).toBe(true));
    const saved = requests.find((request) => request.init?.method === 'PUT');
    expect(saved?.url).toContain(`/v1/sla/projects/${encodeURIComponent(project)}`);
    expect(JSON.parse(String(saved?.init?.body))).toMatchObject({ version: 0, high_days: 14, blocker_days: 7 });
  });

  it('removes an existing override after confirmation', async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    stubApi(requests, { ...global, scope_key: `project:${project}`, project_key: project, high_days: 14, version: 3 });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const user = userEvent.setup();
    renderSettings();
    const region = await screen.findByRole('region', { name: 'Project override' });
    await user.click(await within(region).findByRole('button', { name: 'Remove override' }));
    await waitFor(() => expect(requests.some((request) => request.init?.method === 'DELETE')).toBe(true));
    expect(within(region).getByRole('textbox', { name: 'High (days)' })).toHaveValue('14');
  });
});
