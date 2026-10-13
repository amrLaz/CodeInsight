import { test } from 'vitest';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync, unlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('local-diff', async () => {

const { addedIndex, addedLines, isAdded } = await import('./local/diff');

// Hand-written first: the shapes that matter, including a deleted file.
const diff = [
  'diff --git a/src/a.ts b/src/a.ts',
  '--- a/src/a.ts',
  '+++ b/src/a.ts',
  '@@ -1,2 +1,4 @@',
  ' keep',
  '+added one',
  '+added two',
  ' keep',
  '@@ -10 +12,1 @@',
  '-removed',
  '+replaced',
  'diff --git a/src/gone.ts b/src/gone.ts',
  '--- a/src/gone.ts',
  '+++ /dev/null',
  '@@ -1,2 +0,0 @@',
  '-a',
  '-b',
].join('\n');
assert.deepEqual(addedLines(diff), [{ path: 'src/a.ts', addedLines: [2, 3, 12] }]);
assert.deepEqual(addedLines(''), []);

const index = addedIndex(addedLines(diff));
assert.equal(isAdded(index, 'src/a.ts', 2), true);
assert.equal(isAdded(index, 'src/a.ts', 4), false, 'an untouched line is not added');
assert.equal(isAdded(index, 'src/other.ts', 2), false);
assert.equal(isAdded(index, undefined, 2), false);
assert.equal(isAdded(index, 'src/a.ts', undefined), false);

// Then against real git output: the added lines must carry the content we wrote.
let repo;
try {
  repo = mkdtempSync(join(tmpdir(), 'codeinsights-local-'));
  execFileSync('git', ['-C', repo, 'init', '-q', '.']);
  execFileSync('git', ['-C', repo, 'config', 'user.email', 'checks@example.test']);
  execFileSync('git', ['-C', repo, 'config', 'user.name', 'checks']);
} catch {
  console.log('Git diff checks skipped: git is unavailable.');
  process.exit(0);
}
try {
  const before = ['one', 'two', 'three', 'four', 'five'];
  writeFileSync(join(repo, 'app.ts'), before.join('\n') + '\n');
  writeFileSync(join(repo, 'gone.ts'), 'temporary\n');
  execFileSync('git', ['-C', repo, 'add', '-A']);
  execFileSync('git', ['-C', repo, 'commit', '-qm', 'base']);

  const after = ['zero', 'one', 'two', 'INSERTED', 'three', 'four', 'five', 'tail'];
  writeFileSync(join(repo, 'app.ts'), after.join('\n') + '\n');
  unlinkSync(join(repo, 'gone.ts'));
  const diffText = execFileSync('git', ['-C', repo, 'diff', '--'], { encoding: 'utf8' });

  const files = addedLines(diffText);
  assert.deepEqual(
    files.map((file) => file.path),
    ['app.ts'],
    'a deleted file contributes no added line',
  );
  const expected = ['zero', 'INSERTED', 'tail'];
  const got = files[0].addedLines.map((line) => after[line - 1]);
  assert.deepEqual(got, expected, `added lines resolved to ${JSON.stringify(got)}`);
  console.log(`Git local-diff checks passed: ${files[0].addedLines.length} added lines, all on the content written.`);
} finally {
  rmSync(repo, { recursive: true, force: true });
}

});
