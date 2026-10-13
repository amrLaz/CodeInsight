import type { Issue } from './issues';
import { severityLabel } from './issues';
import { anchorNote, type Anchor } from './drift';

/**
 * Turns an issue plus its anchor into what the Problems panel shows. Kept free of the
 * extension host so the mapping, the wording and the drift note are asserted directly.
 */

export type DiagnosticSeverity = 'error' | 'warning' | 'information' | 'hint';

export type DiagnosticSpec = {
  line: number;
  severity: DiagnosticSeverity;
  message: string;
  code: string;
  documentationUrl: string | undefined;
  unnecessary: boolean;
};

export function diagnosticSeverity(issue: Issue): DiagnosticSeverity {
  if (issue.excepted) return 'hint';
  switch (issue.severity) {
    case 'blocker':
    case 'high':
      return 'error';
    case 'medium':
      return 'warning';
    case 'low':
      return 'information';
    default:
      return 'hint';
  }
}

export function diagnosticSpec(issue: Issue, anchor: Anchor): DiagnosticSpec | undefined {
  if (anchor.line === undefined) return undefined;
  const note = anchorNote(anchor.state);
  const parts = [issue.message.length === 0 ? issue.rule : issue.message];
  if (issue.excepted) parts.push('exception acceptée');
  if (note !== undefined) parts.push(note);
  return {
    line: anchor.line,
    severity: diagnosticSeverity(issue),
    message: `${parts.join(' — ')} [${severityLabel(issue.severity)}]`,
    code: issue.rule,
    documentationUrl: issue.documentationUrl,
    unnecessary: issue.excepted,
  };
}
