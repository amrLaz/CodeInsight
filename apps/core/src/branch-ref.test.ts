import { test } from 'vitest';
import assert from 'node:assert/strict';

test('branch-ref', async () => {

const { branchCandidates, latestAnalysis } = await import('./api/codeinsights');

// The CI reports Build.SourceBranch, the Git extension reports the short name.
assert.deepEqual(branchCandidates('develop'), ['refs/heads/develop', 'develop']);
assert.deepEqual(branchCandidates(' feature/gct/x '), ['refs/heads/feature/gct/x', 'feature/gct/x']);
assert.deepEqual(branchCandidates('refs/heads/develop'), ['refs/heads/develop', 'develop']);
assert.deepEqual(branchCandidates('refs/remotes/origin/develop'), ['refs/remotes/origin/develop', 'develop']);
assert.deepEqual(branchCandidates('refs/pull/12/merge'), ['refs/pull/12/merge']);
assert.deepEqual(branchCandidates('  '), []);

const asked = [];
const api = {
  page: async (path) => {
    const branch = new URL(path, 'https://hub.example').searchParams.get('branch');
    asked.push(branch);
    return { items: branch === 'refs/heads/develop' ? [{ analysis_id: 'a1', branch }] : [], nextCursor: undefined };
  },
};

// The stored ref is found on the first try: no second request when the full ref matches.
const found = await latestAnalysis(api, 'Up.Public.ReactControls', 'develop');
assert.equal(found.analysis_id, 'a1');
assert.deepEqual(asked, ['refs/heads/develop']);

// A hub storing short names is reached by the fallback rather than reported as unanalysed.
asked.length = 0;
const shortOnly = {
  page: async (path) => {
    const branch = new URL(path, 'https://hub.example').searchParams.get('branch');
    asked.push(branch);
    return { items: branch === 'main' ? [{ analysis_id: 'a2', branch }] : [], nextCursor: undefined };
  },
};
assert.equal((await latestAnalysis(shortOnly, 'demo', 'main')).analysis_id, 'a2');
assert.deepEqual(asked, ['refs/heads/main', 'main']);

// A branch the hub never analysed stays null; both spellings were tried before saying so.
asked.length = 0;
assert.equal(await latestAnalysis({ page: async () => ({ items: [] }) }, 'demo', 'topic'), null);

// A branch build wins; a pull request build is only the fallback, and it carries its pr_id.
const kinds = [];
const builds = (withBranchBuild) => ({
  page: async (path) => {
    const params = new URL(path, 'https://hub.example').searchParams;
    kinds.push(`${params.get('kind')}:${params.get('branch')}`);
    if (params.get('branch') !== 'refs/heads/feature/x') return { items: [] };
    if (params.get('kind') === 'branch') return { items: withBranchBuild ? [{ analysis_id: 'branch-build', branch: 'refs/heads/feature/x' }] : [] };
    return { items: [{ analysis_id: 'pr-build', branch: 'refs/heads/feature/x', pr_id: '7' }] };
  },
});
assert.equal((await latestAnalysis(builds(true), 'demo', 'feature/x')).analysis_id, 'branch-build');
assert.deepEqual(kinds, ['branch:refs/heads/feature/x']);
kinds.length = 0;
const fallback = await latestAnalysis(builds(false), 'demo', 'feature/x');
assert.equal(fallback.pr_id, '7');
// Every spelling of a branch build is tried before falling back to a pull request build.
assert.deepEqual(kinds, ['branch:refs/heads/feature/x', 'branch:feature/x', 'pull_request:refs/heads/feature/x']);

console.log('branch-ref.test.ts ok');

});
