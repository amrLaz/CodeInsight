import { test } from 'vitest';
import assert from 'node:assert/strict';

test('duplication', async () => {

const { blocksFor, duplicateLabel, placeDuplicates } = await import('./report/duplication');
const { parseHunks } = await import('./report/drift');

const blocks = [
  { first: { path: 'src/a.ts', start_line: 2, end_line: 4 }, second: { path: 'src/b.ts', start_line: 10, end_line: 12 } },
  { first: { path: 'src/c.ts', start_line: 1, end_line: 2 }, second: { path: 'src/d.ts', start_line: 1, end_line: 2 } },
];
assert.deepEqual(blocksFor('src/a.ts', blocks), [{ start: 2, end: 4, other: { path: 'src/b.ts', start: 10, end: 12 } }]);

const placed = placeDuplicates('src/a.ts', blocks, undefined);
assert.equal(placed.length, 1);
assert.equal(duplicateLabel(placed[0]), 'Bloc dupliqué avec src/b.ts:10–12 (analyse)');

const rewritten = placeDuplicates(
  'src/a.ts',
  blocks,
  parseHunks(['@@ -2,3 +2,3 @@', '-a', '-b', '-c', '+x', '+y', '+z'].join('\n')),
);
assert.equal(rewritten.length, 0, 'a rewritten block is not painted on the new lines');

console.log('duplication checks passed');

});
