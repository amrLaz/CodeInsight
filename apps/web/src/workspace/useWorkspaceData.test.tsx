import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useWorkspaceData } from './useWorkspaceData';

const api = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiJson: vi.fn(),
  apiPage: vi.fn(),
}));

vi.mock('../services/api', () => api);

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return function TestQueryClient({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

describe('useWorkspaceData request ownership', () => {
  beforeEach(() => {
    api.apiFetch.mockReset();
    api.apiJson
      .mockReset()
      .mockImplementation((url: string) =>
        Promise.resolve(url.endsWith('/v1/auth/me') ? { auth_provider: 'local', permissions: { is_admin: true } } : url.includes('/summary') ? { projects: [] } : []),
      );
    api.apiPage.mockReset().mockResolvedValue({ items: [], next: null });
  });

  it('does not fetch dashboard data on an unrelated administration screen', async () => {
    renderHook(() => useWorkspaceData('token', 'administration', 'general-settings', 'project', 'main'), { wrapper: wrapper() });

    await waitFor(() => expect(api.apiJson).toHaveBeenCalledTimes(1));
    expect(api.apiJson.mock.calls[0][0]).toContain('/v1/auth/me');
    expect(api.apiPage).not.toHaveBeenCalled();
  });

  it.each(['new-code-settings', 'sla-settings', 'work-item-settings'] as const)('loads the project list for the %s picker', async (section) => {
    renderHook(() => useWorkspaceData('token', 'administration', section), { wrapper: wrapper() });

    await waitFor(() => expect(api.apiJson.mock.calls.some(([url]) => String(url).includes('/v1/insights/summary'))).toBe(true));
  });

  it('fetches project data on the project workspace without the redundant default gate request', async () => {
    renderHook(() => useWorkspaceData('token', 'projects', 'general-settings', 'project', 'main'), { wrapper: wrapper() });

    await waitFor(() => expect(api.apiPage).toHaveBeenCalled());
    const urls = [...api.apiJson.mock.calls, ...api.apiPage.mock.calls].map(([url]) => String(url));
    expect(urls.some((url) => url.includes('/v1/insights/summary'))).toBe(true);
    expect(urls.some((url) => url.includes('/v1/integrations/projects'))).toBe(true);
    expect(urls.some((url) => url.includes('/v1/gates/default'))).toBe(false);
  });

  it('reloads the bounded branch catalogue when the selected project changes', async () => {
    const { rerender } = renderHook(({ project }) => useWorkspaceData('token', 'projects', 'general-settings', project, 'main'), {
      initialProps: { project: 'Up.France.CodeInsights' },
      wrapper: wrapper(),
    });

    await waitFor(() => expect(api.apiPage.mock.calls.some(([url]) => String(url).includes('/v1/insights/branches?') && String(url).includes('project_key=Up.France.CodeInsights'))).toBe(true));
    api.apiPage.mockClear();
    rerender({ project: 'Up.France.StoreLocator' });

    await waitFor(() => expect(api.apiPage.mock.calls.some(([url]) => String(url).includes('/v1/insights/branches?') && String(url).includes('project_key=Up.France.StoreLocator'))).toBe(true));
  });

  it('reads branch builds for a branch and pull request builds for a pull request', async () => {
    const { rerender } = renderHook(({ branch }) => useWorkspaceData('token', 'projects', 'general-settings', 'project', branch), {
      initialProps: { branch: 'refs/heads/develop' },
      wrapper: wrapper(),
    });
    const urls = () => api.apiPage.mock.calls.map(([url]) => new URL(String(url), 'http://local'));

    await waitFor(() => expect(urls().some((url) => url.pathname.endsWith('/v1/insights/analyses'))).toBe(true));
    const branchAnalyses = urls().find((url) => url.pathname.endsWith('/v1/insights/analyses'))!;
    expect([branchAnalyses.searchParams.get('kind'), branchAnalyses.searchParams.get('branch')]).toEqual(['branch', 'refs/heads/develop']);
    expect(urls().some((url) => url.pathname.endsWith('/v1/insights/pull-requests') && !url.searchParams.has('pr_id'))).toBe(true);

    api.apiPage.mockClear();
    rerender({ branch: 'pr:51382' });

    await waitFor(() => expect(urls().some((url) => url.pathname.endsWith('/v1/insights/analyses'))).toBe(true));
    const pullRequestAnalyses = urls().find((url) => url.pathname.endsWith('/v1/insights/analyses'))!;
    expect([pullRequestAnalyses.searchParams.get('kind'), pullRequestAnalyses.searchParams.get('pr_id'), pullRequestAnalyses.searchParams.has('branch')]).toEqual(['pull_request', '51382', false]);
    expect(urls().some((url) => url.pathname.endsWith('/v1/insights/pull-requests') && url.searchParams.get('pr_id') === '51382')).toBe(true);
    // The selection key is not a branch name: it is never sent as one.
    expect(urls().some((url) => url.pathname.endsWith('/v1/insights/branches') && url.searchParams.get('branch') === 'pr:51382')).toBe(false);
  });

  it('asks for main and master by name only when the catalogue has other pages, before any default is chosen', async () => {
    const preferred = () =>
      api.apiPage.mock.calls
        .map(([url]) => new URL(String(url), 'http://local'))
        .filter((url) => url.pathname.endsWith('/v1/insights/branches') && url.searchParams.has('branch'))
        .map((url) => url.searchParams.get('branch'));
    const load = () => useWorkspaceData('token', 'projects', 'general-settings', 'project', '');
    const single = renderHook(load, { wrapper: wrapper() });

    await waitFor(() => expect(single.result.current.branchesQuery.isSuccess).toBe(true));
    expect(preferred()).toEqual([]);
    single.unmount();

    api.apiPage.mockImplementation((url: string) =>
      Promise.resolve({
        items: url.includes('branch=master') ? [{ name: 'refs/heads/master', count: 91 }] : [],
        next: url.includes('/v1/insights/branches?') && !url.includes('branch=') ? 'cursor' : null,
      }),
    );
    // The default branch is chosen as soon as the page has loaded: the preferred branches must already be pending then.
    const undecided: boolean[] = [];
    const paged = renderHook(
      () => {
        const data = load();
        if (data.branchesQuery.data?.next) undecided.push(!data.preferredBranchesQuery.isLoading && !data.preferredBranchesQuery.isSuccess && !data.preferredBranchesQuery.isError);
        return data;
      },
      { wrapper: wrapper() },
    );

    await waitFor(() => expect(paged.result.current.preferredBranchesQuery.isSuccess).toBe(true));
    expect(preferred()).toEqual(['main', 'master']);
    expect(paged.result.current.preferredBranchesQuery.data?.map((branch) => branch.name)).toEqual(['refs/heads/master']);
    expect(undecided.length).toBeGreaterThan(0);
    expect(undecided).not.toContain(true);
  });

  it('searches the server for branches only when the catalogue has other pages', async () => {
    const searches = () =>
      api.apiPage.mock.calls.map(([url]) => new URL(String(url), 'http://local')).filter((url) => url.pathname.endsWith('/v1/insights/branches') && url.searchParams.has('search'));
    const search = () => useWorkspaceData('token', 'projects', 'general-settings', 'project', '', '', '', '', 'Master');
    const single = renderHook(search, { wrapper: wrapper() });

    // A single page is already filtered by the picker itself.
    await waitFor(() => expect(single.result.current.branchesQuery.isSuccess).toBe(true));
    expect(searches()).toEqual([]);
    single.unmount();

    api.apiPage.mockImplementation((url: string) => Promise.resolve({ items: [], next: url.includes('/v1/insights/branches?') && !url.includes('search=') ? 'cursor' : null }));
    renderHook(search, { wrapper: wrapper() });

    await waitFor(() => expect(searches()).toHaveLength(1));
    expect([searches()[0].searchParams.get('project_key'), searches()[0].searchParams.get('search'), searches()[0].searchParams.get('limit')]).toEqual(['project', 'Master', '100']);
  });
});
