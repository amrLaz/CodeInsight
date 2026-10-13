import { test } from 'vitest';
import assert from 'node:assert/strict';

test('deep-link', async () => {

const { hubBranchKey, hubUrl } = await import('./hub/deep-link');

// Parameter names are the contract of apps/web/src/navigation-state.ts.
assert.equal(hubUrl({ serverUrl: 'https://hub.example' }), 'https://hub.example/');
assert.equal(hubUrl({ serverUrl: 'https://hub.example/' }), 'https://hub.example/');
assert.equal(hubUrl({ serverUrl: 'https://hub.example', project: 'demo' }), 'https://hub.example/?project=demo');
assert.equal(
  hubUrl({ serverUrl: 'https://hub.example', tab: 'issues', project: 'demo', branch: 'feature/x', analysisId: 'a1' }),
  'https://hub.example/?tab=issues&project=demo&branch=feature%2Fx&analysis_id=a1',
);
assert.equal(
  hubUrl({ serverUrl: 'https://hub.example', tab: 'issues', file: 'apps/api/Program.cs', severity: 'blocker', mine: true }),
  'https://hub.example/?tab=issues&issue_file=apps%2Fapi%2FProgram.cs&severity=blocker&mine=true',
);
// Defaults are omitted so a shared link matches what the SPA itself produces.
assert.equal(hubUrl({ serverUrl: 'https://hub.example', tab: 'projects', scope: 'overall', severity: 'all', mine: false }), 'https://hub.example/');
assert.equal(
  hubUrl({ serverUrl: 'https://hub.example', tab: 'rules', project: 'demo', selectedRule: 'semgrep/dotnet.cors', ruleStatus: 'disabled' }),
  'https://hub.example/?tab=rules&project=demo&selected_rule=semgrep%2Fdotnet.cors&rule_status=disabled',
);
assert.throws(() => hubUrl({ serverUrl: '  ' }), /server URL/);

// The hub selects a pull request build through its pull request, never its source branch.
assert.equal(hubBranchKey({ branch: 'refs/heads/develop', pr_id: '51382' }), 'pr:51382');
assert.equal(hubBranchKey({ branch: 'refs/heads/develop', pr_id: null }), 'refs/heads/develop');
assert.equal(hubUrl({ serverUrl: 'https://hub.example', branch: hubBranchKey({ branch: 'develop', pr_id: '7' }) }), 'https://hub.example/?branch=pr%3A7');

console.log('deep-link checks passed');

});
