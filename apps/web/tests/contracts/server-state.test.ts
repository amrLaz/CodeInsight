import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { InfiniteQueryObserver } from '@tanstack/react-query';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('server state contracts', async () => {
async function load(name) {
  const { outputText } = ts.transpileModule(readFileSync(new URL(name, sourceUrl), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 },
  });
  return import(`data:text/javascript;base64,${Buffer.from(outputText.replace("'@tanstack/react-query'", JSON.stringify(import.meta.resolve('@tanstack/react-query')))).toString('base64')}`);
}
const { apiFetch, apiJson, apiPage, sessionEvents } = await load('./services/api.ts');
const { createSessionQueryClient } = await load('./services/query-client.ts');
const originalFetch = globalThis.fetch;
const clients = [];
try {
  const expiredTokens = [];
  sessionEvents.addEventListener('expired', (event) => expiredTokens.push(event.detail));
  for (const status of [401, 403, 422, 500]) {
    let success = false;
    globalThis.fetch = async () => new Response(JSON.stringify({ detail: 'Refused' }), { status });
    await assert.rejects(
      apiFetch('/write', 'test-only', { method: 'DELETE' }).then(() => {
        success = true;
      }),
      /Refused/,
    );
    assert.equal(success, false);
  }
  assert.deepEqual(expiredTokens, ['test-only'], 'Only authenticated 401 responses expire a session');
  globalThis.fetch = async () => new Response('not JSON', { status: 401 });
  await assert.rejects(apiFetch('/private', 'expired-token'), /401/);
  assert.deepEqual(expiredTokens, ['test-only', 'expired-token'], 'Non-JSON 401 must also expire a session');
  await assert.rejects(apiFetch('/login'), /401/);
  assert.equal(expiredTokens.length, 2, 'Anonymous failures must not clear a session');
  globalThis.fetch = async () => {
    throw new TypeError('Network error');
  };
  await assert.rejects(apiFetch('/private', 'valid-token'), /Network error/);
  assert.equal(expiredTokens.length, 2, 'Network failures must preserve the session');
  globalThis.fetch = async () => new Response('bad gateway', { status: 502 });
  await assert.rejects(apiFetch('/write'), /502/);
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers.get('Authorization'), 'Bearer test-only');
    assert.equal(init.headers.get('X-Request'), 'preserved');
    return Response.json({ count: 0 });
  };
  assert.deepEqual(await apiJson('/read', 'test-only', { headers: { 'X-Request': 'preserved' } }), { count: 0 });

  globalThis.fetch = async () => new Response('[]', { headers: { 'X-Next-Cursor': 'next-page' } });
  assert.deepEqual(await apiPage('/read'), { items: [], next: 'next-page' });
  globalThis.fetch = async () => Response.json([]);
  assert.deepEqual(await apiPage('/read'), { items: [], next: null });

  const a = createSessionQueryClient(),
    b = createSessionQueryClient();
  clients.push(a, b);
  let release;
  let signal;
  const oldRead = a.fetchQuery({
    queryKey: ['analyses'],
    queryFn: (context) => {
      signal = context.signal;
      return new Promise((resolve) => {
        release = resolve;
      });
    },
  });
  const cancelled = assert.rejects(oldRead);
  await a.cancelQueries();
  await cancelled;
  assert.equal(signal.aborted, true);
  release(['account A']);
  await b.fetchQuery({ queryKey: ['analyses'], queryFn: async () => ['account B'] });
  assert.deepEqual(b.getQueryData(['analyses']), ['account B']);
  assert.equal(a.getQueryData(['analyses']), undefined);

  const observer = new InfiniteQueryObserver(b, {
    queryKey: ['audit', { actor: 'A' }],
    initialPageParam: 0,
    queryFn: async ({ pageParam }) => ({ entries: [`A-${pageParam}`] }),
    getNextPageParam: (_page, pages) => pages.length,
  });
  const unsubscribe = observer.subscribe(() => {});
  await observer.refetch();
  await observer.fetchNextPage();
  observer.setOptions({
    queryKey: ['audit', { actor: 'B' }],
    initialPageParam: 0,
    queryFn: async () => ({ entries: ['B-0'] }),
    getNextPageParam: () => undefined,
  });
  await observer.refetch();
  assert.deepEqual(observer.getCurrentResult().data.pages, [{ entries: ['B-0'] }]);
  unsubscribe();
  assert.equal(b.getDefaultOptions().queries.retry, false);
  assert.equal(b.getDefaultOptions().queries.refetchOnWindowFocus, false);
  console.log('HTTP, session cancellation and paginated cache checks passed.');
} finally {
  globalThis.fetch = originalFetch;
  clients.forEach((client) => client.clear());
}
});
