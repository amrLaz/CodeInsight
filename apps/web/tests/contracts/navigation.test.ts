import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('navigation contracts', async () => {
const source = ts.createSourceFile('MainNavigation.tsx', readFileSync(new URL('./layouts/MainNavigation.tsx', sourceUrl), 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const calculation = source.statements.find((node) => ts.isFunctionDeclaration(node) && node.name?.text === 'visibleTabCount');
assert.ok(calculation);
const { outputText } = ts.transpileModule(calculation.getText(source).replace('export ', ''), { compilerOptions: { target: ts.ScriptTarget.ES2020 } });
const fit = runInNewContext(`${outputText}\nvisibleTabCount`);
assert.equal(fit([80, 100, 120], 304, 36, 2), 3, 'All tabs fit without reserving an unused menu');
assert.equal(fit([80, 100, 120], 303, 36, 2), 2, 'Reserve the menu width when any tab overflows');
assert.equal(fit([80, 100], 117, 36, 2), 0, 'Do not squeeze the first tab into insufficient space');
assert.equal(fit([80, 100], 118, 36, 2), 1, 'Exact available width fits');
assert.equal(fit([], 0, 36, 2), 0);
assert.equal(fit([180], 179, 36, 2), 0, 'A long label remains available in the menu');
assert.equal(fit([80.5, 99.5], 182, 36, 2), 2, 'Preserve fractional measured widths');
console.log('Navigation overflow width checks passed.');
});
