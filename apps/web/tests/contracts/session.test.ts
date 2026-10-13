import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('session contracts', async () => {
// Exercise the actual App callbacks with state updates held until the batch ends.
const source = ts.createSourceFile('App.tsx', readFileSync(new URL('./App.tsx', sourceUrl), 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const app = source.statements.find(node => ts.isFunctionDeclaration(node) && node.name?.text === 'App');
assert.ok(app);
const { outputText } = ts.transpileModule(app.getText(source).replace('export ', ''), {
  compilerOptions: { jsx: ts.JsxEmit.React, target: ts.ScriptTarget.ES2020 },
});
let state;
let stored = JSON.stringify({ token: 'old' });
const pending = [];
const effects = [];
const events = new EventTarget();
const tree = runInNewContext(`${outputText}\nApp()`, {
  useState: initialize => {
    state = initialize();
    return [state, update => pending.push(update)];
  },
  useRef: current => ({ current }),
  useEffect: effect => effects.push(effect),
  sessionEvents: events,
  LOCAL_STORAGE_SESSION_KEY: 'session',
  localStorage: { getItem: () => stored, removeItem: () => { stored = null; } },
  React: { createElement: (_component, props) => props },
  SessionApp: () => {},
});
const cleanup = effects[0]();
const expire = token => events.dispatchEvent(new CustomEvent('expired', { detail: token }));
const flush = () => { for (const update of pending.splice(0)) state = update(state); };
tree.onSessionChange({ token: 'new' });
stored = JSON.stringify({ token: 'new' });
expire('old');
flush();
assert.equal(state.user.token, 'new', 'A late 401 must not cancel a newly queued identity');
assert.equal(JSON.parse(stored).token, 'new', 'A late 401 must preserve the new stored identity');
expire('new');
expire('new');
assert.equal(pending.length, 1, 'Concurrent 401 responses expire an identity only once');
flush();
assert.equal(state.user, null);
assert.equal(stored, null);
cleanup();
console.log('Session identity race and expiry checks passed.');
});
