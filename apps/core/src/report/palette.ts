/**
 * Colour code shared by the views. A colour only carries a semantic the analysis
 * actually published — verdict, rating, condition result, measurement state — never a
 * judgment invented on a raw count. `charts.*` and `descriptionForeground` are the ids
 * themes guarantee to keep readable; `codeinsights.*` are contributed by package.json
 * so a theme or a user setting can repaint them.
 */

export const palette = {
  pass: 'charts.green',
  fail: 'charts.red',
  warn: 'charts.orange',
  watch: 'charts.yellow',
  info: 'charts.blue',
  /** Deep red of the High criticality, contributed so it stays distinct from the Blocker red. */
  high: 'codeinsights.severityHigh',
  /** Anything unknown or not measured: muted, never a colour that suggests a result. */
  muted: 'descriptionForeground',
} as const;

/** Colour of a A-E rating: the scale of the hub, from the best grade to the worst. */
export function ratingColor(grade: string): string {
  switch (grade.trim().toUpperCase()) {
    case 'A':
      return palette.pass;
    case 'B':
      return palette.info;
    case 'C':
      return palette.watch;
    case 'D':
      return palette.warn;
    case 'E':
      return palette.high;
    default:
      return palette.muted;
  }
}
