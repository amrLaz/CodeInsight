import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('collaboration contracts', async () => {
const source = readFileSync(new URL('./features/issues/components/IssueReview.tsx', sourceUrl), 'utf8');
const result = ts.transpileModule(source, {
  compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  reportDiagnostics: true,
});

assert.equal(result.diagnostics?.length ?? 0, 0, 'Issue review must transpile without diagnostics');
assert.match(source, /version: finding\.version/, 'Updates keep optimistic concurrency');
assert.match(source, /expires_at: exception \? new Date\(draft\.expires_at\)\.toISOString\(\) : null/, 'Reopening clears exception expiry');
assert.match(source, /disabled=\{!isAdmin\}/, 'Exception expiry stays administrator-only');
assert.match(source, /invalidateQueries\(\{ queryKey: \['findings'\] \}\)/, 'A saved review refreshes Issues');
console.log('Issue review checks passed.');
});
