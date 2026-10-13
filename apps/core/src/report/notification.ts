/**
 * Decides when a finished analysis is worth interrupting the developer. Only a new
 * analysis of the branch they are on, and never the first read of a session: a
 * notification for a report that was already there would train them to ignore them.
 */

export type Seen = { analysisId: string } | undefined;

export type NoticeDecision = { notify: false } | { notify: true; reason: 'new-analysis' };

export function noticeFor(previous: Seen, current: { analysisId: string } | undefined): NoticeDecision {
  if (current === undefined || previous === undefined) return { notify: false };
  return previous.analysisId === current.analysisId ? { notify: false } : { notify: true, reason: 'new-analysis' };
}
