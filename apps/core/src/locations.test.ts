import { test } from 'vitest';
import assert from 'node:assert/strict';

test('locations', async () => {

const { flows, locationLabel, locationsSuffix, primaryRange, secondary, toLocation, toLocations } = await import('./report/locations');
const { toIssue } = await import('./report/issues');

// SARIF shape as the hub serialises it.
const evidence = {
  locations_truncated: false,
  locations: [
    { kind: 'primary', step: 0, path: 'src\\app.ts', line: 12, column: 9, end_column: 24, repository_relative: true },
    { kind: 'flow', flow: 1, thread: 1, step: 2, path: 'src/b.ts', line: 30 },
    { kind: 'flow', flow: 1, thread: 1, step: 1, path: 'src/a.ts', line: 4, column: 3 },
    { kind: 'related', step: 0, path: 'src/c.ts', line: 7 },
  ],
};

const parsed = toLocations(evidence);
assert.equal(parsed.length, 4);
assert.equal(parsed[0].path, 'src/app.ts', 'windows separators are normalised');
assert.equal(secondary(parsed).length, 3, 'the primary location is not a secondary one');
// Steps keep the producer's order, not the array order.
assert.deepEqual(
  flows(parsed)[0].steps.map((step) => step.step),
  [1, 2],
);
assert.equal(flows(parsed).length, 1);
// The rank comes from the displayed order, not from the internal index the hub assigned.
assert.equal(locationLabel(flows(parsed)[0].steps[0], 1), 'Étape 1 · src/a.ts · L4, Col 3');
assert.equal(locationLabel(flows(parsed)[0].steps[1], 2), 'Étape 2 · src/b.ts · L30');
assert.equal(locationLabel(flows(parsed)[0].steps[0]), 'src/a.ts · L4, Col 3', 'without a rank the step number is not invented');
assert.equal(locationLabel({ kind: 'related', path: undefined, line: undefined, step: 0 }), 'fichier inconnu · position inconnue');

assert.equal(locationsSuffix(parsed, false), ' [+3 locations]');
assert.equal(locationsSuffix([parsed[0]], false), '', 'a lone primary location adds nothing');
assert.equal(locationsSuffix([parsed[0], parsed[3]], false), ' [+1 location]');
assert.equal(locationsSuffix([parsed[0]], true), ' [locations tronquées]');
assert.equal(locationsSuffix(parsed, true), ' [+3 locations, tronquées]');

// The squiggle covers the expression only when the producer gave usable columns.
assert.deepEqual(primaryRange(parsed, 12), { column: 9, endColumn: 24 });
assert.equal(primaryRange(parsed, 30), undefined, 'no primary location on that line');
assert.equal(primaryRange([toLocation({ kind: 'primary', step: 0, line: 3 })], 3), undefined, 'no column, no range');
assert.deepEqual(primaryRange([toLocation({ kind: 'primary', step: 0, line: 3, column: 5, end_column: 5 })], 3), {
  column: 5,
  endColumn: undefined,
});
// Zero and negative positions are not positions.
assert.equal(toLocation({ kind: 'primary', step: 0, line: 0, column: 0 }).line, undefined);
assert.equal(toLocation({ kind: 'nonsense', step: 0 }).kind, 'related', 'an unknown kind is never treated as primary');

const issue = toIssue({ id: 1, path: 'src/app.ts', line: 12, severity: 'error', rule_id: 'r1', evidence });
assert.equal(issue.locationsSuffix, ' [+3 locations]');
assert.equal(issue.locationsTruncated, false);
assert.equal(toIssue({ id: 2 }).locations.length, 0, 'a finding without evidence has no locations');
assert.equal(toIssue({ id: 3, evidence: { locations_truncated: true } }).locationsSuffix, ' [locations tronquées]');

console.log('location checks passed');

});
