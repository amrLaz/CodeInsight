import { test } from 'vitest';
import assert from 'node:assert/strict';

test('diagnostics', async () => {

const { diagnosticSeverity, diagnosticSpec } = await import('./report/diagnostics');
const { toIssue } = await import('./report/issues');

const issue = (over) => toIssue({ id: 1, path: 'a.ts', line: 10, severity: 'error', rule_id: 'r1', tool: 'semgrep', message: 'Boom', ...over });

assert.equal(diagnosticSeverity(issue()), 'error');
assert.equal(diagnosticSeverity(issue({ severity: 'high' })), 'error');
assert.equal(diagnosticSeverity(issue({ severity: 'warning' })), 'warning');
assert.equal(diagnosticSeverity(issue({ severity: 'low' })), 'information');
assert.equal(diagnosticSeverity(issue({ severity: 'note' })), 'hint');
// An accepted exception stays visible but never as an error.
assert.equal(diagnosticSeverity(issue({ excepted: true })), 'hint');

const exact = diagnosticSpec(issue(), { line: 10, state: 'exact' });
assert.equal(exact.line, 10);
assert.equal(exact.message, 'Boom [Blocker]');
assert.equal(exact.code, 'r1');
assert.equal(exact.unnecessary, false);

// The drift is written into the message: the reader must not believe a shifted line is exact.
assert.equal(
  diagnosticSpec(issue(), { line: 14, state: 'approximate' }).message,
  'Boom — position approximative : la ligne analysée a changé [Blocker]',
);
assert.equal(diagnosticSpec(issue(), { line: 14, state: 'shifted' }).message, 'Boom [Blocker]');
assert.ok(diagnosticSpec(issue(), { line: 10, state: 'unverified' }).message.includes('position non vérifiée'));
assert.ok(diagnosticSpec(issue({ excepted: true }), { line: 10, state: 'exact' }).message.includes('exception acceptée'));

// A message-less finding falls back to its rule instead of an empty diagnostic.
assert.equal(diagnosticSpec(issue({ message: '' }), { line: 3, state: 'exact' }).message, 'r1 [Blocker]');
// Without a location there is no diagnostic at all.
assert.equal(diagnosticSpec(issue({ line: null }), { line: undefined, state: 'unverified' }), undefined);
assert.equal(diagnosticSpec(issue({ evidence: { documentation_url: 'https://doc' } }), { line: 1, state: 'exact' }).documentationUrl, 'https://doc');

console.log('diagnostic checks passed');

});
