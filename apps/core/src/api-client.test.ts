import { test } from 'vitest';
import assert from 'node:assert/strict';

test('api-client', async () => {

const { ApiError, CodeInsightsApi, problemDetail, resolveUrl } = await import('./api/client');

assert.equal(resolveUrl('https://hub.example', '/api/v1/analyses'), 'https://hub.example/api/v1/analyses');
assert.equal(resolveUrl('https://hub.example/', 'api/v1/analyses'), 'https://hub.example/api/v1/analyses');
assert.throws(() => resolveUrl('  ', '/api/v1/analyses'), /not configured/);

const problem = new Response(JSON.stringify({ detail: 'Project not found' }), { status: 404 });
assert.equal(await problemDetail(problem), 'Project not found');
assert.equal(await problemDetail(new Response('not json', { status: 500 })), 'HTTP 500');

const calls = [];
const api = new CodeInsightsApi({
  serverUrl: 'https://hub.example',
  token: async () => 'pat-value',
  fetch: async (url, init) => {
    calls.push({ url, authorization: new Headers(init.headers).get('Authorization') });
    const cursor = new URL(url).searchParams.get('cursor');
    const body = cursor === null ? [{ id: 1 }] : [{ id: 2 }];
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: cursor === null ? { 'X-Next-Cursor': 'c1' } : {},
    });
  },
});

const wrapped = await api.jsonWithCursor('/api/v1/analyses?project_key=demo');
assert.deepEqual(wrapped, { body: [{ id: 1 }], next: 'c1' });

const page = await api.page('/api/v1/analyses?project_key=demo');
assert.deepEqual(page, { items: [{ id: 1 }], next: 'c1' });
assert.equal(calls[0].authorization, 'Bearer pat-value');

// The cursor is opaque and appended without dropping existing query parameters.
const everything = await api.all('/api/v1/analyses?project_key=demo');
assert.deepEqual(everything, [{ id: 1 }, { id: 2 }]);
assert.ok(
  calls.some((call) => String(call.url).endsWith('/api/v1/analyses?project_key=demo&cursor=c1')),
  calls.map((call) => call.url).join('\n'),
);

let unauthorized = 0;
const expired = new CodeInsightsApi({
  serverUrl: 'https://hub.example',
  token: async () => 'stale',
  onUnauthorized: () => { unauthorized += 1; },
  fetch: async () => new Response(JSON.stringify({ detail: 'Invalid, expired, or inactive session' }), { status: 401 }),
});
await assert.rejects(() => expired.json('/api/v1/analyses'), (error) => error instanceof ApiError && error.status === 401);
assert.equal(unauthorized, 1);

// An anonymous client sends no Authorization header rather than an empty one.
const anonymous = new CodeInsightsApi({
  serverUrl: 'https://hub.example',
  token: async () => undefined,
  fetch: async (url, init) => {
    assert.equal(new Headers(init.headers).get('Authorization'), null);
    return new Response('{}', { status: 200 });
  },
});
await anonymous.json('/api/v1/health');

console.log('api client checks passed');

});
