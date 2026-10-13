import { test } from 'vitest';
import assert from 'node:assert/strict';

test('producers', async () => {

const { parseGitleaks, parseSemgrep, semgrepSeverity } = await import('./local/producers');

// Real semgrep --json shape.
const semgrep = JSON.stringify({
  errors: [],
  results: [
    {
      check_id: 'dotnet.permissive-cors',
      path: '/repo/apps/api/Program.cs',
      start: { line: 42, col: 9 },
      end: { line: 42, col: 31 },
      extra: { message: 'Permissive CORS requires a security review.', severity: 'WARNING', metadata: { category: 'security' } },
    },
    { check_id: 'no-location', path: '/repo/a.cs', start: {}, extra: { severity: 'ERROR' } },
    { check_id: 'outside', path: '/elsewhere/b.cs', start: { line: 2 }, extra: { severity: 'ERROR' } },
  ],
});
const findings = parseSemgrep(semgrep, '/repo');
assert.equal(findings.length, 1, 'a finding without a line and one outside the repository are dropped');
assert.deepEqual(findings[0], {
  tool: 'semgrep',
  rule: 'semgrep/dotnet.permissive-cors',
  severity: 'medium',
  category: 'security',
  message: 'Permissive CORS requires a security review.',
  path: 'apps/api/Program.cs',
  line: 42,
  endLine: 42,
  column: 9,
  endColumn: 31,
});
assert.equal(semgrepSeverity('ERROR'), 'blocker');
assert.equal(semgrepSeverity('WARNING'), 'medium');
assert.equal(semgrepSeverity('INFO'), 'low');
assert.equal(semgrepSeverity('WHATEVER'), 'info', 'an unknown severity is never promoted');
assert.deepEqual(parseSemgrep('{"results":[]}', '/repo'), []);
assert.throws(() => parseSemgrep('not json', '/repo'), /illisible/);

// Real gitleaks --report-format json shape (an array, empty output when clean).
const gitleaks = JSON.stringify([
  {
    RuleID: 'generic-api-key',
    Description: 'Detected a Generic API Key',
    File: 'apps/api/appsettings.json',
    StartLine: 7,
    StartColumn: 12,
    EndColumn: 52,
    Secret: 'REDACTED-BUT-STILL-A-SECRET',
    Match: 'apikey = REDACTED-BUT-STILL-A-SECRET',
  },
  { RuleID: 'no-file', StartLine: 3 },
]);
const leaks = parseGitleaks(gitleaks, '/repo');
assert.equal(leaks.length, 1);
assert.equal(leaks[0].rule, 'gitleaks/generic-api-key');
assert.equal(leaks[0].severity, 'blocker', 'a secret is always blocking');
assert.equal(leaks[0].category, 'secret');
assert.equal(leaks[0].path, 'apps/api/appsettings.json');
// The secret itself never reaches a message that ends up in logs or in the UI.
const serialised = JSON.stringify(leaks);
assert.ok(!serialised.includes('REDACTED-BUT-STILL-A-SECRET'), 'the matched value is not carried over');
assert.deepEqual(parseGitleaks('', '/repo'), [], 'no output means no leak');
assert.deepEqual(parseGitleaks('[]', '/repo'), []);
assert.throws(() => parseGitleaks('nope', '/repo'), /illisible/);

const { isPathConfig } = await import('./local/runner');
// Local rule files can be checked for existence; registry identifiers cannot.
assert.equal(isPathConfig('.codeinsights/semgrep-mqr-v1.json'), true);
assert.equal(isPathConfig('/etc/rules.yaml'), true);
assert.equal(isPathConfig('rules/custom.yml'), true);
assert.equal(isPathConfig('p/ci'), false);
assert.equal(isPathConfig('r/csharp'), false);

console.log('producer checks passed');

});
