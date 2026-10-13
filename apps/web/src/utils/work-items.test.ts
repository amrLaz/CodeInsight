import { describe, expect, it } from 'vitest';
import { parseMaxPerRun, workItemSummary, type FindingWorkItem } from './work-items';

const item = (patch: Partial<FindingWorkItem>): FindingWorkItem => ({
  finding_id: 'f',
  state: 'created',
  work_item_id: 42,
  url: 'https://dev.azure.com/up-group/Quality/_workitems/edit/42',
  origin: 'manual',
  requested_by: 'dev@up.coop',
  requested_at: '2026-09-29T10:00:00Z',
  created_at: '2026-09-29T10:00:05Z',
  last_error: null,
  ...patch,
});

describe('workItemSummary', () => {
  it('names the work item and how it was requested', () => {
    expect(workItemSummary(item({}))).toBe('Work item #42, requested manually.');
    expect(workItemSummary(item({ state: 'pending', work_item_id: null, origin: 'sla_rule' }))).toBe('Creation queued, created by the SLA breach rule.');
  });

  it("keeps Azure DevOps' explanation of a failure", () => {
    expect(workItemSummary(item({ state: 'failed', last_error: 'HTTP 403' }))).toBe('Creation failed: HTTP 403');
    expect(workItemSummary(item({ state: 'failed', last_error: null }))).toBe('Creation failed.');
  });
});

describe('parseMaxPerRun', () => {
  it('accepts whole runs from 1 to 100 only', () => {
    expect(parseMaxPerRun('10')).toBe(10);
    expect(parseMaxPerRun('0')).toBeUndefined();
    expect(parseMaxPerRun('101')).toBeUndefined();
    expect(parseMaxPerRun('')).toBeUndefined();
    expect(parseMaxPerRun('2.5')).toBeUndefined();
  });
});
