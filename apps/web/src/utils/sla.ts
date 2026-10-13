export type SlaStatus = 'on_track' | 'due_soon' | 'breached' | 'suspended' | 'closed' | 'not_applicable' | 'not_configured' | 'indeterminate' | 'pending';
export type SlaView = { status: SlaStatus; basis: string; due_at: string | null; warn_at: string | null };
export type SlaSeverity = 'blocker' | 'high' | 'medium' | 'low' | 'info';
export type SlaPolicy = {
  scope_key: string;
  project_key: string | null;
  blocker_days: number | null;
  high_days: number | null;
  medium_days: number | null;
  low_days: number | null;
  info_days: number | null;
  version: number;
  updated_by: string;
  updated_at: string;
};

export const SLA_MAX_DAYS = 3650;
export const SLA_SEVERITIES: ReadonlyArray<{ value: SlaSeverity; label: string }> = [
  { value: 'blocker', label: 'Blocker' },
  { value: 'high', label: 'High' },
  { value: 'medium', label: 'Medium' },
  { value: 'low', label: 'Low' },
  { value: 'info', label: 'Info' },
];

export const SLA_STATUSES: Record<SlaStatus, string> = {
  breached: 'Breached',
  due_soon: 'Due soon',
  on_track: 'On track',
  suspended: 'Suspended by exception',
  closed: 'Closed',
  not_applicable: 'Not applicable',
  not_configured: 'Not configured',
  indeterminate: 'Indeterminate',
  pending: 'Being computed',
};

const SLA_REASONS: Record<string, string> = {
  not_main_branch: 'Deadlines only apply to the project main branch.',
  no_target: 'The policy sets no deadline for this severity.',
  no_policy: 'No SLA policy is configured.',
  unknown_severity: 'The scanner did not report a known severity.',
  unknown_detection: 'The first detection date is unknown: earlier analysis details were purged.',
  unknown_main_branch: 'The project main branch is not configured.',
  pending: 'The deadline will be computed by the next maintenance run.',
};

const DAY = 86_400_000;

/** Human wording of the deadline; a missing deadline is explained, never shown as a date or as zero. */
export function slaSummary(sla: SlaView, now = Date.now()): string {
  if (sla.basis !== 'applies') return SLA_REASONS[sla.basis] ?? 'No deadline could be established.';
  if (!sla.due_at) return 'No deadline could be established.';
  const due = Date.parse(sla.due_at);
  const date = new Date(due).toLocaleDateString();
  if (sla.status === 'closed') return `Resolved; the deadline was ${date}.`;
  if (sla.status === 'suspended') return `An active exception suspends the deadline of ${date}.`;
  const days = Math.round((due - now) / DAY);
  if (days < 0) return `Due ${date}, ${-days} day${days === -1 ? '' : 's'} ago.`;
  if (days === 0) return `Due ${date}, today.`;
  return `Due ${date}, in ${days} day${days === 1 ? '' : 's'}.`;
}

/** Empty means "no deadline for this severity"; anything else must be a whole number of days within bounds. */
export function parseSlaDays(value: string): number | null | undefined {
  const text = value.trim();
  if (!text) return null;
  if (!/^\d+$/.test(text)) return undefined;
  const days = Number(text);
  return days >= 1 && days <= SLA_MAX_DAYS ? days : undefined;
}

export function policyDays(policy: SlaPolicy | null | undefined, severity: SlaSeverity): number | null {
  return policy ? policy[`${severity}_days`] : null;
}
