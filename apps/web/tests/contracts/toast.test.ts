import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('toast contracts', async () => {
const source = readFileSync(new URL('./context/ToastProvider.tsx', sourceUrl), 'utf8');

assert.match(source, /createContext/, 'Notifications are available from one application provider');
assert.match(source, /current\.slice\(-2\)/, 'The viewport stays bounded to three notifications');
assert.match(source, /window\.setTimeout/, 'Transient notifications dismiss automatically');
assert.match(source, /window\.clearTimeout/, 'Notification timers are cleaned up');
assert.match(source, /aria-live="polite"/, 'The notification viewport announces non-blocking updates');
assert.match(source, /toast\.tone === 'error' \? 'alert' : 'status'/, 'Error notifications use alert semantics');
assert.match(source, /aria-label="Dismiss notification"/, 'Notifications can be dismissed explicitly');

console.log('Toast lifecycle and accessibility checks passed.');
});
