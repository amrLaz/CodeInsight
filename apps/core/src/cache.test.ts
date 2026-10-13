import { test } from 'vitest';
import assert from 'node:assert/strict';

test('cache', async () => {

const { ageLabel, cacheKey, issuesCacheKey, readCache, writeCache } = await import('./report/cache');

const memory = new Map();
const store = { get: (key) => memory.get(key), update: (key, value) => void memory.set(key, value) };
const key = cacheKey('demo', 'feature/x');
assert.equal(key, 'codeinsights.report:demo:feature/x');
assert.notEqual(key, cacheKey('demo', 'main'));
assert.equal(issuesCacheKey('demo', 'feature/x'), 'codeinsights.issues:demo:feature/x');
assert.notEqual(issuesCacheKey('demo', 'feature/x'), key);

assert.equal(readCache(store, key), undefined);
await writeCache(store, key, { analysis_id: 'a1' }, 1_000_000);
assert.deepEqual(readCache(store, key), { value: { analysis_id: 'a1' }, storedAt: 1_000_000 });

// Anything that is not a well-formed entry is ignored rather than shown as a report.
for (const corrupt of [null, 'text', 42, {}, { value: { a: 1 } }, { storedAt: 5 }]) {
  memory.set(key, corrupt);
  assert.equal(readCache(store, key), undefined, `corrupt entry ${JSON.stringify(corrupt)}`);
}

assert.equal(ageLabel(1_000_000, 1_000_000), "il y a moins d'une minute");
assert.equal(ageLabel(1_000_000, 1_000_000 + 30_000), "il y a moins d'une minute");
assert.equal(ageLabel(1_000_000, 1_000_000 + 600_000), 'il y a 10 minutes');
assert.equal(ageLabel(1_000_000, 1_000_000 + 7_200_000), 'il y a 2 heures');
assert.equal(ageLabel(1_000_000, 1_000_000 + 172_800_000), 'il y a 2 jours');
// A clock that went backwards must not produce a negative age.
assert.equal(ageLabel(1_000_000, 0), "il y a moins d'une minute");

console.log('cache checks passed');

});
