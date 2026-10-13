import { test } from 'vitest';
import assert from 'node:assert/strict';

test('project-resolution', async () => {

const { resolveProject, resolutionLabel } = await import('./context/project-resolution');

const never = async () => {
  throw new Error('the hub must not be called');
};

// The explicit setting and the workspace mapping win before any network call.
assert.deepEqual(await resolveProject({ setting: 'demo', repositoryUrl: 'https://host/x' }, never), {
  state: 'resolved',
  projectKey: 'demo',
  source: 'setting',
});
assert.deepEqual(
  await resolveProject({ mapping: { projects: { web: 'demo-web' } }, folderName: 'web', repositoryUrl: 'https://host/x' }, never),
  { state: 'resolved', projectKey: 'demo-web', source: 'mapping' },
);

const lookup = (result) => async () => result;
assert.deepEqual(await resolveProject({ repositoryUrl: 'https://host/x' }, lookup([{ key: 'demo' }])), {
  state: 'resolved',
  projectKey: 'demo',
  source: 'repository',
});
// Duplicates collapse; genuinely different projects are reported, never picked at random.
assert.deepEqual(
  await resolveProject({ repositoryUrl: 'https://host/x' }, lookup([{ key: 'demo' }, { key: 'demo' }])),
  { state: 'resolved', projectKey: 'demo', source: 'repository' },
);
assert.deepEqual(
  await resolveProject({ repositoryUrl: 'https://host/x' }, lookup([{ key: 'b' }, { key: 'a' }])),
  { state: 'ambiguous', candidates: ['a', 'b'] },
);
assert.deepEqual(await resolveProject({ repositoryUrl: 'https://host/x' }, lookup([])), {
  state: 'unresolved',
  reason: 'unknown-repository',
});
assert.deepEqual(await resolveProject({}, lookup([{ key: 'demo' }])), { state: 'unresolved', reason: 'no-repository' });

const failed = await resolveProject({ repositoryUrl: 'https://host/x' }, async () => {
  throw new Error('HTTP 503');
});
assert.deepEqual(failed, { state: 'unresolved', reason: 'lookup-failed', detail: 'HTTP 503' });

assert.equal(resolutionLabel({ state: 'resolved', projectKey: 'demo', source: 'setting' }), 'demo');
assert.equal(resolutionLabel({ state: 'ambiguous', candidates: ['a', 'b'] }), 'plusieurs projets pour ce dépôt : a, b');
assert.equal(resolutionLabel({ state: 'unresolved', reason: 'unknown-repository' }), 'aucun projet lié à ce dépôt');
assert.equal(resolutionLabel({ state: 'unresolved', reason: 'no-repository' }), 'aucun dépôt Git détecté');

console.log('project resolution checks passed');

});
