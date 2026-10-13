import type { CodeInsightsApi } from './client';

/**
 * Issue triage and AI context. Durable findings carry a version: every write sends the
 * version it read, so a concurrent decision produces a 409 instead of overwriting it.
 */

export type DurableFinding = {
  id: string;
  project_key: string;
  branch: string;
  category?: string;
  state: string;
  effective_state?: string;
  hotspot_review?: string;
  assignee_user_id?: string | null;
  team_id?: string | null;
  exception_reason?: string | null;
  exception_expires_at?: string | null;
  version: number;
};

export type FindingUpdate = {
  version: number;
  state: string;
  hotspot_review: string;
  assignee_user_id: string | null;
  team_id: string | null;
  reason: string;
  expires_at?: string | null;
};

export type Person = { user_id: string; email?: string; full_name?: string };

/** Resolves the durable finding behind an occurrence, adopting it if never observed. */
export async function durableFinding(api: CodeInsightsApi, occurrenceId: number): Promise<DurableFinding> {
  return api.json<DurableFinding>(`/api/v1/collaboration/occurrences/${occurrenceId}`, { method: 'POST' });
}

export async function updateFinding(api: CodeInsightsApi, id: string, body: FindingUpdate): Promise<DurableFinding> {
  return api.json<DurableFinding>(`/api/v1/collaboration/findings/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function commentFinding(api: CodeInsightsApi, id: string, version: number, reason: string): Promise<DurableFinding> {
  return api.json<DurableFinding>(`/api/v1/collaboration/findings/${encodeURIComponent(id)}/comments`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ version, reason }),
  });
}

export async function people(api: CodeInsightsApi, projectKey: string): Promise<Person[]> {
  return api.json<Person[]>(`/api/v1/collaboration/people?project_key=${encodeURIComponent(projectKey)}`);
}

/** Prompt built by the hub from the source it stored at the analysed revision. */
export async function issuePrompt(api: CodeInsightsApi, occurrenceId: number): Promise<string> {
  const body = await api.json<{ prompt?: string }>(`/api/v1/issues/${occurrenceId}/prompt`);
  return body.prompt ?? '';
}
