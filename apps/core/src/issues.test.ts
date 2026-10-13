import { test } from 'vitest';
import assert from 'node:assert/strict';

test('issues', async () => {

const { countLabel, defaultIssueFilters, filtersLabel, groupIssues, issuesQuery, severity, severityColor, severityIcon, severityLabel, toIssue } = await import('./report/issues');

// The vocabulary is the one of apps/web/src/components/Issues.tsx: same analysis, same ranking.
for (const raw of ['critical', 'error', 'blocker', 'BLOCKER']) assert.equal(severity(raw), 'blocker', raw);
assert.equal(severity('high'), 'high');
for (const raw of ['warning', 'medium']) assert.equal(severity(raw), 'medium', raw);
for (const raw of ['low', 'convention']) assert.equal(severity(raw), 'low', raw);
for (const raw of ['note', 'info', '', undefined]) assert.equal(severity(raw), 'info', String(raw));
assert.equal(severityLabel('blocker'), 'Blocker');
assert.equal(severityIcon('blocker'), 'error');
assert.equal(severityIcon('medium'), 'warning');
assert.equal(severityIcon('info'), 'info');
// Blocker and high share the `error` icon: the colour is what tells them apart.
assert.equal(severityColor('blocker'), 'charts.red');
assert.equal(severityColor('high'), 'codeinsights.severityHigh');
assert.notEqual(severityColor('blocker'), severityColor('high'));
assert.equal(severityColor('medium'), 'charts.yellow');
assert.equal(severityColor('low'), 'charts.blue');
assert.equal(severityColor('info'), 'descriptionForeground');
// That `codeinsights.severityHigh` is actually contributed is asserted by the host that
// declares it: apps/vscode/src/severity-colors.test.ts.

// Windows separators become repository paths; an absent location stays absent.
const mapped = toIssue({ id: 7, path: 'apps\\api\\Program.cs', line: 12, severity: 'error', rule_id: 'r1', tool: 'roslyn', message: 'm' });
assert.equal(mapped.path, 'apps/api/Program.cs');
assert.equal(mapped.line, 12);
assert.equal(mapped.severity, 'blocker');
const located = toIssue({ id: 8, path: null, line: 0, severity: 'low' });
assert.equal(located.path, undefined);
assert.equal(located.line, undefined, 'line 0 is not a location');
assert.equal(located.rule, 'unknown');
assert.equal(located.documentationUrl, undefined);
assert.equal(toIssue({ id: 9, evidence: { documentation_url: '' } }).documentationUrl, undefined);
assert.equal(toIssue({ id: 10, evidence: { documentation_url: 'https://doc' } }).documentationUrl, 'https://doc');

const issues = [
  toIssue({ id: 1, path: 'src/b.ts', line: 20, severity: 'warning', rule_id: 'r2' }),
  toIssue({ id: 2, path: 'src/a.ts', line: 5, severity: 'low', rule_id: 'r3' }),
  toIssue({ id: 3, path: 'src/b.ts', line: 4, severity: 'error', rule_id: 'r1' }),
  toIssue({ id: 4, path: null, severity: 'high', rule_id: 'r4' }),
];
const groups = groupIssues(issues);
// Worst severity first, then path; inside a file, severity then line.
assert.deepEqual(
  groups.map((group) => group.label),
  ['src/b.ts', 'Localisation indisponible', 'src/a.ts'],
);
assert.equal(groups[0].worst, 'blocker');
assert.deepEqual(
  groups[0].issues.map((issue) => issue.id),
  [3, 1],
);
assert.equal(groups[1].path, undefined);
assert.deepEqual(groupIssues([]), []);

assert.equal(countLabel(groups), '4 issues · 3 fichiers · 1 bloquante');
assert.equal(countLabel([]), 'aucune issue');
assert.equal(countLabel(groupIssues([toIssue({ id: 5, path: 'a.ts', severity: 'low' })])), '1 issue · 1 fichier');

// Filters are applied by the server: the IDE must not filter a page it already truncated.
assert.equal(issuesQuery(defaultIssueFilters), 'limit=100');
assert.equal(
  issuesQuery({ severity: 'blocker', category: 'vulnerability', state: 'open', mine: true }),
  'limit=100&severity=blocker&category=vulnerability&state=open&mine=true',
);
assert.equal(filtersLabel(defaultIssueFilters), 'toutes les issues');
assert.equal(filtersLabel({ severity: 'high', category: 'all', state: 'all', mine: true }), 'High · mes issues');

console.log('issues checks passed');

});
