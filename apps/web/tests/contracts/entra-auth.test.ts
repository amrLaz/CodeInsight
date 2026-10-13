import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('entra auth contracts', async () => {
const source = readFileSync(new URL('./entra-auth.ts', sourceUrl), 'utf8');
const { outputText } = ts.transpileModule(source.replaceAll('export ', ''), { compilerOptions: { module: ts.ModuleKind.None, target: ts.ScriptTarget.ES2020 } });
const context = {};
runInNewContext(outputText, context);
const base = { signedIn: false, hasCallback: false, entraEnabled: true, autoSignInEnabled: true, claimed: false, attemptedInTab: false, suppressedInTab: false };
assert.equal(context.shouldAttemptSilentEntra(base), true, 'Anonymous first arrival starts silent Entra SSO');
for (const blocked of ['signedIn', 'hasCallback', 'claimed', 'attemptedInTab', 'suppressedInTab'])
  assert.equal(context.shouldAttemptSilentEntra({ ...base, [blocked]: true }), false, `${blocked} prevents a silent SSO loop`);
assert.equal(context.shouldAttemptSilentEntra({ ...base, entraEnabled: false }), false);
assert.equal(context.shouldAttemptSilentEntra({ ...base, autoSignInEnabled: false }), false);

const app = readFileSync(new URL('./workspace/Workspace.tsx', sourceUrl), 'utf8');
assert.match(app, /entra\/login\?mode=\$\{mode\}/, 'Interactive and silent login modes are explicit');
assert.match(app, /entraSilent === 'unavailable'/, 'Expected prompt=none failure returns quietly');
assert.match(app, /sessionStorage\.setItem\(ENTRA_AUTO_SUPPRESSED_KEY/, 'Voluntary sign-out suppresses auto SSO in the current tab');
assert.doesNotMatch(app, /decodeURIComponent\(entraError\)/, 'OIDC errors are not decoded twice');
console.log('Entra silent SSO checks passed.');
});
