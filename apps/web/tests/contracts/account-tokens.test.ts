import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('account token contracts', async () => {
// Exercise the real handlers of the account dialog without a browser or a second implementation.
const path = './features/account/AccountTokens.tsx';
const source = readFileSync(new URL(path, sourceUrl), 'utf8');
const tree = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
function handler(name) {
  let expression;
  function visit(node) {
    if (ts.isVariableDeclaration(node) && node.name.getText(tree) === name) expression = node.initializer.getText(tree);
    ts.forEachChild(node, visit);
  }
  visit(tree);
  assert.ok(expression, `${name} is present`);
  const { outputText } = ts.transpileModule(`(${expression})`, { compilerOptions: { target: ts.ScriptTarget.ES2020 } });
  return vm.runInNewContext(outputText, { Number });
}

const validateTokenForm = handler('validateTokenForm');
const tokenScopes = handler('tokenScopes');
const valid = { name: 'VS Code', ttlDays: 90, triage: false };

assert.equal(validateTokenForm(valid), null);
assert.equal(validateTokenForm({ ...valid, name: '   ' }), 'Token name is required.');
assert.equal(validateTokenForm({ ...valid, name: 'x'.repeat(256) }), 'Token name must be 255 characters or fewer.');
// Expiration is mandatory and bounded exactly like the server rule.
const expiration = 'Expiration must be a whole number of days between 1 and 365.';
assert.equal(validateTokenForm({ ...valid, ttlDays: '' }), expiration);
assert.equal(validateTokenForm({ ...valid, ttlDays: 0 }), expiration);
assert.equal(validateTokenForm({ ...valid, ttlDays: 366 }), expiration);
assert.equal(validateTokenForm({ ...valid, ttlDays: 1.5 }), expiration);
assert.equal(validateTokenForm({ ...valid, ttlDays: 1 }), null);
assert.equal(validateTokenForm({ ...valid, ttlDays: 365 }), null);

// A token is read-only unless triage was explicitly requested, and never asks for more.
// Spread into this realm: values built inside the vm have another Array prototype.
assert.deepEqual([...tokenScopes(valid)], ['analysis:read']);
assert.deepEqual([...tokenScopes({ ...valid, triage: true })], ['analysis:read', 'collaboration:write']);

// The dialog never renders a token it did not just receive, and never lists a plaintext value.
assert.ok(source.includes('Copy this token now — it is never shown again'), 'one-time disclosure is stated');
assert.ok(!/token\.token\b/.test(source), 'the token list never reads a plaintext value');
assert.ok(source.includes('showModal()'), 'native modal dialog');
assert.ok(source.includes('aria-labelledby="account-tokens-title"'), 'accessible dialog title');

console.log('Account token dialog checks passed.');
});
