/**
 * Presentation rules shared by every surface of the extension. The hub never
 * replaces an absent measure by zero (see docs/integration.md); the IDE must not
 * either, so formatting lives here rather than in each view.
 */

export type MeasurementState = 'measured' | 'absent' | 'failed' | 'not_measured';
export type GateVerdict = 'pass' | 'fail' | 'unknown';
export type Completeness = 'complete' | 'incomplete' | 'unknown';

export function measurementLabel(state: MeasurementState | undefined): string {
  switch (state) {
    case 'measured':
      return 'Mesuré';
    case 'absent':
      return 'Absent';
    case 'failed':
      return 'Mesure en échec';
    default:
      return 'Non mesuré';
  }
}

/** A verdict is only trustworthy when the analysis is complete; anything else is unknown. */
export function gateVerdict(verdict: string | undefined, completeness: string | undefined): GateVerdict {
  if (completeness !== 'complete') return 'unknown';
  return verdict === 'pass' || verdict === 'fail' ? verdict : 'unknown';
}

export function gateLabel(verdict: GateVerdict): string {
  return verdict === 'pass' ? 'Pass' : verdict === 'fail' ? 'Fail' : 'Unknown';
}

/** Status bar icon: never green unless the hub actually produced a complete pass. */
export function gateIcon(verdict: GateVerdict): string {
  return verdict === 'pass' ? '$(pass)' : verdict === 'fail' ? '$(error)' : '$(question)';
}

/** Formats a metric without inventing a value; `0` is a measured value, `null` is not. */
export function formatMetric(value: number | null | undefined, unit = ''): string {
  if (value === null || value === undefined || Number.isNaN(value)) return 'Non disponible';
  if (!Number.isFinite(value)) return 'Non disponible';
  const rounded = Math.round(value * 100) / 100;
  if (rounded === 0 && value !== 0) return `<0.01${unit}`;
  return `${rounded}${unit}`;
}

/** Describes how far the working tree has drifted from the analysed commit. */
export function freshness(analysedSha: string | undefined, headSha: string | undefined, aheadCount: number | undefined): string {
  if (analysedSha === undefined || analysedSha.length === 0) return 'Aucune analyse pour cette branche';
  if (headSha !== undefined && analysedSha.startsWith(headSha.slice(0, 7))) return 'À jour avec le HEAD local';
  if (aheadCount === undefined) return 'Commit analysé différent du HEAD local';
  return aheadCount === 1 ? '1 commit local plus récent que l’analyse' : `${aheadCount} commits locaux plus récents que l’analyse`;
}
