import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { encode, FrameReader, METHODS, VERSION } from '../dist/host.mjs';

/**
 * Speaks the real framed protocol to the bundled host: a renamed method would
 * leave Visual Studio talking to a process that never answers.
 */

const host = fileURLToPath(new URL('../dist/host.mjs', import.meta.url));
const cacheDir = await mkdtemp(join(tmpdir(), 'codeinsights-vs-'));
const child = spawn(process.execPath, [host], { stdio: ['pipe', 'pipe', 'pipe'] });

const reader = new FrameReader();
const pending = new Map();
let nextId = 1;

child.stdout.on('data', (chunk) => {
  for (const message of reader.push(chunk)) {
    if (!('id' in message)) continue;
    const wait = pending.get(message.id);
    if (wait === undefined) continue;
    pending.delete(message.id);
    wait(message);
  }
});

function call(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`délai dépassé pour ${method}`)), 10_000);
    pending.set(id, (message) => {
      clearTimeout(timer);
      resolve(message);
    });
    child.stdin.write(encode({ jsonrpc: '2.0', id, method, params }));
  });
}

try {
  assert.deepEqual(
    [...METHODS].sort(),
    [
      'analyzeChanges',
      'clearIssueFilters',
      'clearRuleFilters',
      'fileContext',
      'filterIssues',
      'filterRules',
      'getSnapshot',
      'hubUrl',
      'initialize',
      'installHook',
      'prompt',
      'refresh',
      'selectIssue',
      'setCwd',
      'setSettings',
      'setToken',
      'shutdown',
      'triage',
    ],
  );

  const unknown = await call('not-a-method', {});
  assert.equal(unknown.error?.code, -32601);

  const initialized = await call('initialize', {
    cwd: process.cwd(),
    cacheDir,
    settings: {
      serverUrl: '',
      projectKey: '',
      refreshIntervalSeconds: 0,
      diagnosticsEnabled: true,
      liveAnalysisEnabled: false,
      liveAnalysisTargetBranch: 'origin/master',
      liveAnalysisSemgrepConfig: ['.codeinsights/semgrep-mqr-v1.json'],
      liveAnalysisUseDocker: false,
      liveAnalysisImages: { semgrep: 'semgrep/semgrep:1.145.0', gitleaks: 'zricethezav/gitleaks:v8.30.0' },
      liveAnalysisTimeoutSeconds: 120,
      liveAnalysisMaxFiles: 100,
      coverageEnabled: true,
      coverageLocalLcov: '',
      duplicationEnabled: true,
      dependenciesEnabled: true,
    },
  });
  assert.equal(initialized.error, undefined, initialized.error?.message);
  assert.equal(initialized.result.report.kind, 'message');
  assert.match(initialized.result.report.message, /URL du hub/);
  assert.equal(initialized.result.signedIn, false);
  assert.ok(Array.isArray(initialized.result.reportNodes));
  assert.equal(initialized.result.reportNodes[0].label, 'URL du hub non configurée');

  const snapshot = await call('getSnapshot', {});
  assert.equal(snapshot.result.report.kind, 'message');

  child.stdin.write(encode({ jsonrpc: '2.0', method: 'shutdown' }));
  await new Promise((resolve) => child.once('exit', resolve));
} finally {
  if (!child.killed) child.kill();
  await rm(cacheDir, { recursive: true, force: true });
}

console.log(`protocol checks passed (codeinsights-visualstudio ${VERSION}, ${METHODS.length} méthodes)`);
