import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import WorkItemSettings from './WorkItemSettings';
import { ToastProvider } from '@/context/ToastProvider';

const project = 'Up.France.CodeInsights';
const defaults = { project_key: project, work_item_type: 'Bug', area_path: null, iteration_path: null, auto_min_severity: null, auto_on_sla_breach: false, auto_max_per_run: 10, auto_since: null, version: 0, updated_by: '', updated_at: '2026-09-29T00:00:00Z' };

function stub(requests: Array<{ init?: RequestInit }>, availability: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ init });
      if (init?.method === 'PUT') return Response.json({ ...defaults, ...JSON.parse(String(init.body)), version: 1 });
      return Response.json({ settings: defaults, availability });
    }),
  );
}

function renderSettings() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <WorkItemSettings authToken="session" projects={[project]} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('WorkItemSettings', () => {
  it('saves fields and rules, sending empty paths as null', async () => {
    const requests: Array<{ init?: RequestInit }> = [];
    stub(requests, { enabled: true, reason: null, organization_url: 'https://dev.azure.com/up-group', ado_project: 'Quality' });
    const user = userEvent.setup();
    renderSettings();
    expect(await screen.findByText(/Work items go to/)).toHaveTextContent('Quality');
    await user.selectOptions(screen.getByRole('combobox', { name: 'New findings with severity' }), 'high');
    await user.click(screen.getByRole('checkbox', { name: /breach their SLA/ }));
    await user.click(screen.getByRole('button', { name: 'Save work item settings' }));
    await waitFor(() => expect(requests.some((request) => request.init?.method === 'PUT')).toBe(true));
    const body = JSON.parse(String(requests.find((request) => request.init?.method === 'PUT')?.init?.body));
    expect(body).toEqual({ version: 0, work_item_type: 'Bug', area_path: null, iteration_path: null, auto_min_severity: 'high', auto_on_sla_breach: true, auto_max_per_run: 10 });
  });

  it('explains why a project cannot receive work items and blocks an invalid run size', async () => {
    stub([], { enabled: false, reason: 'The project has no Azure DevOps repository link', organization_url: null, ado_project: null });
    const user = userEvent.setup();
    renderSettings();
    expect(await screen.findByText(/unavailable for this project: The project has no Azure DevOps repository link/)).toBeInTheDocument();
    const max = screen.getByRole('textbox', { name: /At most per run/ });
    await user.clear(max);
    await user.type(max, '0');
    expect(screen.getByRole('button', { name: 'Save work item settings' })).toBeDisabled();
  });
});
