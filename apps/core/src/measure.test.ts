import { test } from 'vitest';
import assert from 'node:assert/strict';

test('measure', async () => {

const { formatMetric, freshness, gateIcon, gateLabel, gateVerdict, measurementLabel } = await import('./report/measure');

// An incomplete analysis is never presented as a pass, exactly like the hub.
assert.equal(gateVerdict('pass', 'complete'), 'pass');
assert.equal(gateVerdict('pass', 'incomplete'), 'unknown');
assert.equal(gateVerdict('pass', undefined), 'unknown');
assert.equal(gateVerdict('fail', 'complete'), 'fail');
assert.equal(gateVerdict(undefined, 'complete'), 'unknown');
assert.equal(gateLabel(gateVerdict('pass', 'incomplete')), 'Unknown');
assert.equal(gateIcon('pass'), '$(pass)');
assert.equal(gateIcon('unknown'), '$(question)');

// Absent measures are never zero.
assert.equal(formatMetric(undefined, '%'), 'Non disponible');
assert.equal(formatMetric(null), 'Non disponible');
assert.equal(formatMetric(Number.NaN), 'Non disponible');
assert.equal(formatMetric(Number.POSITIVE_INFINITY), 'Non disponible');
assert.equal(formatMetric(0, '%'), '0%');
assert.equal(formatMetric(0.001, '%'), '<0.01%');
assert.equal(formatMetric(12.345, '%'), '12.35%');

assert.equal(measurementLabel('measured'), 'Mesuré');
assert.equal(measurementLabel('failed'), 'Mesure en échec');
assert.equal(measurementLabel('absent'), 'Absent');
assert.equal(measurementLabel(undefined), 'Non mesuré');

assert.equal(freshness(undefined, 'abcdef1234', 0), 'Aucune analyse pour cette branche');
assert.equal(freshness('abcdef1234', 'abcdef1234', 0), 'À jour avec le HEAD local');
assert.equal(freshness('0000000000', 'abcdef1234', 1), '1 commit local plus récent que l’analyse');
assert.equal(freshness('0000000000', 'abcdef1234', 4), '4 commits locaux plus récents que l’analyse');
assert.equal(freshness('0000000000', 'abcdef1234', undefined), 'Commit analysé différent du HEAD local');

console.log('measure checks passed');

});
