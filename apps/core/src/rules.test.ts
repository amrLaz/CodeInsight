import { test } from 'vitest';
import assert from 'node:assert/strict';

test('rules', async () => {

const { defaultRuleFilters, filterRules, groupRules, rulesLabel, statusLabel, toRule } = await import('./report/rules');

const rules = [
  toRule({ tool: 'semgrep', rule_id: 'dotnet.cors', title: 'CORS', category: 'vulnerability', severity: 'high', occurrences: 3, status: 'enabled' }),
  toRule({ tool: 'gitleaks', rule_id: 'generic-api-key', category: 'secret', severity: 'blocker', occurrences: 1, status: 'disabled' }),
  toRule({ tool: 'semgrep', rule_id: 'unused', title: 'unused', category: 'code_smell', severity: 'low', occurrences: 8, status: 'enabled' }),
];
assert.equal(rules[0].title, 'CORS');
assert.equal(toRule({}).language, 'Non disponible');

const grouped = groupRules(rules);
assert.deepEqual(
  grouped.map((group) => group.tool),
  ['gitleaks', 'semgrep'],
);
assert.deepEqual(
  grouped.find((group) => group.tool === 'semgrep').rules.map((rule) => rule.ruleId),
  ['unused', 'dotnet.cors'],
);

assert.equal(filterRules(rules, { ...defaultRuleFilters, status: 'disabled' }).length, 1);
assert.equal(filterRules(rules, { ...defaultRuleFilters, search: 'cors' })[0].ruleId, 'dotnet.cors');
assert.equal(rulesLabel(defaultRuleFilters, 2), 'toutes les règles — 2 règles');
assert.equal(statusLabel('disabled'), 'désactivée');

console.log('rules checks passed');

});
