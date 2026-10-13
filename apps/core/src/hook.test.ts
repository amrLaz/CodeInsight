import { test } from 'vitest';
import assert from 'node:assert/strict';

test('hook', async () => {

const { alreadyInstalled, HOOK_MARKER, prePushHook } = await import('./local/hook');

const script = prePushHook('CodeInsights : analyser mes changements');
assert.ok(script.startsWith('#!/bin/sh'));
assert.ok(script.includes(HOOK_MARKER));
assert.ok(script.includes('exit 0'), 'the hook must not block the push');
assert.equal(script.includes('git push'), false);
assert.equal(alreadyInstalled(undefined), false);
assert.equal(alreadyInstalled('#!/bin/sh\nexit 0\n'), false);
assert.equal(alreadyInstalled(script), true);

console.log('hook checks passed');

});
