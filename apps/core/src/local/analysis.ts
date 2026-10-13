import { isAdded, type ChangedFile } from './diff';
import type { LocalFinding, ProducerResult } from './producers';

/**
 * Assembles the local run. Two rules carry the whole thing: a producer that did not run
 * is reported as absent — never as zero findings — and only findings on lines the
 * developer actually added are attributed to the pending change.
 */

export type Exception = { tool: string; ruleId: string; path: string | null };

export type LocalReport = {
  producers: ProducerResult[];
  /** Findings on added lines, after project exceptions and disabled rules. */
  introduced: LocalFinding[];
  /** Findings of the same producers elsewhere in the changed files, for context. */
  elsewhere: LocalFinding[];
  suppressed: number;
  /** True when at least one producer could not measure: the picture is partial. */
  partial: boolean;
};

function matchesException(finding: LocalFinding, exception: Exception): boolean {
  const rule = finding.rule.includes('/') ? finding.rule.slice(finding.rule.indexOf('/') + 1) : finding.rule;
  if (exception.tool.length > 0 && exception.tool !== finding.tool) return false;
  if (exception.ruleId !== rule && exception.ruleId !== finding.rule) return false;
  if (exception.path === null || exception.path.length === 0) return true;
  return finding.path === exception.path || finding.path.startsWith(exception.path.replace(/\/+$/, '') + '/');
}

export function isSuppressed(finding: LocalFinding, exceptions: Exception[], disabledRules: string[]): boolean {
  if (disabledRules.includes(finding.rule)) return true;
  return exceptions.some((exception) => matchesException(finding, exception));
}

export function buildReport(
  producers: ProducerResult[],
  changed: ChangedFile[],
  exceptions: Exception[],
  disabledRules: string[],
): LocalReport {
  const index = new Map(changed.map((file) => [file.path, new Set(file.addedLines)]));
  const introduced: LocalFinding[] = [];
  const elsewhere: LocalFinding[] = [];
  let suppressed = 0;
  for (const producer of producers) {
    for (const finding of producer.findings) {
      if (isSuppressed(finding, exceptions, disabledRules)) {
        suppressed += 1;
        continue;
      }
      if (isAdded(index, finding.path, finding.line)) introduced.push(finding);
      else if (index.has(finding.path)) elsewhere.push(finding);
    }
  }
  const order = (finding: LocalFinding): number => ['blocker', 'high', 'medium', 'low', 'info'].indexOf(finding.severity);
  const sort = (list: LocalFinding[]): LocalFinding[] =>
    list.sort((a, b) => order(a) - order(b) || a.path.localeCompare(b.path) || a.line - b.line);
  return {
    producers,
    introduced: sort(introduced),
    elsewhere: sort(elsewhere),
    suppressed,
    partial: producers.some((producer) => producer.state !== 'measured'),
  };
}

/** One honest sentence about the run, including what could not be measured. */
export function reportLabel(report: LocalReport): string {
  const measured = report.producers.filter((producer) => producer.state === 'measured').map((producer) => producer.tool);
  if (measured.length === 0) return 'aucun producteur local disponible : rien n’a été mesuré';
  const count = report.introduced.length;
  const introduced = count === 0 ? 'aucune issue introduite' : count === 1 ? '1 issue introduite' : `${count} issues introduites`;
  const missing = report.producers.filter((producer) => producer.state !== 'measured');
  const partial =
    missing.length === 0 ? '' : ` · ${missing.map((producer) => `${producer.tool} ${producer.state === 'absent' ? 'absent' : 'en échec'}`).join(', ')}`;
  return `${introduced} sur les lignes ajoutées · ${measured.join(', ')}${partial}`;
}

/** Explicit warning: a partial run cannot conclude on the absence of issues. */
export function partialNotice(report: LocalReport): string | undefined {
  if (!report.partial) return undefined;
  const missing = report.producers.filter((producer) => producer.state !== 'measured');
  return `Analyse partielle : ${missing
    .map((producer) => `${producer.tool} — ${producer.reason ?? (producer.state === 'absent' ? 'non installé' : 'échec')}`)
    .join(' ; ')}. L'absence d'issue ne vaut pas absence de problème.`;
}
