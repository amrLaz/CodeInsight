import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defaultSettings, Session } from '../dist/host.mjs';

/**
 * The host must never invent a green gate: without a hub URL, a token or a Git
 * branch, the snapshot is a message, not a report.
 */

const cacheDir = await mkdtemp(join(tmpdir(), 'codeinsights-vs-session-'));
const logs = [];
const session = new Session((level, message) => logs.push(`${level}:${message}`));

const missingUrl = await session.initialize({
  cwd: process.cwd(),
  cacheDir,
  settings: { ...defaultSettings(), serverUrl: '', refreshIntervalSeconds: 0 },
});
assert.equal(missingUrl.report.kind, 'message');
assert.match(missingUrl.report.message, /URL du hub/);
assert.equal(missingUrl.signedIn, false);
assert.equal(
  missingUrl.reportNodes.some((node) => node.color === 'charts.green' || node.icon === 'pass'),
  false,
  'une URL absente ne doit pas peindre un gate vert',
);

const missingToken = await session.initialize({
  cwd: process.cwd(),
  cacheDir,
  settings: { ...defaultSettings(), serverUrl: 'https://codeinsights.example', refreshIntervalSeconds: 0 },
});
assert.equal(missingToken.report.kind, 'message');
assert.match(missingToken.report.message, /Non connecté/);
assert.equal(missingToken.status.stale, false);

const file = await session.fileContext(join(process.cwd(), 'README.md'));
assert.equal(file.diagnostics.length, 0);
assert.equal(file.coverage.length, 0);
assert.equal(
  file.diagnostics.some((item) => item.message.includes('0') && item.source === 'CodeInsights'),
  false,
);

await rm(cacheDir, { recursive: true, force: true });
console.log('session checks passed');
