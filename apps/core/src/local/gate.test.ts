import { test } from 'vitest';
import assert from 'node:assert/strict';

test('gate', async () => {

const { correctedIssues, estimateGate, estimateLabel, metricFor, sameRule } = await import('./gate');

const finding = (over) => ({
  tool: 'semgrep',
  rule: 'semgrep/dotnet.permissive-cors',
  severity: 'medium',
  category: 'security',
  message: 'Permissive CORS',
  path: 'apps/api/Program.cs',
  line: 42,
  ...over,
});

assert.equal(metricFor({ tool: 'gitleaks', category: 'secret' }), 'vulnerabilities');
assert.equal(metricFor({ tool: 'semgrep', category: 'bug' }), 'bugs');
assert.equal(metricFor({ tool: 'semgrep', category: 'code' }), 'code_smells');
assert.equal(metricFor({ tool: 'semgrep', category: 'ioc' }), undefined);
assert.equal(sameRule({ tool: 'semgrep', rule: 'dotnet.x' }, { tool: 'semgrep', rule: 'semgrep/dotnet.x' }), true);
assert.equal(sameRule({ tool: 'semgrep', rule: 'dotnet.x' }, { tool: 'gitleaks', rule: 'dotnet.x' }), false);

const measured = (tool, findings) => ({ tool, state: 'measured', reason: undefined, findings });
const absent = { tool: 'gitleaks', state: 'absent', reason: 'introuvable', findings: [] };
const conditions = [
  { metric: 'bugs', required: true, operator: '<=', threshold: 0, value: 0, state: 'measured', result: 'pass' },
  { metric: 'vulnerabilities', required: true, operator: '<=', threshold: 0, value: 0, state: 'measured', result: 'pass' },
  { metric: 'coverage', required: true, operator: '>=', threshold: 80, value: 41, state: 'measured', result: 'fail' },
];

// A new secret on an added line would push vulnerabilities over the threshold.
const secret = finding({ tool: 'gitleaks', rule: 'gitleaks/generic-api-key', category: 'secret', severity: 'blocker' });
const worse = estimateGate(
  conditions,
  { producers: [measured('semgrep', []), measured('gitleaks', [secret])], introduced: [secret], elsewhere: [], suppressed: 0, partial: false },
  [],
);
assert.equal(worse.effect, 'worsens');
assert.equal(worse.label, 'aggravation probable');
assert.equal(worse.label.toLowerCase().includes('pass'), false, 'the estimate must never read as Pass');
assert.equal(worse.conditions.find((row) => row.metric === 'vulnerabilities').result, 'worsens');
assert.equal(worse.conditions.find((row) => row.metric === 'coverage').result, 'not-estimable');
assert.ok(worse.notice.includes("ce n'est pas le verdict de la CI"));

// No local finding, both producers measured: we may say no worsening, still not Pass.
const quiet = estimateGate(
  conditions,
  { producers: [measured('semgrep', []), measured('gitleaks', [])], introduced: [], elsewhere: [], suppressed: 0, partial: false },
  [],
);
assert.equal(quiet.effect, 'no-worsening-detected');
assert.equal(estimateLabel(quiet.effect), 'aucune aggravation détectée');
assert.equal(/pass/i.test(quiet.label), false);

// gitleaks absent: we cannot conclude that vulnerabilities did not worsen.
const partial = estimateGate(
  conditions,
  { producers: [measured('semgrep', []), absent], introduced: [], elsewhere: [], suppressed: 0, partial: true },
  [],
);
assert.equal(partial.effect, 'unknown');
assert.equal(partial.conditions.find((row) => row.metric === 'vulnerabilities').result, 'unknown');
assert.ok(partial.notice.includes('gitleaks (absent)'));

// A disappearing analysed finding of a measured tool, on a changed file, is a possible fix.
const branch = [finding({ tool: 'semgrep', rule: 'dotnet.permissive-cors', category: 'security' })];
const local = {
  producers: [measured('semgrep', []), measured('gitleaks', [])],
  introduced: [],
  elsewhere: [],
  suppressed: 0,
  partial: false,
};
const corrected = correctedIssues(branch, local, ['apps/api/Program.cs']);
assert.equal(corrected.length, 1);
assert.equal(correctedIssues(branch, local, ['apps/web/App.tsx']).length, 0, 'an untouched file is not a local fix');

const improved = estimateGate(
  [{ metric: 'vulnerabilities', required: true, operator: '<=', threshold: 0, value: 1, state: 'measured', result: 'fail' }],
  local,
  corrected,
);
assert.equal(improved.effect, 'improves');
assert.equal(improved.label, 'amélioration possible');
assert.equal(/pass/i.test(improved.label), false, 'a possible improvement is not a local pass');

// Nothing ran: the estimate concludes nothing, even on a green CI snapshot.
const idle = estimateGate(conditions, { producers: [absent], introduced: [], elsewhere: [], suppressed: 0, partial: true }, []);
assert.equal(idle.effect, 'unknown');
assert.ok(idle.notice.includes('ne conclut rien'));

console.log('local gate checks passed');

});
