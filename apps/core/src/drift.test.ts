import { test } from 'vitest';
import assert from 'node:assert/strict';

test('drift', async () => {

const { anchor, anchorNote, parseHunks } = await import('./report/drift');

// `git diff <sha> -- path`: old side is the analysed revision, new side the working tree.
const diff = [
  'diff --git a/app.ts b/app.ts',
  'index 111..222 100644',
  '--- a/app.ts',
  '+++ b/app.ts',
  '@@ -1,3 +1,5 @@',
  '+inserted a',
  '+inserted b',
  ' line 1',
  ' line 2',
  ' line 3',
  '@@ -17,8 +19,6 @@ line 16',
  ' line 17',
  '-line 18',
  '-line 19',
  '+rewritten',
  ' line 20',
  ' line 21',
  ' line 22',
  ' line 23',
  ' line 24',
  '\\ No newline at end of file',
].join('\n');

const hunks = parseHunks(diff);
assert.deepEqual(hunks, [
  { oldStart: 1, newStart: 1, ops: '++   ' },
  { oldStart: 17, newStart: 19, ops: ' --+     ' },
]);
assert.deepEqual(parseHunks(''), []);

// Unchanged lines keep their content: they are mapped precisely, not lumped into the hunk.
assert.deepEqual(anchor(1, hunks), { line: 3, state: 'shifted' });
assert.deepEqual(anchor(3, hunks), { line: 5, state: 'shifted' });
assert.deepEqual(anchor(17, hunks), { line: 19, state: 'shifted' });
assert.deepEqual(anchor(20, hunks), { line: 21, state: 'shifted' });
assert.deepEqual(anchor(24, hunks), { line: 25, state: 'shifted' });
// Only the rewritten lines themselves are approximate.
assert.deepEqual(anchor(18, hunks), { line: 20, state: 'approximate' });
assert.deepEqual(anchor(19, hunks), { line: 20, state: 'approximate' });
// Beyond the last hunk the accumulated delta applies.
assert.deepEqual(anchor(40, hunks), { line: 41, state: 'shifted' });

// A pure deletion leaves nothing to point at.
const removed = parseHunks(['@@ -5,3 +5,0 @@', '-a', '-b', '-c'].join('\n'));
assert.deepEqual(anchor(6, removed), { line: 5, state: 'deleted' });
// An unchanged file maps identity, and a missing diff is never presented as verified.
assert.deepEqual(anchor(12, []), { line: 12, state: 'exact' });
assert.deepEqual(anchor(12, undefined), { line: 12, state: 'unverified' });
assert.deepEqual(anchor(undefined, hunks), { line: undefined, state: 'unverified' });
assert.deepEqual(anchor(0, hunks), { line: undefined, state: 'unverified' });

assert.equal(anchorNote('exact'), undefined);
assert.equal(anchorNote('shifted'), undefined);
assert.ok(anchorNote('approximate').startsWith('position approximative'));
assert.ok(anchorNote('deleted').includes('supprimée'));
assert.ok(anchorNote('unverified').startsWith('position non vérifiée'));

console.log('drift checks passed');

});
