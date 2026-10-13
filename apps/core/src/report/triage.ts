import type { DurableFinding, FindingUpdate } from '../api/collaboration';
import type { Issue } from './issues';

/**
 * Triage rules mirrored from the hub so a refusal is explained before the request
 * instead of after it. The API remains the authority: nothing here grants a right,
 * it only avoids sending a call that is already known to be invalid.
 */

export type State = 'open' | 'confirmed' | 'resolved' | 'false_positive' | 'accepted_risk';

export const STATES: State[] = ['open', 'confirmed', 'resolved', 'false_positive', 'accepted_risk'];

/** States the hub treats as exceptions: administrator review plus a bounded expiry. */
export function isException(state: string): boolean {
  return state === 'false_positive' || state === 'accepted_risk';
}

export function stateLabel(state: string): string {
  switch (state) {
    case 'open':
      return 'Ouverte';
    case 'confirmed':
      return 'Confirmée';
    case 'resolved':
      return 'Résolue';
    case 'false_positive':
      return 'Faux positif';
    case 'accepted_risk':
      return 'Risque accepté';
    default:
      return state;
  }
}

/**
 * States the actor may actually reach. Exceptions are an administrator decision, and a
 * finding already under exception cannot be moved at all by anyone else: offering the
 * action would only produce a refusal. The server still decides — this only avoids
 * showing a dead control.
 */
export function availableStates(isAdmin: boolean, currentState: string): State[] {
  if (isAdmin) return [...STATES];
  if (isException(currentState)) return [];
  return ['open', 'confirmed', 'resolved'];
}

export type Change = {
  state?: State;
  assigneeUserId?: string | null;
  teamId?: string | null;
  hotspotReview?: string;
  reason: string;
  expiresAt?: string | null;
};

/** A reason is required by the hub whenever the review itself changes. */
export function requiresReason(finding: DurableFinding, change: Change): boolean {
  const state = change.state ?? finding.state;
  const review = change.hotspotReview ?? finding.hotspot_review ?? 'not_reviewed';
  return state !== finding.state || review !== (finding.hotspot_review ?? 'not_reviewed') || isException(state) || isException(finding.state);
}

export function requiresExpiry(state: string): boolean {
  return isException(state);
}

/** Expiry the hub accepts: strictly in the future and at most one year away. */
export function validExpiry(value: string | undefined, now: number): boolean {
  if (value === undefined) return false;
  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) return false;
  return parsed > now && parsed <= now + 365 * 24 * 60 * 60 * 1000;
}

export function updateBody(finding: DurableFinding, change: Change): FindingUpdate {
  const state = change.state ?? (finding.state as State);
  const body: FindingUpdate = {
    version: finding.version,
    state,
    hotspot_review: change.hotspotReview ?? finding.hotspot_review ?? 'not_reviewed',
    assignee_user_id: change.assigneeUserId === undefined ? (finding.assignee_user_id ?? null) : change.assigneeUserId,
    team_id: change.teamId === undefined ? (finding.team_id ?? null) : change.teamId,
    reason: change.reason,
  };
  // The hub only stores an expiry for the two exception states; sending one otherwise is refused.
  if (isException(state)) body.expires_at = change.expiresAt ?? finding.exception_expires_at ?? null;
  return body;
}

/** Turns an API refusal into what the developer should do about it. */
export function triageError(status: number, detail: string): string {
  if (status === 403 && detail.toLowerCase().includes('personal access token'))
    return 'Ce jeton est en lecture seule. Créer un jeton avec « Allow issue triage » depuis le hub.';
  if (status === 403 && detail.toLowerCase().includes('exception'))
    return "Les exceptions (faux positif, risque accepté) demandent une revue d'administrateur.";
  if (status === 403) return `Action refusée par le hub : ${detail}`;
  if (status === 409) return 'Cette issue a changé entre-temps. Rafraîchir puis recommencer.';
  if (status === 410) return "Les détails de cette analyse ont été purgés.";
  return detail;
}

/** Prompt built from the working tree when the hub no longer holds the analysed source. */
export function localPrompt(issue: Issue, line: number | undefined, lines: string[], context: number): string {
  const anchor = line ?? 0;
  const start = Math.max(anchor - context, 1);
  const end = Math.min(anchor + context, lines.length);
  const snippet: string[] = [];
  for (let index = start; index <= end; index += 1) snippet.push(`${String(index).padStart(5)}: ${lines[index - 1] ?? ''}`);
  return [
    'You are fixing an issue detected by CodeInsights.',
    '',
    'Rule:',
    issue.rule,
    '',
    'Severity:',
    issue.severity,
    '',
    'Issue:',
    issue.message,
    '',
    'File:',
    issue.path ?? 'unknown',
    '',
    'Line:',
    String(anchor),
    '',
    'Relevant source code (from the local working tree, not from the analysed revision):',
    snippet.join('\n'),
    '',
    'Fix the issue while minimizing unrelated changes.',
  ].join('\n');
}

/** Why the hub cannot provide its own prompt, in the developer's terms. */
export function promptUnavailable(reason: string | undefined): string {
  switch (reason) {
    case 'source_purged':
      return 'la source analysée a été purgée';
    case 'source_unavailable':
      return "le producteur n'a pas transmis cette source";
    case 'location_unavailable':
      return "l'issue n'a pas de localisation";
    default:
      return 'la source analysée est indisponible';
  }
}
