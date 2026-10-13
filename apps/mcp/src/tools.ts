import type { CodeInsightsApi } from '../../core/src/api/client';
import { issuePrompt } from '../../core/src/api/collaboration';
import {
  codeQuality,
  coverageFiles,
  duplications,
  findings,
  projectExceptions,
  projectRules,
  type RawAnalysis,
  type RawFinding,
} from '../../core/src/api/codeinsights';
import { analysisScope, branchReport } from '../../core/src/report/branch-report';
import { DESCRIPTION_NOTICE, details } from '../../core/src/report/details';
import { blocksFor } from '../../core/src/report/duplication';
import { toIssue, type Issue, type Severity } from '../../core/src/report/issues';
import { formatMetric, freshness, measurementLabel } from '../../core/src/report/measure';
import { toRule } from '../../core/src/report/rules';
import { partialNotice, reportLabel, type Exception } from '../../core/src/local/analysis';
import { correctedIssues, estimateGate, estimateLabel, estimateNotice, type BranchIssue } from '../../core/src/local/gate';
import { analyseChanges } from '../../core/src/local/session';
import { hubBranchKey, hubUrl } from '../../core/src/hub/deep-link';
import type { Config } from './config';
import { client, fail, requireAnalysis, resolveTarget, ToolError, type Target } from './hub';
import { clamp, renderBranchReport, renderFileQuality, renderFinding, renderFindings, renderGate, renderLocalRun, renderRule } from './render';
import { git, headSha, nodeWorkspace } from './workspace';

/**
 * The tools are built on the agent's intentions, not on the routes of the hub. One tool
 * per route would make forty generic tools an agent gets lost in; these seven answer the
 * questions it actually asks — what is the state of my branch, what is wrong, and where.
 */

const SEVERITIES: Severity[] = ['blocker', 'high', 'medium', 'low', 'info'];

/** Source context is prose for the model; past this it displaces the agent's own reasoning. */
const PROMPT_BUDGET = 6000;

/** The hub pages findings at 100; this caps what one tool call may read behind the scenes. */
const SCAN_LIMIT = 500;

export function createTools(config: Config) {
  // An agent chains several tools on the same analysis: list, then explain, then the rule.
  // The findings of that analysis are read once and reused, and dropped when it changes.
  let scanned: { analysisId: string; rows: RawFinding[]; complete: boolean } | undefined;

  const scan = async (api: CodeInsightsApi, analysis: RawAnalysis): Promise<RawFinding[]> => {
    if (scanned?.analysisId === analysis.analysis_id) return scanned.rows;
    const rows = await findings(api, analysis.analysis_id, '', SCAN_LIMIT);
    // `complete` dit si le plafond a coupé : une issue absente n'est alors pas une preuve.
    scanned = { analysisId: analysis.analysis_id, rows, complete: rows.length < SCAN_LIMIT };
    return rows;
  };

  const open = async (cwd: string, branch?: string): Promise<{ api: CodeInsightsApi; target: Target; analysis: RawAnalysis }> => {
    const api = client(config);
    const target = await resolveTarget(api, config, cwd, branch);
    return { api, target, analysis: requireAnalysis(target) };
  };

  const link = (analysis: RawAnalysis, extra: { file?: string; rule?: string } = {}): string | undefined => {
    try {
      return hubUrl({
        serverUrl: config.serverUrl,
        tab: extra.file === undefined && extra.rule === undefined ? 'projects' : 'issues',
        project: analysis.project_key,
        branch: hubBranchKey(analysis),
        analysisId: analysis.analysis_id,
        ...(extra.file === undefined ? {} : { file: extra.file }),
        ...(extra.rule === undefined ? {} : { rule: extra.rule }),
      });
    } catch {
      return undefined;
    }
  };

  return {
    /** Verdict, completeness, technical state and counters of the branch's last analysis. */
    async getBranchReport(input: { cwd: string; branch?: string | undefined }): Promise<string> {
      try {
        const { target, analysis } = await open(input.cwd, input.branch);
        const report = branchReport(analysis);
        return renderBranchReport(report, {
          hubUrl: link(analysis),
          freshness: await drift(target.rootPath, analysis.commit_sha),
        });
      } catch (error) {
        return fail(error);
      }
    },

    /** Findings of the last analysis, filtered server-side, severity first and bounded. */
    async listFindings(input: { cwd: string; branch?: string | undefined; paths?: string[] | undefined; severity?: string | undefined; limit?: number | undefined }): Promise<string> {
      try {
        const { api, analysis } = await open(input.cwd, input.branch);
        const limit = bounded(input.limit, config.defaultLimit, config.maxLimit);
        const severity = input.severity === undefined || input.severity === 'all' ? undefined : input.severity;
        const paths = (input.paths ?? []).map((path) => path.trim()).filter((path) => path.length > 0).slice(0, 5);
        const queries = paths.length === 0 ? [''] : paths.map((path) => `path=${encodeURIComponent(path)}`);
        const collected = new Map<number, RawFinding>();
        for (const query of queries) {
          const parts = [query, severity === undefined ? '' : `severity=${encodeURIComponent(severity)}`].filter((part) => part.length > 0);
          // limit + 1 rather than limit: the extra row is how the answer knows it truncated.
          for (const row of await findings(api, analysis.analysis_id, parts.join('&'), limit + 1)) collected.set(row.id, row);
        }
        const issues = [...collected.values()].map(toIssue).sort(order);
        return renderFindings(issues.slice(0, limit), {
          projectKey: analysis.project_key,
          branch: analysisScope(analysis),
          analysisId: analysis.analysis_id,
          scope: scope(severity, paths),
          available: issues.length,
          limit: config.maxLimit,
        });
      } catch (error) {
        return fail(error);
      }
    },

    /** Message, rule, secondary locations and the source context the hub kept. */
    async explainFinding(input: { cwd: string; finding_id: number; include_source?: boolean | undefined }): Promise<string> {
      try {
        const { api, analysis } = await open(input.cwd);
        const rows = await scan(api, analysis);
        const row = rows.find((candidate) => candidate.id === input.finding_id);
        if (row === undefined)
          throw new ToolError(
            `Aucune issue ${input.finding_id} dans l'analyse ${analysis.analysis_id}${scanned?.complete === false ? ` (les ${SCAN_LIMIT} premières issues seulement ont été lues)` : ''}. Les identifiants changent à chaque analyse : relancer list_findings.`,
          );
        const issue = toIssue(row);
        let prompt: string | undefined;
        if (input.include_source !== false) {
          if (issue.promptAvailable) {
            try {
              const text = await issuePrompt(api, issue.id);
              prompt = text.length === 0 ? undefined : clamp(text, PROMPT_BUDGET);
            } catch (error) {
              prompt = `indisponible (${error instanceof Error ? error.message : String(error)})`;
            }
          } else {
            // Saying why there is no source beats an agent assuming there was none to show.
            prompt = `indisponible : ${issue.promptUnavailableReason ?? 'le hub n’a pas conservé la source de cette issue'}`;
          }
        }
        return renderFinding(details(issue, undefined, analysis.commit_sha), {
          projectKey: analysis.project_key,
          analysisId: analysis.analysis_id,
          prompt,
          hubUrl: link(analysis, issue.path === undefined ? {} : { file: issue.path }),
        });
      } catch (error) {
        return fail(error);
      }
    },

    /** The rule as the project's profile holds it — not the violation. */
    async describeRule(input: { cwd: string; rule_id: string }): Promise<string> {
      try {
        const { api, target, analysis } = await open(input.cwd);
        const needle = input.rule_id.trim();
        if (needle.length === 0) throw new ToolError('rule_id est vide.');
        const rules = (await projectRules(api, target.projectKey, `search=${encodeURIComponent(needle)}`)).map(toRule);
        const exact = rules.filter((rule) => rule.ruleId === needle || rule.key === needle || rule.ruleId.endsWith(`/${needle}`));
        const candidates = exact.length > 0 ? exact : rules;
        const rule = candidates[0];
        if (rule === undefined)
          throw new ToolError(
            `Aucune règle « ${needle} » observée sur les analyses de ${target.projectKey}. Le catalogue ne contient que les règles que les producteurs ont réellement émises.`,
          );
        if (candidates.length > 1 && exact.length !== 1)
          throw new ToolError(
            `Plusieurs règles correspondent à « ${needle} » : ${candidates.slice(0, 10).map((candidate) => candidate.key).join(', ')}. Reprendre l'identifiant complet.`,
          );
        const documented = (await scan(api, analysis)).find(
          (row) => (row.rule_id === rule.ruleId || row.rule_id === rule.key) && typeof row.evidence?.documentation_url === 'string' && row.evidence.documentation_url.length > 0,
        );
        return renderRule(rule, {
          projectKey: target.projectKey,
          documentationUrl: documented?.evidence?.documentation_url ?? undefined,
          notice: DESCRIPTION_NOTICE,
        });
      } catch (error) {
        return fail(error);
      }
    },

    /** Verdict and the conditions the analysis did not hold. */
    async getQualityGate(input: { cwd: string; branch?: string | undefined }): Promise<string> {
      try {
        const { analysis } = await open(input.cwd, input.branch);
        return renderGate(branchReport(analysis), link(analysis));
      } catch (error) {
        return fail(error);
      }
    },

    /**
     * The pending change, scanned on the workstation. This is the only tool a remote
     * transport could never serve: it reads the working tree, and no source leaves the
     * machine — the producers run without network and the hub is only read.
     */
    async analyzePendingChanges(input: { cwd: string; target_branch?: string | undefined }): Promise<string> {
      try {
        const { api, target, analysis } = await open(input.cwd);
        // `target.local` : ce que le dépôt déclare, à défaut l'environnement du serveur.
        const targetBranch = input.target_branch?.trim() || target.local.targetBranch;
        const notes: string[] = [];
        if (target.local.semgrepConfigs.length === 0)
          notes.push('Aucune règle semgrep n’est configurée : la déclarer dans « semgrepConfig » de .codeinsights/vscode.json, ou définir CODEINSIGHTS_SEMGREP_CONFIG (chemin de règles du dépôt ou identifiant de registre), pour activer ce producteur.');
        const policy = await hubPolicy(api, target.projectKey, (message) => notes.push(message));
        const run = await analyseChanges(
          nodeWorkspace,
          target.rootPath,
          targetBranch,
          target.local,
          policy,
          (message) => notes.push(message),
        );
        if (run === undefined)
          throw new ToolError(
            `Diff local indisponible dans ${target.rootPath} : « ${targetBranch} » est inconnue de ce clone. Récupérer la branche cible (git fetch), ou passer « target_branch ».`,
          );
        const branchIssues = (await scan(api, analysis)).flatMap<BranchIssue>((row) => {
          const path = row.path === null || row.path === undefined || row.path.length === 0 ? undefined : row.path.replace(/\\/g, '/');
          const line = typeof row.line === 'number' && row.line > 0 ? row.line : undefined;
          if (path === undefined || line === undefined || row.excepted === true) return [];
          return [{ tool: row.tool ?? 'unknown', rule: row.rule_id ?? 'unknown', path, line, category: row.category ?? 'unknown', severity: row.severity ?? 'info', message: row.message ?? '' }];
        });
        const corrected = correctedIssues(branchIssues, run.report, run.changed.map((file) => file.path));
        const estimate = estimateGate(analysis.gate_snapshot?.conditions ?? [], run.report, corrected);
        return renderLocalRun({
          projectKey: target.projectKey,
          branch: target.branch,
          baseRef: run.baseRef,
          changedFiles: run.changed.length,
          cappedAt: run.changed.length > target.local.maxFiles ? target.local.maxFiles : undefined,
          label: reportLabel(run.report),
          partial: partialNotice(run.report),
          introduced: run.report.introduced.map((finding) => `${finding.severity} · ${finding.tool}/${finding.rule} · ${finding.path}:${finding.line} — ${clamp(finding.message)}`),
          corrected: corrected.map((issue) => `${issue.severity} · ${issue.tool}/${issue.rule} · ${issue.path}:${issue.line} — ${clamp(issue.message)}`),
          suppressed: run.report.suppressed,
          estimate: {
            label: estimateLabel(estimate.effect),
            notice: [estimateNotice(run.report.producers), ...notes].join(' '),
            conditions: estimate.conditions.map((condition) => `${condition.label} : ${condition.display}${condition.required ? ' (requise)' : ''}`),
          },
        });
      } catch (error) {
        return fail(error);
      }
    },

    /** Coverage and duplication of one file, as the analysis measured them. */
    async getFileQuality(input: { cwd: string; path: string }): Promise<string> {
      try {
        const { api, target, analysis } = await open(input.cwd);
        const path = input.path.trim().replace(/\\/g, '/').replace(/^\.\//, '');
        if (path.length === 0) throw new ToolError('path est vide.');
        const summary = await codeQuality(api, analysis.analysis_id);
        const files = await coverageFiles(api, analysis.analysis_id, path, 100);
        const file = files.files.find((candidate) => candidate.path === path) ?? files.files.find((candidate) => candidate.path.endsWith(path));
        const blocks = await duplications(api, analysis.analysis_id, path, 100);
        const placed = blocksFor(file?.path ?? path, blocks.duplications);
        return renderFileQuality(
          {
            path: file?.path ?? path,
            coverage: coverageLine(summary.coverage.state, files.state, file),
            duplication:
              blocks.state === 'measured'
                ? `${placed.length === 0 ? 'aucun bloc dupliqué sur ce fichier' : `${placed.length} bloc${placed.length > 1 ? 's' : ''} dupliqué${placed.length > 1 ? 's' : ''}`} · densité du projet ${formatMetric(summary.duplication.density_percentage ?? null, '%')}`
                : `${measurementLabel(state(blocks.state))}${blocks.reason === undefined ? '' : ` (${blocks.reason})`}`,
            duplicateBlocks: placed.slice(0, 20).map((block) => `${block.start}–${block.end} ↔ ${block.other.path}:${block.other.start}–${block.other.end}`),
          },
          { projectKey: target.projectKey, branch: analysisScope(analysis), analysisId: analysis.analysis_id },
        );
      } catch (error) {
        return fail(error);
      }
    },
  };
}

/**
 * Exceptions and disabled rules of the project, so a local run stays as quiet as the CI.
 * A policy the hub refuses is reported, not assumed empty: silently scanning without the
 * project's exceptions would drown the agent in findings the team already accepted.
 */
async function hubPolicy(
  api: CodeInsightsApi,
  projectKey: string,
  note: (message: string) => void,
): Promise<{ exceptions: Exception[]; disabledRules: string[] }> {
  const policy: { exceptions: Exception[]; disabledRules: string[] } = { exceptions: [], disabledRules: [] };
  try {
    policy.exceptions = await projectExceptions(api, projectKey, Date.now());
  } catch (error) {
    note(`Exceptions du projet indisponibles (${error instanceof Error ? error.message : String(error)}) : des findings déjà acceptés peuvent réapparaître.`);
  }
  try {
    policy.disabledRules = (await projectRules(api, projectKey, 'status=disabled'))
      .filter((rule) => rule.status === 'disabled')
      .map((rule) => `${rule.tool ?? ''}/${rule.rule_id ?? ''}`);
  } catch (error) {
    note(`Règles désactivées indisponibles (${error instanceof Error ? error.message : String(error)}) : des règles écartées du profil peuvent réapparaître.`);
  }
  return policy;
}

function coverageLine(
  analysisState: string | undefined,
  fileState: string | undefined,
  file: { covered_lines?: number; total_lines?: number; percentage?: number | null } | undefined,
): string {
  if (analysisState !== 'measured' && fileState !== 'measured') return measurementLabel(state(analysisState ?? fileState));
  if (file === undefined) return 'fichier absent du rapport de couverture — non instrumenté ou non exécuté par les tests';
  const percentage = file.percentage ?? null;
  const covered = file.covered_lines ?? 0;
  const total = file.total_lines ?? 0;
  return `${formatMetric(percentage, '%')} (${covered}/${total} lignes)`;
}

function state(raw: string | undefined): 'measured' | 'absent' | 'failed' | 'not_measured' | undefined {
  return raw === 'measured' || raw === 'absent' || raw === 'failed' || raw === 'not_measured' ? raw : undefined;
}

function order(left: Issue, right: Issue): number {
  const rank = SEVERITIES.indexOf(left.severity) - SEVERITIES.indexOf(right.severity);
  if (rank !== 0) return rank;
  return (left.path ?? '').localeCompare(right.path ?? '') || (left.line ?? 0) - (right.line ?? 0);
}

function scope(severity: string | undefined, paths: string[]): string {
  const parts = [severity === undefined ? undefined : `criticité ${severity}`, paths.length === 0 ? undefined : `chemins ${paths.join(', ')}`].filter(
    (part): part is string => part !== undefined,
  );
  return parts.length === 0 ? 'toutes les issues' : parts.join(' · ');
}

function bounded(value: number | undefined, fallback: number, ceiling: number): number {
  if (value === undefined || !Number.isFinite(value) || value <= 0) return fallback;
  return Math.min(Math.floor(value), ceiling);
}

/** How far the working tree drifted from the analysed commit, or nothing if unknowable. */
async function drift(rootPath: string, commitSha: string): Promise<string | undefined> {
  const head = await headSha(rootPath);
  let ahead: number | undefined;
  try {
    const count = Number((await git(rootPath, ['rev-list', '--count', `${commitSha}..HEAD`])).trim());
    if (Number.isFinite(count)) ahead = count;
  } catch {
    // Shallow clone, or a commit this clone never fetched: the drift stays unknown.
  }
  return freshness(commitSha, head, ahead);
}
