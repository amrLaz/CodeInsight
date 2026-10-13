import type { Severity } from '../report/issues';

/**
 * Parsers for the two local producers. They read the tools' native JSON and never
 * invent a field: a finding without a usable location is dropped rather than placed at
 * line 1, and an unknown severity degrades to `info` instead of being guessed upwards.
 */

export type LocalFinding = {
  tool: string;
  rule: string;
  severity: Severity;
  category: string;
  message: string;
  path: string;
  line: number;
  endLine: number | undefined;
  column: number | undefined;
  endColumn: number | undefined;
};

export type ProducerState = 'measured' | 'absent' | 'failed';

export type ProducerResult = {
  tool: string;
  state: ProducerState;
  /** Why the producer did not measure; shown as such, never replaced by zero findings. */
  reason: string | undefined;
  findings: LocalFinding[];
};

function relative(path: string, root: string): string | undefined {
  const normalised = path.replace(/\\/g, '/');
  const base = root.replace(/\\/g, '/').replace(/\/+$/, '');
  if (base.length > 0 && normalised.startsWith(`${base}/`)) return normalised.slice(base.length + 1);
  if (normalised.startsWith('/') || /^[a-zA-Z]:/.test(normalised)) return undefined;
  return normalised.replace(/^\.\//, '');
}

function positive(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : undefined;
}

/** Semgrep severities: ERROR, WARNING, INFO — mapped onto the hub vocabulary. */
export function semgrepSeverity(raw: unknown): Severity {
  switch (String(raw ?? '').toUpperCase()) {
    case 'ERROR':
      return 'blocker';
    case 'WARNING':
      return 'medium';
    case 'INFO':
      return 'low';
    default:
      return 'info';
  }
}

export function parseSemgrep(output: string, root: string): LocalFinding[] {
  let payload: { results?: unknown };
  try {
    payload = JSON.parse(output) as { results?: unknown };
  } catch {
    throw new Error('sortie semgrep illisible');
  }
  const results = Array.isArray(payload.results) ? payload.results : [];
  const findings: LocalFinding[] = [];
  for (const entry of results) {
    const result = entry as {
      check_id?: string;
      path?: string;
      start?: { line?: number; col?: number };
      end?: { line?: number; col?: number };
      extra?: { message?: string; severity?: string; metadata?: { category?: string } };
    };
    const path = typeof result.path === 'string' ? relative(result.path, root) : undefined;
    const line = positive(result.start?.line);
    if (path === undefined || line === undefined) continue;
    findings.push({
      tool: 'semgrep',
      rule: `semgrep/${result.check_id ?? 'unknown'}`,
      severity: semgrepSeverity(result.extra?.severity),
      category: result.extra?.metadata?.category ?? 'code',
      message: result.extra?.message?.trim() ?? '',
      path,
      line,
      endLine: positive(result.end?.line),
      column: positive(result.start?.col),
      endColumn: positive(result.end?.col),
    });
  }
  return findings;
}

/** Gitleaks reports one JSON array of leaks; every leak is a blocking secret. */
export function parseGitleaks(output: string, root: string): LocalFinding[] {
  const trimmed = output.trim();
  if (trimmed.length === 0) return [];
  let payload: unknown;
  try {
    payload = JSON.parse(trimmed);
  } catch {
    throw new Error('sortie gitleaks illisible');
  }
  const leaks = Array.isArray(payload) ? payload : [];
  const findings: LocalFinding[] = [];
  for (const entry of leaks) {
    const leak = entry as { RuleID?: string; Description?: string; File?: string; StartLine?: number; StartColumn?: number; EndColumn?: number };
    const path = typeof leak.File === 'string' ? relative(leak.File, root) : undefined;
    const line = positive(leak.StartLine);
    if (path === undefined || line === undefined) continue;
    findings.push({
      tool: 'gitleaks',
      rule: `gitleaks/${leak.RuleID ?? 'unknown'}`,
      severity: 'blocker',
      category: 'secret',
      // The matched secret is never copied into the message: it would end up in logs.
      message: leak.Description ?? 'Secret détecté',
      path,
      line,
      endLine: undefined,
      column: positive(leak.StartColumn),
      endColumn: positive(leak.EndColumn),
    });
  }
  return findings;
}
