import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IssueWorkItem } from './IssueWorkItem';

const created = { finding_id: 'f1', state: 'created', work_item_id: 42, url: 'https://dev.azure.com/up-group/Quality/_workitems/edit/42', origin: 'severity_rule', requested_by: 'rule', requested_at: '2026-09-29T10:00:00Z', created_at: '2026-09-29T10:00:05Z', last_error: null };

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <IssueWorkItem findingId="f1" authToken="session" />
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('IssueWorkItem', () => {
  it('offers creation when the finding has no work item, then shows the queued request', async () => {
    const methods: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        methods.push(init?.method ?? 'GET');
        return init?.method === 'POST' ? Response.json({ ...created, state: 'pending', work_item_id: null, url: null, origin: 'manual' }, { status: 202 }) : Response.json(null);
      }),
    );
    const user = userEvent.setup();
    renderPanel();
    await user.click(await screen.findByRole('button', { name: 'Create work item' }));
    expect(await screen.findByText('Creation queued, requested manually.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /work item/ })).not.toBeInTheDocument();
    expect(methods).toEqual(['GET', 'POST']);
  });

  it('links a created work item and offers no second creation', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(created)));
    renderPanel();
    const link = await screen.findByRole('link', { name: /Work item #42, created by the severity rule/ });
    expect(link).toHaveAttribute('href', created.url);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('explains a refusal and allows a retry', async () => {
    const methods: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        methods.push(init?.method ?? 'GET');
        return Response.json({ ...created, state: 'failed', work_item_id: null, url: null, last_error: 'Azure DevOps refused to create a Bug work item (HTTP 403).' });
      }),
    );
    const user = userEvent.setup();
    renderPanel();
    expect(await screen.findByText(/Creation failed: Azure DevOps refused/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry work item' }));
    await waitFor(() => expect(methods).toContain('POST'));
  });
});
