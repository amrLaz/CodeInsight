import { test } from 'vitest';
import assert from 'node:assert/strict';

test('details', async () => {

const { DESCRIPTION_NOTICE, details, detailsHtml, escapeHtml, safeLink } = await import('./report/details');
const { toIssue } = await import('./report/issues');

const issue = toIssue({
  id: 1,
  path: 'src/app.ts',
  line: 12,
  severity: 'error',
  rule_id: 'S3358',
  tool: 'roslyn',
  message: 'Extract this nested ternary operation',
  evidence: {
    documentation_url: 'https://rules.example/S3358',
    locations: [
      { kind: 'primary', step: 0, path: 'src/app.ts', line: 12, column: 9 },
      { kind: 'related', step: 0, path: 'src/b.ts', line: 3 },
    ],
  },
});

const view = details(issue, 'approximate', 'abcdef0123456789');
assert.equal(view.title, 'Extract this nested ternary operation');
assert.ok(view.rows.some((row) => row.label === 'Règle' && row.value === 'roslyn/S3358'));
assert.ok(view.rows.some((row) => row.label === 'Commit analysé' && row.value === 'abcdef01'));
assert.ok(view.note.startsWith('position approximative'));
assert.deepEqual(view.locations, ['src/b.ts · L3']);
assert.ok(!view.locations.some((entry) => entry.includes('Étape 0')), 'steps are never numbered from zero');
assert.equal(view.descriptionNotice, DESCRIPTION_NOTICE);

// The panel never renders a rule explanation: CodeInsights stores none and inventing one is forbidden.
const html = detailsHtml(view);
assert.ok(html.includes(escapeHtml(DESCRIPTION_NOTICE)));
assert.ok(html.includes('https://rules.example/S3358'));
assert.ok(html.includes('var(--vscode-foreground)'), 'the panel uses the editor theme, not product colours');
assert.ok(html.includes("default-src 'none'"), 'the webview declares a strict policy');
assert.ok(!html.includes('<script'), 'no script in a static panel');

// Everything that comes from a report is escaped before reaching the panel.
assert.equal(escapeHtml('<img src=x onerror="alert(1)">'), '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;');
const hostile = toIssue({
  id: 2,
  path: 'src/<script>.ts',
  severity: 'low',
  rule_id: '"><script>alert(1)</script>',
  message: '<b>bold</b>',
  evidence: { documentation_url: 'javascript:alert(1)' },
});
const hostileHtml = detailsHtml(details(hostile, undefined, undefined));
assert.ok(!hostileHtml.includes('<script>alert(1)</script>'));
assert.ok(!hostileHtml.includes('<b>bold</b>'));
assert.ok(!hostileHtml.includes('javascript:'), 'a non-http documentation URL is not rendered as a link');
assert.equal(safeLink('javascript:alert(1)'), undefined);
assert.equal(safeLink('https://ok.example'), 'https://ok.example');
assert.equal(safeLink(undefined), undefined);

assert.ok(detailsHtml(undefined).includes('Sélectionner une issue'));

console.log('details checks passed');

});
