import { test } from 'vitest';
import assert from 'node:assert/strict';

test('local-analysis', async () => {

const { buildReport, isSuppressed, partialNotice, reportLabel } = await import('./local/analysis');
const { reportNodes } = await import('./report/report-nodes');

const finding = (over) => ({
  tool: 'semgrep',
  rule: 'semgrep/dotnet.permissive-cors',
  severity: 'medium',
  category: 'security',
  message: 'Permissive CORS',
  path: 'apps/api/Program.cs',
  line: 42,
  endLine: undefined,
  column: undefined,
  endColumn: undefined,
  ...over,
});
const changed = [{ path: 'apps/api/Program.cs', addedLines: [42, 43] }];
const measured = (findings) => ({ tool: 'semgrep', state: 'measured', reason: undefined, findings });
const absent = { tool: 'gitleaks', state: 'absent', reason: 'binaire gitleaks introuvable', findings: [] };

// Only findings on added lines are attributed to the pending change.
const report = buildReport([measured([finding(), finding({ line: 10 })])], changed, [], []);
assert.equal(report.introduced.length, 1);
assert.equal(report.introduced[0].line, 42);
assert.equal(report.elsewhere.length, 1, 'a pre-existing finding in the same file is context, not an introduction');
assert.equal(report.partial, false);

// Blockers first, then path, then line.
const ordered = buildReport(
  [measured([finding({ line: 43, severity: 'low' }), finding({ line: 42, severity: 'blocker' })])],
  changed,
  [],
  [],
);
assert.deepEqual(ordered.introduced.map((entry) => entry.severity), ['blocker', 'low']);

// Project exceptions and disabled rules keep the local run as quiet as the pipeline.
assert.equal(isSuppressed(finding(), [{ tool: 'semgrep', ruleId: 'dotnet.permissive-cors', path: null }], []), true);
assert.equal(isSuppressed(finding(), [{ tool: 'semgrep', ruleId: 'dotnet.permissive-cors', path: 'apps/api' }], []), true);
assert.equal(isSuppressed(finding(), [{ tool: 'semgrep', ruleId: 'dotnet.permissive-cors', path: 'apps/web' }], []), false);
assert.equal(isSuppressed(finding(), [{ tool: 'gitleaks', ruleId: 'dotnet.permissive-cors', path: null }], []), false);
assert.equal(isSuppressed(finding(), [], ['semgrep/dotnet.permissive-cors']), true);
const filtered = buildReport([measured([finding()])], changed, [{ tool: 'semgrep', ruleId: 'dotnet.permissive-cors', path: null }], []);
assert.equal(filtered.introduced.length, 0);
assert.equal(filtered.suppressed, 1);

// A producer that could not run is absent, and its absence is stated — never zero findings.
const partial = buildReport([measured([]), absent], changed, [], []);
assert.equal(partial.partial, true);
assert.equal(reportLabel(partial), 'aucune issue introduite sur les lignes ajoutées · semgrep · gitleaks absent');
assert.ok(partialNotice(partial).includes("L'absence d'issue ne vaut pas absence de problème"));
assert.equal(partialNotice(buildReport([measured([])], changed, [], [])), undefined);
// Nothing ran at all: the run concludes nothing.
const nothing = buildReport([absent], changed, [], []);
assert.equal(reportLabel(nothing), 'aucun producteur local disponible : rien n’a été mesuré');

// The tree keeps the analysed report first and never mixes the local run into its measures.
const branchReport = {
  analysisId: 'a1',
  projectKey: 'demo',
  branch: 'main',
  commitSha: 'abcdef1234',
  createdAt: '2026-09-10T00:00:00Z',
  profile: 'p0-dotnet/1',
  verdict: 'pass',
  completeness: 'complete',
  technicalState: 'success',
  metrics: [],
  conditions: [],
};
const nodes = reportNodes({
  kind: 'report',
  report: branchReport,
  context: { branch: 'main' },
  local: {
    label: reportLabel(report),
    notice: undefined,
    baseRef: 'origin/master',
    producers: [{ tool: 'semgrep', state: 'measured', reason: undefined }, { tool: 'gitleaks', state: 'absent', reason: 'introuvable' }],
    suppressed: 1,
    introduced: [{ id: 'local.finding.0', label: 'Permissive CORS', description: 'Medium · semgrep/x · a.cs:42', path: 'a.cs', line: 42 }],
    corrected: [],
    estimate: {
      label: 'aggravation probable',
      notice: "Estimation locale : ce n'est pas le verdict de la CI.",
      effect: 'worsens',
      conditions: [{ metric: 'vulnerabilities', label: 'Vulnérabilités', display: '0 → 1 <= 0', result: 'worsens' }],
    },
  },
});
assert.deepEqual(nodes.map((node) => node.id), ['verdict', 'local', 'identity', 'metrics', 'conditions']);
const local = nodes[1];
assert.equal(local.children[0].description, 'origin/master');
assert.ok(local.children.some((child) => child.description?.includes('introuvable')), 'an absent producer is listed');
assert.ok(local.children.some((child) => child.id === 'local.suppressed'));
const estimate = local.children.find((child) => child.id === 'local.estimate');
assert.equal(estimate.label, 'Estimation du gate : aggravation probable');
assert.equal(/pass/i.test(estimate.label), false, 'the tree must not present the estimate as Pass');
assert.equal(estimate.children[0].description, '0 → 1 <= 0');
assert.deepEqual(local.children.at(-1).open, { path: 'a.cs', line: 42 });
// Without a local run the tree is unchanged.
assert.deepEqual(reportNodes({ kind: 'report', report: branchReport, context: { branch: 'main' } }).map((node) => node.id), [
  'verdict',
  'identity',
  'metrics',
  'conditions',
]);

console.log('local analysis checks passed');

});
