import { test } from 'vitest';
import assert from 'node:assert/strict';

test('triage', async () => {

const {
  availableStates,
  isException,
  localPrompt,
  promptUnavailable,
  requiresExpiry,
  requiresReason,
  stateLabel,
  triageError,
  updateBody,
  validExpiry,
  STATES,
} = await import('./report/triage');
const { toIssue } = await import('./report/issues');

const finding = (over) => ({
  id: 'f1',
  project_key: 'demo',
  branch: 'main',
  state: 'open',
  hotspot_review: 'not_reviewed',
  assignee_user_id: null,
  team_id: null,
  version: 3,
  ...over,
});

assert.deepEqual([...STATES], ['open', 'confirmed', 'resolved', 'false_positive', 'accepted_risk']);
assert.equal(isException('false_positive'), true);
assert.equal(isException('accepted_risk'), true);
assert.equal(isException('resolved'), false);
assert.equal(stateLabel('accepted_risk'), 'Risque accepté');
assert.equal(stateLabel('unknown-from-server'), 'unknown-from-server');

// A reason is required whenever the review changes, and exceptions always require one.
assert.equal(requiresReason(finding(), { reason: '' }), false, 'a plain assignment needs no reason');
assert.equal(requiresReason(finding(), { state: 'confirmed', reason: '' }), true);
assert.equal(requiresReason(finding({ category: 'security_hotspot' }), { hotspotReview: 'safe', reason: '' }), true);
assert.equal(requiresReason(finding({ state: 'accepted_risk' }), { reason: '' }), true, 'leaving an exception is a review');
assert.equal(requiresExpiry('false_positive'), true);
assert.equal(requiresExpiry('resolved'), false);

const now = Date.parse('2026-09-10T00:00:00Z');
assert.equal(validExpiry(undefined, now), false);
assert.equal(validExpiry('not a date', now), false);
assert.equal(validExpiry('2026-09-09T00:00:00Z', now), false, 'the past is refused');
assert.equal(validExpiry('2026-12-31T00:00:00Z', now), true);
assert.equal(validExpiry('2028-01-01T00:00:00Z', now), false, 'beyond one year is refused');

// The version read is sent back, so a concurrent decision produces a 409 rather than a silent overwrite.
const body = updateBody(finding({ assignee_user_id: 'u9' }), { state: 'confirmed', reason: 'Reproduit' });
assert.deepEqual(body, {
  version: 3,
  state: 'confirmed',
  hotspot_review: 'not_reviewed',
  assignee_user_id: 'u9',
  team_id: null,
  reason: 'Reproduit',
});
assert.equal('expires_at' in body, false, 'a non-exception state carries no expiry');
const exception = updateBody(finding(), { state: 'accepted_risk', reason: 'Accepté', expiresAt: '2026-12-31T00:00:00.000Z' });
assert.equal(exception.expires_at, '2026-12-31T00:00:00.000Z');
// An explicit null clears the assignee; an absent field keeps what the hub holds.
assert.equal(updateBody(finding({ assignee_user_id: 'u9' }), { assigneeUserId: null, reason: '' }).assignee_user_id, null);
assert.equal(updateBody(finding({ assignee_user_id: 'u9' }), { reason: '' }).assignee_user_id, 'u9');

// Exceptions belong to administrators, so the action is not offered to anyone else.
assert.deepEqual([...availableStates(true, 'open')], ['open', 'confirmed', 'resolved', 'false_positive', 'accepted_risk']);
assert.deepEqual([...availableStates(false, 'open')], ['open', 'confirmed', 'resolved']);
assert.deepEqual([...availableStates(false, 'confirmed')], ['open', 'confirmed', 'resolved']);
// A finding already under exception cannot be moved by a non-administrator at all.
assert.deepEqual([...availableStates(false, 'accepted_risk')], []);
assert.deepEqual([...availableStates(false, 'false_positive')], []);
assert.deepEqual([...availableStates(true, 'accepted_risk')].length, 5);

// Refusals are turned into the action the developer must take.
assert.ok(triageError(403, 'Personal access token scope does not allow this action').includes('Allow issue triage'));
assert.ok(triageError(403, 'Exceptions require administrator review').includes('administrateur'));
assert.ok(triageError(409, 'Finding changed; reload before updating').includes('Rafraîchir'));
assert.equal(triageError(422, 'A review reason is required'), 'A review reason is required');

// A locally built prompt says where its source comes from and never claims the analysed revision.
const issue = toIssue({ id: 1, path: 'src/app.ts', line: 3, severity: 'error', rule_id: 'r1', message: 'Boom' });
const prompt = localPrompt(issue, 3, ['one', 'two', 'three', 'four', 'five'], 1);
assert.ok(prompt.includes('from the local working tree, not from the analysed revision'));
assert.ok(prompt.includes('    2: two'));
assert.ok(prompt.includes('    4: four'));
assert.ok(!prompt.includes('    1: one'), 'context is bounded');
// A line at the edge of the file must not read past its bounds.
assert.ok(localPrompt(issue, 1, ['only'], 5).includes('    1: only'));
assert.ok(!localPrompt(issue, 1, ['only'], 5).includes('undefined'));

assert.equal(promptUnavailable('source_purged'), 'la source analysée a été purgée');
assert.equal(promptUnavailable('source_unavailable'), "le producteur n'a pas transmis cette source");
assert.equal(promptUnavailable(undefined), 'la source analysée est indisponible');

console.log('triage checks passed');

});
