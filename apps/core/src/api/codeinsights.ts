import type { CodeInsightsApi } from './client';

/**
 * Reads of the hub the extension depends on. The shapes mirror the API contracts
 * (snake_case, `null` for an absent measure) and are deliberately partial: only the
 * fields consumed here are declared, so an unrelated contract change stays harmless.
 */

export type RawMeasurement = { state?: string; tool?: string; tool_version?: string };

export type RawCondition = {
  metric?: string;
  required?: boolean;
  operator?: string;
  threshold?: number | null;
  value?: number | null;
  state?: string;
  result?: string;
};

export type RawAnalysis = {
  analysis_id: string;
  project_key: string;
  branch: string;
  commit_sha: string;
  /** Set on a pull request build: it analyses what the pull request merges, not the branch itself. */
  pr_id?: string | null;
  pr_target_branch?: string | null;
  status?: string;
  quality_gate?: string;
  completeness?: string;
  technical_state?: string;
  created_at?: string;
  profile_id?: string;
  profile_version?: string;
  metrics?: Record<string, number | string | null | undefined>;
  measurements?: Record<string, RawMeasurement>;
  gate_snapshot?: { conditions?: RawCondition[] };
};

export type RawProject = { key: string; name?: string };

/** Projects the caller may see whose integration points at this clone URL. */
export async function projectsForRepository(api: CodeInsightsApi, repositoryUrl: string): Promise<RawProject[]> {
  return api.json<RawProject[]>(`/api/v1/projects?repo_url=${encodeURIComponent(repositoryUrl)}`);
}

/**
 * Spellings of a branch to try against the hub. The platform stores the branch as the CI
 * reports it — the full ref `refs/heads/x` — while the Git extension gives the short name,
 * and the server matches the `branch` filter exactly. Both forms are tried rather than
 * concluding on the first miss: announcing « aucune analyse » over a real report is the
 * failure this extension exists to avoid.
 */
export function branchCandidates(branch: string): string[] {
  const name = branch.trim().replace(/^\/+|\/+$/g, '');
  if (name.length === 0) return [];
  if (!name.startsWith('refs/')) return [`refs/heads/${name}`, name];
  const short = name.replace(/^refs\/(?:heads|remotes\/[^/]+)\//, '');
  return short.length === 0 || short === name ? [name] : [name, short];
}

/**
 * Newest branch build of a branch; failing that, the newest pull request build from that
 * branch, which names itself through `pr_id` and is displayed as the pull request it is.
 * null when the branch was never analysed. A hub predating the `kind` filter ignores it and
 * answers with either kind, still labelled by `pr_id`.
 */
export async function latestAnalysis(
  api: CodeInsightsApi,
  projectKey: string,
  branch: string,
): Promise<RawAnalysis | null> {
  for (const kind of ['branch', 'pull_request'] as const) {
    for (const candidate of branchCandidates(branch)) {
      const query = `project_key=${encodeURIComponent(projectKey)}&branch=${encodeURIComponent(candidate)}&kind=${kind}&limit=1`;
      const page = await api.page<RawAnalysis>(`/api/v1/analyses?${query}`);
      const found = page.items[0];
      if (found !== undefined) return found;
    }
  }
  return null;
}

export type RawFinding = {
  id: number;
  finding_id?: string;
  tool?: string;
  rule_id?: string;
  severity?: string;
  category?: string;
  message?: string;
  path?: string | null;
  line?: number | null;
  excepted?: boolean;
  prompt_available?: boolean;
  prompt_unavailable_reason?: string | null;
  source_url?: string | null;
  evidence?: RawEvidence | null;
};

export type RawEvidenceLocation = {
  kind?: string;
  flow?: number | null;
  thread?: number | null;
  step?: number;
  path?: string | null;
  line?: number | null;
  end_line?: number | null;
  column?: number | null;
  end_column?: number | null;
  repository_relative?: boolean;
  source_url?: string | null;
};

export type RawEvidence = {
  driver_version?: string | null;
  documentation_url?: string | null;
  documentation_source?: string | null;
  documentation_status?: string | null;
  locations_truncated?: boolean;
  locations?: RawEvidenceLocation[] | null;
};

/** Findings of one analysis, filtered server-side and read page by page. */
export async function findings(api: CodeInsightsApi, analysisId: string, query: string, limit = 500): Promise<RawFinding[]> {
  const path = `/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/findings`;
  return api.all<RawFinding>(query.length === 0 ? path : `${path}?${query}`, limit);
}

export type MeasurementEnvelope = { state?: string; reason?: string | null };

export type CoverageLine = { line: number; covered: boolean };

export type CoverageFile = {
  path: string;
  covered_lines?: number;
  uncovered_lines?: number;
  total_lines?: number;
  percentage?: number | null;
};

export type DuplicateLocation = { path?: string; start_line?: number; end_line?: number };

export type DuplicateBlock = {
  language?: string;
  lines?: number;
  first?: DuplicateLocation;
  second?: DuplicateLocation;
};

export type CodeQualitySummary = {
  coverage: MeasurementEnvelope & {
    covered_lines?: number | null;
    uncovered_lines?: number | null;
    total_lines?: number | null;
    percentage?: number | null;
  };
  duplication: MeasurementEnvelope & {
    density_percentage?: number | null;
    duplicate_blocks?: number | null;
    duplicated_lines?: number | null;
  };
};

export type RawComponentVulnerability = {
  id?: string;
  severity?: string | null;
  source?: string | null;
  published_at?: string | null;
};

export type RawComponent = {
  id: number;
  name: string;
  version?: string | null;
  purl?: string | null;
  ecosystem?: string | null;
  vulnerabilities?: RawComponentVulnerability[] | unknown;
};

export type RawException = { tool?: string; rule_id?: string; path?: string | null; expires_at?: string | null };

/**
 * Exceptions the project accepted, expired ones removed: an exception past its date no
 * longer suppresses anything, and treating it as still valid would hide a real finding.
 */
export async function projectExceptions(
  api: CodeInsightsApi,
  projectKey: string,
  now: number,
): Promise<{ tool: string; ruleId: string; path: string | null }[]> {
  const rows = await api.json<RawException[]>(`/api/v1/projects/${encodeURIComponent(projectKey)}/exceptions`);
  return rows
    .filter((row) => row.expires_at === null || row.expires_at === undefined || Date.parse(row.expires_at) > now)
    .map((row) => ({ tool: row.tool ?? '', ruleId: row.rule_id ?? '', path: row.path ?? null }));
}

export type RawRule = {
  key?: string;
  tool?: string;
  rule_id?: string;
  title?: string;
  category?: string;
  severity?: string;
  language?: string;
  occurrences?: number;
  status?: string;
};

async function pagedField<T>(api: CodeInsightsApi, path: string, field: string, limit: number): Promise<{ envelope: Record<string, unknown>; items: T[] }> {
  const items: T[] = [];
  let cursor: string | null = null;
  let envelope: Record<string, unknown> = {};
  do {
    const url: string = cursor === null ? path : `${path}${path.includes('?') ? '&' : '?'}cursor=${encodeURIComponent(cursor)}`;
    const page: { body: Record<string, unknown>; next: string | null } = await api.jsonWithCursor<Record<string, unknown>>(url);
    envelope = page.body;
    const extra = page.body[field];
    if (Array.isArray(extra)) items.push(...(extra as T[]));
    cursor = page.next;
  } while (cursor !== null && items.length < limit);
  return { envelope, items: items.slice(0, limit) };
}

/** Line coverage of one path. An unmeasured analysis returns an empty list and its state. */
export async function coverageLines(
  api: CodeInsightsApi,
  analysisId: string,
  path: string,
  limit = 5000,
): Promise<MeasurementEnvelope & { lines: CoverageLine[] }> {
  const query = `path=${encodeURIComponent(path)}&covered=all&limit=100`;
  const { envelope, items } = await pagedField<CoverageLine>(
    api,
    `/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/coverage/lines?${query}`,
    'lines',
    limit,
  );
  const result: MeasurementEnvelope & { lines: CoverageLine[] } = {
    state: typeof envelope.state === 'string' ? envelope.state : 'not_measured',
    lines: items.filter((row) => typeof row.line === 'number' && typeof row.covered === 'boolean'),
  };
  if (typeof envelope.reason === 'string') result.reason = envelope.reason;
  return result;
}

export async function coverageFiles(
  api: CodeInsightsApi,
  analysisId: string,
  search: string,
  limit = 100,
): Promise<MeasurementEnvelope & { files: CoverageFile[] }> {
  const query = search.length === 0 ? 'limit=100' : `search=${encodeURIComponent(search)}&limit=100`;
  const { envelope, items } = await pagedField<CoverageFile>(
    api,
    `/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/coverage/files?${query}`,
    'files',
    limit,
  );
  const result: MeasurementEnvelope & { files: CoverageFile[] } = {
    state: typeof envelope.state === 'string' ? envelope.state : 'not_measured',
    files: items,
  };
  if (typeof envelope.reason === 'string') result.reason = envelope.reason;
  return result;
}

export async function duplications(
  api: CodeInsightsApi,
  analysisId: string,
  search: string,
  limit = 200,
): Promise<MeasurementEnvelope & { duplications: DuplicateBlock[] }> {
  const query = search.length === 0 ? 'limit=100' : `search=${encodeURIComponent(search)}&limit=100`;
  const { envelope, items } = await pagedField<DuplicateBlock>(
    api,
    `/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/duplications?${query}`,
    'duplications',
    limit,
  );
  const result: MeasurementEnvelope & { duplications: DuplicateBlock[] } = {
    state: typeof envelope.state === 'string' ? envelope.state : 'not_measured',
    duplications: items,
  };
  if (typeof envelope.reason === 'string') result.reason = envelope.reason;
  return result;
}

export async function codeQuality(api: CodeInsightsApi, analysisId: string): Promise<CodeQualitySummary> {
  return api.json<CodeQualitySummary>(`/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/code-quality`);
}

export async function components(api: CodeInsightsApi, analysisId: string, limit = 500): Promise<RawComponent[]> {
  return api.all<RawComponent>(`/api/v1/insights/analyses/${encodeURIComponent(analysisId)}/components?limit=100`, limit);
}

export async function projectRules(api: CodeInsightsApi, projectKey: string, query = '', limit = 500): Promise<RawRule[]> {
  const base = `/api/v1/insights/rules?project_key=${encodeURIComponent(projectKey)}&limit=100`;
  return api.all<RawRule>(query.length === 0 ? base : `${base}&${query}`, limit);
}
