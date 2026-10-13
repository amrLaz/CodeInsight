import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('forms contracts', async () => {
// Exercise the actual handlers without a browser framework or a second implementation.
function handler(
  name,
  context,
  path = name === 'saveNotifChannel' ? './features/administration/Notifications.tsx' : name === 'platformSettings' ? './features/administration/PlatformSettings.tsx' : './features/quality-gates/QualityGates.tsx',
) {
  const source = readFileSync(new URL(path, sourceUrl), 'utf8');
  const tree = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  let expression;
  function visit(node) {
    if (ts.isVariableDeclaration(node) && node.name.getText(tree) === name) expression = node.initializer.getText(tree);
    ts.forEachChild(node, visit);
  }
  visit(tree);
  assert.ok(expression, `${name} is present`);
  const { outputText } = ts.transpileModule(`(${expression})`, { compilerOptions: { target: ts.ScriptTarget.ES2020 } });
  return vm.runInNewContext(outputText, context);
}

let gate = { name: 'Gate', max_bugs: 3 };
const update = handler('updateField', {
  creating: false,
  selectedGate: { gate_id: 'g1' },
  currentForm: gate,
  Number,
  setDraft: (draft) => {
    gate = draft.form;
  },
});
update('max_bugs', '');
assert.equal(gate.max_bugs, '', 'Clearing a threshold must not send zero');
update('max_bugs', '0');
assert.equal(gate.max_bugs, 0);
update('name', 'New gate');
assert.equal(gate.name, 'New gate');
const gateSource = readFileSync(new URL('./features/quality-gates/QualityGates.tsx', sourceUrl), 'utf8');
assert.match(gateSource, /draft && draft\.gateId/, 'An empty gate catalog must not dereference a missing draft');
assert.match(gateSource, /setCreating\(true\)[\s\S]*setDraft\(/, 'New gate starts a local draft');
assert.doesNotMatch(gateSource, /onClick=\{\(\) => run\(\{ kind: 'create'/, 'New gate must not call the API before form submission');
const settings = handler('platformSettings', {
  DEFAULT_PLATFORM_SETTINGS: { instance_name: 'Default', retention_keep_days: '90' },
  settingsQuery: { data: { instance_name: 'Refetched name', retention_keep_days: '30' } },
  draft: { instance_name: '' },
});
assert.equal(settings.instance_name, '', 'A settings refetch must preserve an explicitly empty draft field');
assert.equal(settings.retention_keep_days, '30', 'Unedited settings must follow the server');

let requests = 0;
let error = null;
let success = null;
let reset = false;
const context = {
  notifSaving: false,
  notifForm: { name: 'Webhook', channel_type: 'webhook', url: 'https://example.test', on_fail: true, on_pass: false },
  Error,
  channelMutation: { reset: () => {} },
  saveMutation: {
    mutateAsync: async () => {
      requests++;
      throw new Error('Channel creation failed (500).');
    },
  },
  setNotifFormError: (value) => {
    error = value;
  },
  notify: (value) => {
    success = value.message;
  },
  setNotifForm: () => {
    reset = true;
  },
  setShowNotifForm: () => {},
  loadNotifChannels: () => {
    throw new Error('must not reload after failure');
  },
  normalizeErrorMessage: (value) => value.message,
};
await handler('saveNotifChannel', context)();
assert.equal(requests, 1);
assert.match(error, /500/);
assert.equal(success, null);
assert.equal(reset, false, 'Keep the draft after failure');
await handler('saveNotifChannel', { ...context, notifSaving: true })();
assert.equal(requests, 1, 'Ignore duplicate submission while pending');
console.log('Form handler checks passed.');

const invalidations = [];
const devOps = handler(
  'mutation',
  {
    useMutation: (options) => options,
    queryClient: {
      invalidateQueries: async (options) => {
        invalidations.push(options.queryKey.join('/'));
      },
    },
  },
  './features/administration/DevOpsIntegrations.tsx',
);
await devOps.onSettled(undefined, new Error('403'), { kind: 'save' });
await devOps.onSettled(undefined, new Error('500'), { kind: 'delete' });
assert.equal(invalidations.length, 0, 'Failed configuration writes must not invalidate as successes');
await devOps.onSettled(undefined, new Error('400'), { kind: 'test' });
assert.deepEqual(invalidations, ['devops'], 'A failed connection test persists last_error and requires a refresh');
await devOps.onSettled(undefined, null, { kind: 'save' });
assert.deepEqual(invalidations, ['devops', 'devops']);
const devOpsSource = readFileSync(new URL('./features/administration/DevOpsIntegrations.tsx', sourceUrl), 'utf8');
assert.equal((devOpsSource.match(/>\s*Cancel\s*</g) ?? []).length, 1, 'The Azure DevOps dialog exposes one Cancel action');
assert.match(devOpsSource, /className="integration-option"/, 'Azure DevOps capabilities use the described native option layout');
const usersSource = readFileSync(new URL('./features/administration/UsersAccess.tsx', sourceUrl), 'utf8');
assert.match(usersSource, /className="config-modal iam-local-user-modal"[\s\S]*?<form[\s\S]*?className="form-grid compact"/, 'Local user creation uses a focused modal form');
assert.doesNotMatch(usersSource, /user_type/, 'The local-user form does not expose a field ignored by the API');
const dialogCloseSource = readFileSync(new URL('./components/common/DialogCloseButton.tsx', sourceUrl), 'utf8');
assert.match(dialogCloseSource, /className="icon-button"[\s\S]*?aria-label="Close"[\s\S]*?title="Close"[\s\S]*?<svg/, 'Dialog close actions use an accessible icon button with a tooltip');
for (const path of ['./App.tsx', './features/account/AccountTokens.tsx', './features/administration/DevOpsIntegrations.tsx', './features/issues/Issues.tsx', './features/rules/Rules.tsx', './features/administration/UsersAccess.tsx']) {
  const source = readFileSync(new URL(path, sourceUrl), 'utf8');
  assert.equal((source.match(/<DialogCloseButton\b/g) ?? []).length, (source.match(/<dialog\b/g) ?? []).length, `${path} gives every dialog the shared close icon`);
}
console.log('DevOps mutation checks passed.');
});
