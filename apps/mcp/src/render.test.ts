import { test } from 'vitest';
import assert from 'node:assert/strict';

test('render', async () => {

const { renderBranchReport, renderConditions, renderFindings, renderGate, renderFileQuality, clamp } = await import('./render');
const { branchReport } = await import('../../core/src/report/branch-report');
const { toIssue } = await import('../../core/src/report/issues');

const analysis = {
  analysis_id: 'a1',
  project_key: 'demo',
  branch: 'refs/heads/master',
  commit_sha: 'abcdef1234567890',
  completeness: 'complete',
  technical_state: 'success',
  quality_gate: 'fail',
  created_at: '2026-09-12T08:00:00Z',
  metrics: { bugs: 3, coverage: null },
  measurements: { bugs: { state: 'measured' }, coverage: { state: 'absent' } },
  gate_snapshot: {
    conditions: [
      { metric: 'bugs', required: true, operator: '<=', threshold: 0, value: 3, state: 'measured', result: 'fail' },
      { metric: 'code_smells', required: false, operator: '<=', threshold: 10, value: 2, state: 'measured', result: 'pass' },
      { metric: 'coverage', required: true, operator: '>=', threshold: 80, value: null, state: 'absent', result: 'unknown' },
    ],
  },
};

const rendered = renderBranchReport(branchReport(analysis), { hubUrl: 'https://hub/?project=demo', freshness: '2 commits locaux plus récents que l’analyse' });
assert.match(rendered, /^demo · refs\/heads\/master$/m);
assert.match(rendered, /Quality Gate : Fail/);
assert.match(rendered, /Bugs : 3/);
// Une couverture absente ne doit jamais apparaître comme 0 % : c'est le mode d'échec coûteux.
assert.doesNotMatch(rendered, /Couverture : 0/);
assert.match(rendered, /Non mesuré : .*Couverture \(absent\)/);
assert.match(rendered, /Conditions non tenues :\n {2}✗ Bugs : 3 <= 0 \(requise\)/);
assert.match(rendered, /Conditions non concluantes :\n {2}\? coverage : Absent/);
assert.match(rendered, /Détail : https:\/\/hub/);

// Un build de pull request est nommé comme tel : l'agent ne le prend pas pour le verdict de la branche.
const pullRequest = branchReport({ ...analysis, branch: 'refs/heads/develop', pr_id: '51382', pr_target_branch: 'refs/heads/master' });
assert.match(renderBranchReport(pullRequest, { hubUrl: undefined, freshness: undefined }), /^demo · PR #51382 · develop → master$/m);
assert.match(renderGate(pullRequest, undefined), /^demo · PR #51382 · develop → master$/m);

// Une analyse techniquement en échec ne rend jamais un verdict, quel que soit le gate stocké.
const broken = branchReport({ ...analysis, technical_state: 'failed', quality_gate: 'pass' });
assert.match(renderGate(broken, undefined), /Quality Gate : Unknown \(État technique : failed\)/);
assert.equal(renderConditions([]), 'Conditions : aucune condition de gate enregistrée sur cette analyse.');

const issues = [
  toIssue({ id: 11, severity: 'low', rule_id: 'r2', tool: 'semgrep', path: 'b.ts', line: 2, message: 'petit' }),
  toIssue({ id: 12, severity: 'error', rule_id: 'r1', tool: 'semgrep', path: 'a.ts', line: 4, message: 'grave' }),
];
const context = { projectKey: 'demo', branch: 'main', analysisId: 'a1', scope: 'toutes les issues', available: 2, limit: 200 };
const list = renderFindings(issues, context);
assert.match(list, /\[11\]/);
assert.match(list, /2 issues\./);
// La troncature est annoncée : un agent qui croit avoir tout lu conclut sur un dépôt à moitié lu.
const truncated = renderFindings(issues, { ...context, available: 42 });
assert.match(truncated, /2 issues affichées sur 42 au moins/);
assert.match(truncated, /augmenter « limit » \(max 200\)/);
assert.match(renderFindings([], context), /Aucune issue pour ce filtre sur cette analyse\./);

const quality = renderFileQuality(
  { path: 'a.ts', coverage: 'Non mesuré', duplication: 'aucun bloc dupliqué sur ce fichier · densité du projet 1%', duplicateBlocks: [] },
  { projectKey: 'demo', branch: 'main', analysisId: 'a1' },
);
assert.match(quality, /Couverture : Non mesuré/);
assert.doesNotMatch(quality, /Blocs dupliqués/);

assert.equal(clamp('  deux   espaces\nlignes  '), 'deux espaces lignes');
assert.equal(clamp('abcdef', 4), 'abc…');

console.log('render checks passed');

});
