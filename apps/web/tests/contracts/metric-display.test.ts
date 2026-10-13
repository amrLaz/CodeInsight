import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('metric display contracts', async () => {
// Use the builder's existing TypeScript compiler so the check also runs on Node 20.
const { outputText } = ts.transpileModule(readFileSync(new URL('./utils/metric-display.ts', sourceUrl), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 },
});
const { analysisGateClass, analysisGateLabel, comparableVersions, completenessLabel, downsampleMetrics, findingCategory, findingCategoryLabel, formatMetric, gateConditionValue, gateLabel, measurementStateLabel, scopedMetrics, metricPoint, thresholdRatio, trendTimeX } = await import(
  `data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`
);

const versioned = { profile_id: 'dotnet', profile_version: '1', formula_version: 'formula/1' };
assert.equal(comparableVersions(versioned, { ...versioned }), true);
assert.equal(comparableVersions(versioned, { ...versioned, formula_version: 'formula/2' }), false, 'Formula changes must break trend segments');
assert.equal(comparableVersions(versioned, { ...versioned, profile_id: 'react' }), false);
assert.equal(comparableVersions({}, {}), false, 'Missing versions do not prove comparability');
assert.equal(comparableVersions({ ...versioned, profile_version: '0' }, { ...versioned, profile_version: '0' }), false);

assert.equal(formatMetric(undefined, '%'), 'Not available');
assert.equal(formatMetric(null), 'Not available');
assert.equal(formatMetric(Number.NaN), 'Not available');
assert.equal(formatMetric(0, '%'), '0%');
assert.equal(formatMetric(0.019500007617190476, '%'), '0.02%');
assert.equal(formatMetric(0.001, '%'), '<0.01%');
assert.equal(gateLabel('unknown'), 'Unknown');
assert.equal(gateLabel(undefined), 'Unknown');
assert.equal(gateLabel('pass'), 'Pass');
assert.equal(gateLabel('fail'), 'Fail');
assert.equal(analysisGateLabel('pass', 'complete'), 'Pass');
assert.equal(analysisGateLabel('pass', undefined), 'Unknown');
assert.equal(analysisGateClass('pass', undefined), 'unknown');
assert.equal(measurementStateLabel('measured'), 'Measured');
assert.equal(measurementStateLabel('failed'), 'Measurement failed');
assert.equal(measurementStateLabel(undefined), 'Not measured');
assert.equal(completenessLabel('complete'), 'Complete');
assert.equal(completenessLabel(undefined), 'Not available');
assert.equal(findingCategory('dependency'), 'dependency');
assert.equal(findingCategory('code_smell'), 'code_smell');
assert.equal(findingCategoryLabel('vulnerability'), 'Vulnerability');
assert.equal(findingCategory('dependency_vulnerability'), 'unknown');
assert.equal(findingCategory(undefined), 'unknown');
assert.equal(findingCategoryLabel('ioc'), 'IOC');
assert.deepEqual(scopedMetrics({ coverage: 0 }, 'overall'), { coverage: 0 });
assert.deepEqual(scopedMetrics({ coverage: 95, quality_gate: 'pass' }, 'new-code'), {});
assert.equal(metricPoint(undefined, 1, 3, 100), null);
assert.equal(metricPoint(0, 1, 3, 100).y, 140);
assert.equal(metricPoint(200, 2, 3, 200).y, 10);
const history = Array.from({ length: 100 }, (_, index) => ({ first: index === 63 ? 1_000 : index, second: 100 - index }));
const sampled = downsampleMetrics(history, 12, (item) => [item.first, item.second]);
assert.equal(sampled.length, 12);
assert.deepEqual([sampled[0].index, sampled.at(-1).index], [0, 99], 'History keeps its first and last analyses');
assert(sampled.some(({ index }) => index === 63), 'History keeps metric extrema instead of hiding a spike');
assert.equal(new Set(sampled.map(({ index }) => index)).size, sampled.length, 'History samples are unique');
assert.throws(() => downsampleMetrics(history, 1, (item) => [item.first]), RangeError);
assert.equal(trendTimeX(1000, 1000, 3000, 180), 8, 'Time scale starts at the chart origin');
assert.equal(trendTimeX(2000, 1000, 3000, 180), 180, 'Time scale reflects elapsed time');
assert.equal(trendTimeX(3000, 1000, 3000, 180), 352, 'Time scale ends at the chart bound');
assert.equal(trendTimeX(1000, 1000, 1000, 180), 180, 'A single timestamp keeps its fallback position');
assert.equal(thresholdRatio(5, 10), 50, 'Gate bars show the measured-to-threshold ratio');
assert.equal(thresholdRatio(23, 10), 100, 'Gate bars remain bounded when a maximum is exceeded');
assert.equal(thresholdRatio(1, 0), 100, 'A positive value fully exceeds a zero threshold');
assert.equal(
  gateConditionValue({ metric: 'coverage', operator: '>=', threshold: 80, value: null, state: 'not_measured' }),
  'Not measured · required minimum: 80%',
  'An unknown coverage condition is explained without exposing its raw storage format',
);
assert.equal(
  gateConditionValue({ metric: 'vulnerabilities', operator: '<=', threshold: 0, value: 3, state: 'measured' }),
  '3 · allowed maximum: 0',
);
console.log('Metric display checks passed.');
});
