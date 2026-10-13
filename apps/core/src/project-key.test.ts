import { test } from 'vitest';
import assert from 'node:assert/strict';

test('project-key', async () => {

const { normalizeRepositoryUrl, resolveProjectKey } = await import('./context/project-key');

assert.equal(
  normalizeRepositoryUrl('git@ssh.dev.azure.com:v3/up-group/Up.France.ODI/Up.France.CodeInsights'),
  'https://ssh.dev.azure.com/v3/up-group/up.france.odi/up.france.codeinsights',
);
assert.equal(
  normalizeRepositoryUrl('https://up-group@dev.azure.com/up-group/Up.France.ODI/_git/App.git/'),
  'https://up-group@dev.azure.com/up-group/up.france.odi/_git/app',
);

assert.deepEqual(resolveProjectKey({ setting: ' demo ' }), { state: 'resolved', projectKey: 'demo', source: 'setting' });
assert.deepEqual(
  resolveProjectKey({ mapping: { projects: { web: 'demo-web' } }, folderName: 'web' }),
  { state: 'resolved', projectKey: 'demo-web', source: 'mapping' },
);
assert.deepEqual(
  resolveProjectKey({
    mapping: { projects: { 'https://dev.azure.com/up-group/odi/_git/app': 'demo-app' } },
    repositoryUrl: 'git@dev.azure.com:up-group/odi/_git/app.git',
  }),
  { state: 'resolved', projectKey: 'demo-app', source: 'mapping' },
);
// `projectKey` lie le dépôt entier, mais la correspondance de dossier reste plus précise.
assert.deepEqual(
  resolveProjectKey({ mapping: { projectKey: ' demo-root ' }, folderName: 'web' }),
  { state: 'resolved', projectKey: 'demo-root', source: 'mapping' },
);
assert.deepEqual(
  resolveProjectKey({ mapping: { projectKey: 'demo-root', projects: { web: 'demo-web' } }, folderName: 'web' }),
  { state: 'resolved', projectKey: 'demo-web', source: 'mapping' },
);
// Le réglage explicite du poste passe toujours devant la configuration du dépôt.
assert.deepEqual(
  resolveProjectKey({ setting: 'demo', mapping: { projectKey: 'demo-root' } }),
  { state: 'resolved', projectKey: 'demo', source: 'setting' },
);

// A folder name is never enough on its own: reporting another project's gate is worse than reporting none.
assert.deepEqual(resolveProjectKey({ folderName: 'web' }), { state: 'unresolved', reason: 'not-configured' });
assert.deepEqual(resolveProjectKey({}), { state: 'unresolved', reason: 'not-configured' });

console.log('project-key checks passed');

});
