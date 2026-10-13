export type WorkItemState = 'pending' | 'created' | 'failed';
export type WorkItemOrigin = 'manual' | 'severity_rule' | 'sla_rule';
export type FindingWorkItem = {
  finding_id: string;
  state: WorkItemState;
  work_item_id: number | null;
  url: string | null;
  origin: WorkItemOrigin;
  requested_by: string;
  requested_at: string;
  created_at: string | null;
  last_error: string | null;
};
export type WorkItemSettings = {
  project_key: string;
  work_item_type: string;
  area_path: string | null;
  iteration_path: string | null;
  auto_min_severity: string | null;
  auto_on_sla_breach: boolean;
  auto_max_per_run: number;
  auto_since: string | null;
  version: number;
  updated_by: string;
  updated_at: string;
};
export type WorkItemAvailability = { enabled: boolean; reason: string | null; organization_url: string | null; ado_project: string | null };

export const WORK_ITEM_ORIGINS: Record<WorkItemOrigin, string> = {
  manual: 'requested manually',
  severity_rule: 'created by the severity rule',
  sla_rule: 'created by the SLA breach rule',
};

export const AUTO_SEVERITIES = [
  { value: '', label: 'Off' },
  { value: 'blocker', label: 'Blocker only' },
  { value: 'high', label: 'High and above' },
  { value: 'medium', label: 'Medium and above' },
  { value: 'low', label: 'Low and above' },
  { value: 'info', label: 'Every severity' },
] as const;

/** One line describing the link; a failure keeps Azure DevOps' own explanation. */
export function workItemSummary(item: FindingWorkItem): string {
  if (item.state === 'created') return `Work item #${item.work_item_id}, ${WORK_ITEM_ORIGINS[item.origin] ?? item.origin}.`;
  if (item.state === 'pending') return `Creation queued, ${WORK_ITEM_ORIGINS[item.origin] ?? item.origin}.`;
  return item.last_error ? `Creation failed: ${item.last_error}` : 'Creation failed.';
}

/** Whole runs from 1 to 100; anything else is invalid rather than silently clamped. */
export function parseMaxPerRun(value: string): number | undefined {
  const text = value.trim();
  if (!/^\d+$/.test(text)) return undefined;
  const count = Number(text);
  return count >= 1 && count <= 100 ? count : undefined;
}
