import { test } from 'vitest';
import assert from 'node:assert/strict';

test('branch-report', async () => {

const { branchReport, verdictReason } = await import('./report/branch-report');
const { reportNodes } = await import('./report/report-nodes');

const complete = {
  analysis_id: 'a1',
  project_key: 'demo',
  branch: 'feature/x',
  commit_sha: 'abcdef1234567890',
  quality_gate: 'pass',
  completeness: 'complete',
  technical_state: 'success',
  created_at: '2026-09-09T10:00:00Z',
  profile_id: 'upcoop-dotnet',
  profile_version: '2',
  metrics: { security_rating: 'A', bugs: 0, coverage: 41.239, duplication: null, code_smells: 12 },
  measurements: {
    security_rating: { state: 'measured' },
    bugs: { state: 'measured' },
    coverage: { state: 'measured' },
    duplication: { state: 'absent' },
    code_smells: { state: 'measured' },
    vulnerabilities: { state: 'failed' },
  },
  gate_snapshot: {
    conditions: [
      { metric: 'bugs', required: true, operator: '<=', threshold: 0, value: 0, state: 'measured', result: 'pass' },
      { metric: 'vulnerabilities', required: true, operator: '<=', threshold: 0, value: null, state: 'failed', result: 'unknown' },
    ],
  },
};

const report = branchReport(complete);
assert.equal(report.verdict, 'pass');
assert.equal(verdictReason(report), undefined);
const metric = (key) => report.metrics.find((row) => row.key === key);
assert.equal(metric('security_rating').display, 'A');
// Zero is a measured value and must be printed as zero.
assert.equal(metric('bugs').display, '0');
assert.equal(metric('bugs').measured, true);
assert.equal(metric('coverage').display, '41.24%');
// Absent, failed and missing measures are never zero.
assert.equal(metric('duplication').display, 'Absent');
assert.equal(metric('duplication').measured, false);
assert.equal(metric('vulnerabilities').display, 'Mesure en échec');
assert.equal(metric('dependency_vulnerabilities').display, 'Non mesuré');
assert.equal(report.conditions[0].display, '0 <= 0');
assert.equal(report.conditions[1].display, 'Mesure en échec');
assert.equal(report.conditions[1].result, 'unknown');
assert.equal(report.profile, 'upcoop-dotnet/2');

// An incomplete analysis never reads as a pass, and says why.
const incomplete = branchReport({ ...complete, completeness: 'incomplete', technical_state: 'incomplete' });
assert.equal(incomplete.verdict, 'unknown');
assert.equal(verdictReason(incomplete), 'État technique : incomplete');
// A technically failed run has no trustworthy verdict even when the stored gate says pass.
const technical = branchReport({ ...complete, technical_state: 'failed' });
assert.equal(technical.verdict, 'unknown');
assert.equal(verdictReason(technical), 'État technique : failed');
// Completeness alone still downgrades the verdict when no technical state is published.
const partial = branchReport({ ...complete, technical_state: undefined, completeness: 'incomplete' });
assert.equal(partial.verdict, 'unknown');
assert.equal(verdictReason(partial), 'Analyse incomplete');

// A payload reduced to its mandatory fields must not throw.
const minimal = branchReport({ analysis_id: 'a2', project_key: 'demo', branch: 'main', commit_sha: 'aaaaaa' });
assert.equal(minimal.verdict, 'unknown');
assert.equal(minimal.conditions.length, 0);
assert.equal(minimal.profile, undefined);
assert.equal(minimal.metrics.every((row) => row.measured === false), true);

const nodes = reportNodes({ kind: 'report', report, context: { branch: 'feature/x', headSha: 'beef1234' } });
assert.equal(nodes[0].label, 'Quality Gate : Pass');
assert.equal(nodes[0].icon, 'pass');
assert.equal(nodes[0].color, 'charts.green');
assert.deepEqual(
  nodes.map((node) => node.id),
  ['verdict', 'identity', 'metrics', 'conditions'],
);
// Colour code: a rating carries the A-E scale, a raw count carries no threshold of its
// own, and anything not measured stays muted rather than green or red.
const node = (id) => nodes.flatMap((root) => root.children ?? []).find((child) => child.id === id);
assert.equal(node('metric.security_rating').icon, 'circle-filled');
assert.equal(node('metric.security_rating').color, 'charts.green');
assert.equal(node('metric.bugs').icon, undefined);
assert.equal(node('metric.bugs').color, undefined);
assert.equal(node('metric.duplication').color, 'descriptionForeground');
assert.equal(node('condition.bugs').color, 'charts.green');
assert.equal(node('condition.vulnerabilities').color, 'descriptionForeground');
const failed = reportNodes({
  kind: 'report',
  report: branchReport({ ...complete, quality_gate: 'fail' }),
  context: { branch: 'feature/x' },
});
assert.equal(failed[0].color, 'charts.red');
const unknownVerdict = reportNodes({ kind: 'report', report: incomplete, context: { branch: 'feature/x' } });
assert.equal(unknownVerdict[0].color, 'descriptionForeground');
assert.equal(nodes[1].children.find((node) => node.id === 'identity.commit').description, 'abcdef12');

const stale = reportNodes({ kind: 'report', report, context: { branch: 'feature/x' }, staleness: 'il y a 2 heures' });
assert.equal(stale[0].tooltip, 'Rapport en cache, il y a 2 heures');
assert.deepEqual(reportNodes({ kind: 'message', message: 'Aucune analyse pour cette branche' }), [
  { id: 'message', label: 'Aucune analyse pour cette branche' },
]);

// A pull request build is named as such everywhere the report is displayed.
assert.equal(report.scope, 'feature/x');
assert.equal(report.pullRequest, undefined);
const pullRequest = branchReport({ ...complete, branch: 'refs/heads/develop', pr_id: '51382', pr_target_branch: 'refs/heads/master' });
assert.equal(pullRequest.scope, 'PR #51382 · develop → master');
assert.deepEqual(pullRequest.pullRequest, { id: '51382', targetBranch: 'refs/heads/master' });
assert.equal(branchReport({ ...complete, pr_id: '9', pr_target_branch: null }).scope, 'PR #9 · feature/x → cible non enregistrée');
const pullRequestNodes = reportNodes({ kind: 'report', report: pullRequest, context: { branch: 'develop' } });
assert.deepEqual(
  pullRequestNodes[1].children.filter((child) => child.id === 'identity.branch' || child.id === 'identity.pull-request').map((child) => child.description),
  ['refs/heads/develop', '#51382 → refs/heads/master'],
);
assert.equal(nodes[1].children.some((child) => child.id === 'identity.pull-request'), false);

console.log('branch report checks passed');

});
