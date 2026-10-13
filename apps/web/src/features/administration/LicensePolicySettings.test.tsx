import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LicensePolicySettings from './LicensePolicySettings';
import { ToastProvider } from '@/context/ToastProvider';

const policy = { allowed_licenses: ['MIT', 'Apache-2.0'], denied_licenses: ['GPL-3.0-only'], version: 1, updated_by: 'migration', updated_at: '2026-09-29T00:00:00Z' };
const inventory = {
  projects: 2,
  components: 10,
  unlicensed_components: 3,
  unlicensed_projects: 1,
  licenses: [{ license: 'MIT OR Apache-2.0', components: 4, projects: 2 }],
  truncated: false,
};

function stub(requests: Array<{ url: string; init?: RequestInit }>) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      requests.push({ url, init });
      if (init?.method === 'PUT') return Response.json({ ...policy, ...JSON.parse(String(init.body)), version: 2 });
      return Response.json(url.endsWith('/unclassified') ? inventory : policy);
    }),
  );
}

function renderSettings() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <LicensePolicySettings authToken="session" />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} }));
afterEach(() => vi.unstubAllGlobals());

describe('LicensePolicySettings', () => {
  it('allows an unclassified licence from the inventory and saves both lists', async () => {
    const requests: Array<{ url: string; init?: RequestInit }> = [];
    stub(requests);
    const user = userEvent.setup();
    renderSettings();
    const inventoryTable = await screen.findByRole('region', { name: 'Unclassified licence inventory' });
    await user.click(within(inventoryTable).getByRole('button', { name: 'Allow' }));
    expect(within(inventoryTable).getByText('In the draft')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Allowed licences \(3\)/ })).toHaveValue('MIT\nApache-2.0\nMIT OR Apache-2.0');
    await user.click(screen.getByRole('button', { name: 'Save licence policy' }));
    await waitFor(() => expect(requests.some((request) => request.init?.method === 'PUT')).toBe(true));
    expect(JSON.parse(String(requests.find((request) => request.init?.method === 'PUT')?.init?.body))).toEqual({
      version: 1,
      allowed_licenses: ['MIT', 'Apache-2.0', 'MIT OR Apache-2.0'],
      denied_licenses: ['GPL-3.0-only'],
    });
  });

  it('blocks a licence listed on both sides and explains components without a licence', async () => {
    stub([]);
    const user = userEvent.setup();
    renderSettings();
    expect(await screen.findByText(/3 component\(s\) in 1 project\(s\) declare no licence/)).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Denied licences/ }), '\nMIT');
    expect(screen.getByRole('alert')).toHaveTextContent('Listed as both allowed and denied: MIT');
    expect(screen.getByRole('button', { name: 'Save licence policy' })).toBeDisabled();
  });
});
