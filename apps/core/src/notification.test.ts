import { test } from 'vitest';
import assert from 'node:assert/strict';

test('notification', async () => {

const { noticeFor } = await import('./report/notification');

// The first read of a session never notifies: the report was already there.
assert.deepEqual(noticeFor(undefined, { analysisId: 'a1' }), { notify: false });
assert.deepEqual(noticeFor({ analysisId: 'a1' }, { analysisId: 'a1' }), { notify: false });
assert.deepEqual(noticeFor({ analysisId: 'a1' }, { analysisId: 'a2' }), { notify: true, reason: 'new-analysis' });
assert.deepEqual(noticeFor({ analysisId: 'a1' }, undefined), { notify: false });
assert.deepEqual(noticeFor(undefined, undefined), { notify: false });

console.log('notification checks passed');

});
