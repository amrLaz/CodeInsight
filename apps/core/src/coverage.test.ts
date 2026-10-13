import { test } from 'vitest';
import assert from 'node:assert/strict';

test('coverage', async () => {

const { coverageSummary, parseLcov, placeCoverage, preferLocal } = await import('./report/coverage');
const { parseHunks } = await import('./report/drift');

// Unmeasured coverage is a state, never a painted 0 %.
const absent = coverageSummary('absent', [], 'analysis');
assert.equal(absent.display, 'Absent');
assert.equal(absent.state, 'absent');
assert.equal(absent.covered, 0);

const measured = coverageSummary('measured', [{ line: 1, covered: true }, { line: 2, covered: false }], 'analysis');
assert.equal(measured.display, '50% (1/2)');
assert.equal(measured.state, 'measured');

const lcov = parseLcov(
  ['SF:/repo/src/a.ts', 'DA:1,1', 'DA:2,0', 'DA:3,4', 'end_of_record', 'SF:/elsewhere/secret.ts', 'DA:1,1', 'end_of_record'].join('\n'),
  '/repo',
);
assert.deepEqual(
  [...lcov.entries()],
  [
    ['src/a.ts', [{ line: 1, covered: true }, { line: 2, covered: false }, { line: 3, covered: true }]],
  ],
);

// Local LCOV is used only when it is newer than the analysed report.
assert.equal(preferLocal(2_000, '1970-01-01T00:00:01.000Z', 3_000), true);
assert.equal(preferLocal(500, '1970-01-01T00:00:01.000Z', 3_000), false);
assert.equal(preferLocal(5_000, '1970-01-01T00:00:01.000Z', 3_000), false, 'a clock in the future is ignored');

const diff = ['@@ -1,3 +1,4 @@', ' keep', '+added', ' keep', '-gone', '+rewritten'].join('\n');
const placed = placeCoverage(
  [{ line: 1, covered: true }, { line: 3, covered: false }],
  parseHunks(diff),
  'analysis',
);
assert.equal(placed.length, 1, 'the rewritten line is not painted on the new content');
assert.equal(placed[0].line, 1);
assert.equal(placed[0].covered, true);

console.log('coverage checks passed');

});
