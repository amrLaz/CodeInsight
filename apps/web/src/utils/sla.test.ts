import { describe, expect, it } from 'vitest';
import { parseSlaDays, policyDays, slaSummary, type SlaPolicy, type SlaView } from './sla';

const now = Date.parse('2026-09-29T12:00:00Z');
const view = (status: SlaView['status'], due: string | null, basis = 'applies'): SlaView => ({ status, basis, due_at: due, warn_at: null });

describe('slaSummary', () => {
  it('counts days before and after the deadline', () => {
    expect(slaSummary(view('on_track', '2026-10-09T12:00:00Z'), now)).toMatch(/in 10 days\.$/);
    expect(slaSummary(view('due_soon', '2026-09-30T12:00:00Z'), now)).toMatch(/in 1 day\.$/);
    expect(slaSummary(view('breached', '2026-09-27T12:00:00Z'), now)).toMatch(/2 days ago\.$/);
    expect(slaSummary(view('breached', '2026-09-29T12:00:00Z'), now)).toMatch(/today\.$/);
  });

  it('explains closed and suspended deadlines instead of counting them', () => {
    expect(slaSummary(view('closed', '2026-09-01T00:00:00Z'), now)).toMatch(/^Resolved/);
    expect(slaSummary(view('suspended', '2026-09-01T00:00:00Z'), now)).toMatch(/exception suspends/);
  });

  it('never invents a date when no deadline applies', () => {
    expect(slaSummary(view('indeterminate', null, 'unknown_detection'), now)).toBe('The first detection date is unknown: earlier analysis details were purged.');
    expect(slaSummary(view('not_applicable', null, 'not_main_branch'), now)).toBe('Deadlines only apply to the project main branch.');
    expect(slaSummary(view('indeterminate', null, 'something_new'), now)).toBe('No deadline could be established.');
  });
});

describe('parseSlaDays', () => {
  it('reads an empty field as no deadline, not as zero', () => {
    expect(parseSlaDays('')).toBeNull();
    expect(parseSlaDays('  ')).toBeNull();
  });

  it('accepts whole days within bounds only', () => {
    expect(parseSlaDays('30')).toBe(30);
    expect(parseSlaDays('0')).toBeUndefined();
    expect(parseSlaDays('3651')).toBeUndefined();
    expect(parseSlaDays('7.5')).toBeUndefined();
    expect(parseSlaDays('-1')).toBeUndefined();
  });
});

it('policyDays keeps a missing policy distinct from a delay', () => {
  const policy = { blocker_days: 7, info_days: null } as SlaPolicy;
  expect(policyDays(policy, 'blocker')).toBe(7);
  expect(policyDays(policy, 'info')).toBeNull();
  expect(policyDays(null, 'blocker')).toBeNull();
});
