import { test } from 'vitest';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('severity-colors', async () => {

/**
 * The core names the colour of a severity; only this manifest can make it paint.
 * A `codeinsights.*` id absent from `contributes.colors` renders as the default
 * foreground, so High would silently look like Info.
 */
const { severityColor } = await import('../../core/src/report/issues');

const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const contributed = new Set((manifest.contributes.colors ?? []).map((colour) => colour.id));
for (const severity of ['blocker', 'high', 'medium', 'low', 'info']) {
  const colour = severityColor(severity);
  if (!colour.startsWith('codeinsights.')) continue;
  assert.equal(contributed.has(colour), true, `${colour} n'est pas contribuée par package.json`);
}
assert.equal(severityColor('high'), 'codeinsights.severityHigh');
assert.equal(contributed.has('codeinsights.severityHigh'), true);

console.log('severity colour checks passed');

});
