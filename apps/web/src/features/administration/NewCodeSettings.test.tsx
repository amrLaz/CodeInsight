import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import NewCodeSettings from './NewCodeSettings';
import { ToastProvider } from '@/context/ToastProvider';

const project = 'Up.France.CodeInsights';

function renderSettings() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <NewCodeSettings authToken="session" projects={[project]} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('NewCodeSettings', () => {
  it('filters modes by level and publishes a canonical branch scope', async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        requests.push({ url, init });
        if (url.endsWith('/definitions') && (!init?.method || init.method === 'GET')) return Response.json([]);
        if (url.includes(`/projects/${encodeURIComponent(project)}`) && (!init?.method || init.method === 'GET')) {
          return Response.json({ project_key: project, main_branch: 'refs/heads/main', definitions: [] });
        }
        return Response.json({ scope_key: 'saved' });
      }),
    );
    const user = userEvent.setup();
    renderSettings();

    const level = screen.getByRole('combobox', { name: 'Level' });
    const mode = screen.getByRole('combobox', { name: 'Mode' });
    expect(within(mode).queryByRole('option', { name: 'Reference branch' })).not.toBeInTheDocument();
    expect(within(mode).queryByRole('option', { name: 'Specific analysis' })).not.toBeInTheDocument();

    await user.selectOptions(level, 'project');
    expect(within(mode).getByRole('option', { name: 'Reference branch' })).toBeInTheDocument();
    expect(within(mode).queryByRole('option', { name: 'Specific analysis' })).not.toBeInTheDocument();

    await user.selectOptions(level, 'branch');
    expect(within(mode).getByRole('option', { name: 'Specific analysis' })).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: 'Branch' }), 'feature/new-code');
    await user.selectOptions(mode, 'reference_branch');
    await user.type(screen.getByRole('textbox', { name: 'Reference branch' }), 'main');
    await user.click(screen.getByRole('button', { name: 'Save definition' }));

    await waitFor(() => expect(requests.some((request) => request.init?.method === 'PUT')).toBe(true));
    const saved = requests.find((request) => request.init?.method === 'PUT');
    expect(saved?.url).toContain(encodeURIComponent(`branch:${project}:refs/heads/feature/new-code`));
    expect(JSON.parse(String(saved?.init?.body))).toMatchObject({
      level: 'branch',
      project_key: project,
      branch: 'refs/heads/feature/new-code',
      mode: 'reference_branch',
      reference_branch: 'refs/heads/main',
    });
  });

  it('constrains Number of days to 1 through 90 before submitting', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        return url.endsWith('/definitions') ? Response.json([]) : Response.json({ project_key: project, main_branch: null, definitions: [] });
      }),
    );
    const user = userEvent.setup();
    renderSettings();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Mode' }), 'number_of_days');
    const days = screen.getByRole('spinbutton', { name: 'Number of days' });
    await user.clear(days);
    await user.type(days, '91');
    expect(screen.getByRole('button', { name: 'Save definition' })).toBeDisabled();
    await user.clear(days);
    await user.type(days, '90');
    expect(screen.getByRole('button', { name: 'Save definition' })).toBeEnabled();
  });
});
