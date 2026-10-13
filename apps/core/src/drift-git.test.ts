import { test } from 'vitest';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('drift-git', async () => {

/**
 * The property that matters, verified against real `git diff` output rather than a
 * hand-written fixture: a position the extension presents as trustworthy must point at
 * the same content it was analysed on. Git's default three context lines are part of
 * what is exercised here.
 */
const { anchor, parseHunks } = await import('./report/drift');

const git = (repo, ...args) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' });
let repo;
try {
  repo = mkdtempSync(join(tmpdir(), 'codeinsights-drift-'));
  git(repo, 'init', '-q', '.');
  git(repo, 'config', 'user.email', 'checks@example.test');
  git(repo, 'config', 'user.name', 'checks');
} catch {
  console.log('Git drift checks skipped: git is unavailable.');
  process.exit(0);
}

try {
  const before = Array.from({ length: 40 }, (_, index) => `line ${index + 1}`);
  writeFileSync(join(repo, 'app.ts'), before.join('\n') + '\n');
  git(repo, 'add', '-A');
  git(repo, 'commit', '-qm', 'base');
  const sha = git(repo, 'rev-parse', 'HEAD').trim();

  // Insert at the top, rewrite a block, delete a block: the three drifts that matter.
  const after = ['inserted a', 'inserted b', ...before.slice(0, 19), 'rewritten', ...before.slice(23, 29), ...before.slice(31)];
  writeFileSync(join(repo, 'app.ts'), after.join('\n') + '\n');

  const hunks = parseHunks(git(repo, 'diff', sha, '--', 'app.ts'));
  const newLines = readFileSync(join(repo, 'app.ts'), 'utf8').split('\n');

  let trusted = 0;
  for (let line = 1; line <= before.length; line += 1) {
    const placed = anchor(line, hunks);
    if (placed.state !== 'exact' && placed.state !== 'shifted') continue;
    trusted += 1;
    assert.equal(newLines[placed.line - 1], before[line - 1], `analysed line ${line} anchored on foreign content`);
  }
  // Unchanged lines must survive as trusted anchors, otherwise everything is "approximate".
  assert.ok(trusted >= 30, `expected most lines to keep a trusted anchor, got ${trusted}`);
  for (const rewritten of [20, 21, 22, 23, 30, 31]) {
    const placed = anchor(rewritten, hunks);
    assert.ok(placed.state === 'approximate' || placed.state === 'deleted', `line ${rewritten} is ${placed.state}`);
  }
  console.log(`Git drift checks passed: ${trusted} trusted anchors, all on their original content.`);
} finally {
  rmSync(repo, { recursive: true, force: true });
}

});
