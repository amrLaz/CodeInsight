import type { BranchReport, ConditionRow } from '../../core/src/report/branch-report';
import { verdictReason } from '../../core/src/report/branch-report';
import type { Details } from '../../core/src/report/details';
import type { Issue } from '../../core/src/report/issues';
import { severityLabel } from '../../core/src/report/issues';
import { gateLabel } from '../../core/src/report/measure';
import type { Rule } from '../../core/src/report/rules';
import { statusLabel } from '../../core/src/report/rules';

/**
 * Compact text for an agent. Two rules decide everything here: what was not measured is
 * said, not zeroed, and every list announces what it left out. An unbounded listing
 * saturates the context window and degrades every later answer of the agent, and a
 * silently truncated one makes it conclude on a repository it has only half read.
 */

/** Beyond this, a message is prose the agent does not need in a list. */
const MESSAGE = 240;

export function clamp(text: string, max = MESSAGE): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  return flat.length <= max ? flat : `${flat.slice(0, max - 1)}…`;
}

function lines(...parts: (string | undefined)[]): string {
  return parts.filter((part): part is string => part !== undefined && part.length > 0).join('\n');
}

export type ReportContext = {
  hubUrl: string | undefined;
  /** Sentence from `freshness()`: how far the working tree drifted from the analysis. */
  freshness: string | undefined;
};

export function renderBranchReport(report: BranchReport, context: ReportContext): string {
  const reason = verdictReason(report);
  const measured = report.metrics.filter((metric) => metric.measured);
  const unmeasured = report.metrics.filter((metric) => !metric.measured);
  return lines(
    `${report.projectKey} · ${report.scope}`,
    `Quality Gate : ${gateLabel(report.verdict)}${reason === undefined ? '' : ` (${reason})`}`,
    `Analyse ${report.analysisId} · complétude ${report.completeness} · état technique ${report.technicalState}${report.createdAt === undefined ? '' : ` · ${report.createdAt}`}${report.profile === undefined ? '' : ` · profil ${report.profile}`}`,
    `Commit analysé ${report.commitSha.slice(0, 8)}${context.freshness === undefined ? '' : ` · ${context.freshness}`}`,
    measured.length === 0 ? 'Mesures : aucune' : `Mesures :\n${measured.map((metric) => `  ${metric.label} : ${metric.display}`).join('\n')}`,
    // Naming what was not measured is the point: an agent reading "0 bug" where nothing ran
    // concludes the code is clean and ships. This is the costliest failure of the integration.
    unmeasured.length === 0 ? undefined : `Non mesuré : ${unmeasured.map((metric) => `${metric.label} (${metric.display.toLowerCase()})`).join(', ')}`,
    renderConditions(report.conditions),
    context.hubUrl === undefined ? undefined : `Détail : ${context.hubUrl}`,
  );
}

export function renderConditions(conditions: ConditionRow[]): string {
  if (conditions.length === 0) return 'Conditions : aucune condition de gate enregistrée sur cette analyse.';
  const failed = conditions.filter((condition) => condition.result === 'fail');
  const unknown = conditions.filter((condition) => condition.result === 'unknown');
  const passed = conditions.length - failed.length - unknown.length;
  return lines(
    failed.length === 0
      ? `Conditions : ${passed}/${conditions.length} tenues.`
      : `Conditions non tenues :\n${failed.map((condition) => `  ✗ ${condition.label} : ${condition.display}${condition.required ? ' (requise)' : ''}`).join('\n')}`,
    unknown.length === 0
      ? undefined
      : `Conditions non concluantes :\n${unknown.map((condition) => `  ? ${condition.label} : ${condition.display}`).join('\n')}`,
  );
}

/** Verdict alone: the conditions, not the counters, are what an agent acts on. */
export function renderGate(report: BranchReport, hub: string | undefined): string {
  const reason = verdictReason(report);
  return lines(
    `${report.projectKey} · ${report.scope}`,
    `Quality Gate : ${gateLabel(report.verdict)}${reason === undefined ? '' : ` (${reason})`}`,
    `Analyse ${report.analysisId} · complétude ${report.completeness} · état technique ${report.technicalState}`,
    renderConditions(report.conditions),
    hub === undefined ? undefined : `Détail : ${hub}`,
  );
}

export type FindingsContext = {
  projectKey: string;
  branch: string;
  analysisId: string;
  /** Filters as the agent asked for them, echoed so it knows what it is reading. */
  scope: string;
  /** Findings the hub held before the limit applied. */
  available: number;
  limit: number;
};

export function renderFindings(issues: Issue[], context: FindingsContext): string {
  const header = `${context.projectKey} · ${context.branch} · analyse ${context.analysisId} · ${context.scope}`;
  if (issues.length === 0) return lines(header, 'Aucune issue pour ce filtre sur cette analyse.');
  const truncated = context.available > issues.length;
  return lines(
    header,
    truncated
      ? `${issues.length} issues affichées sur ${context.available} au moins — les plus critiques d'abord. Affiner « paths » ou « severity », ou augmenter « limit » (max ${context.limit}).`
      : `${issues.length} issue${issues.length > 1 ? 's' : ''}.`,
    issues.map((issue) => `[${issue.id}] ${severityLabel(issue.severity)} · ${issue.tool}/${issue.rule} · ${location(issue)}${issue.excepted ? ' · exception acceptée' : ''}\n    ${clamp(issue.message.length === 0 ? issue.rule : issue.message)}${issue.locationsSuffix}`).join('\n'),
    'Détail d’une issue : explain_finding avec l’identifiant entre crochets.',
  );
}

function location(issue: Issue): string {
  if (issue.path === undefined) return 'localisation indisponible';
  return issue.line === undefined ? issue.path : `${issue.path}:${issue.line}`;
}

export function renderFinding(details: Details, context: { projectKey: string; analysisId: string; prompt: string | undefined; hubUrl: string | undefined }): string {
  return lines(
    `${clamp(details.title, 400)}`,
    `${context.projectKey} · analyse ${context.analysisId}`,
    details.rows.map((row) => `  ${row.label} : ${row.value}`).join('\n'),
    details.locations.length === 0 ? undefined : `Localisations :\n${details.locations.map((entry) => `  ${entry}`).join('\n')}`,
    details.note,
    details.documentationUrl === undefined ? details.descriptionNotice : `Définition de la règle : ${details.documentationUrl}\n${details.descriptionNotice}`,
    context.prompt === undefined ? undefined : `Contexte source fourni par le hub :\n${context.prompt}`,
    context.hubUrl === undefined ? undefined : `Dans le hub : ${context.hubUrl}`,
  );
}

export function renderRule(rule: Rule, context: { projectKey: string; documentationUrl: string | undefined; notice: string }): string {
  return lines(
    `${rule.tool}/${rule.ruleId} — ${clamp(rule.title, 400)}`,
    `  Projet : ${context.projectKey}`,
    `  Statut dans le profil : ${statusLabel(rule.status)}`,
    `  Criticité : ${rule.severity} · catégorie : ${rule.category} · langage : ${rule.language}`,
    `  Occurrences sur la dernière analyse : ${rule.occurrences}`,
    context.documentationUrl === undefined ? undefined : `  Définition : ${context.documentationUrl}`,
    context.notice,
  );
}

export type FileQuality = {
  path: string;
  coverage: string;
  duplication: string;
  duplicateBlocks: string[];
};

export function renderFileQuality(quality: FileQuality, context: { projectKey: string; branch: string; analysisId: string }): string {
  return lines(
    `${quality.path} — ${context.projectKey} · ${context.branch} · analyse ${context.analysisId}`,
    `Couverture : ${quality.coverage}`,
    `Duplication : ${quality.duplication}`,
    quality.duplicateBlocks.length === 0 ? undefined : `Blocs dupliqués :\n${quality.duplicateBlocks.map((block) => `  ${block}`).join('\n')}`,
  );
}

export type LocalRunView = {
  projectKey: string;
  branch: string;
  baseRef: string;
  changedFiles: number;
  /** Said only when the cap dropped files: a silent cap reads as « everything scanned ». */
  cappedAt: number | undefined;
  /** One honest sentence about the run, from `reportLabel()`. */
  label: string;
  /** Present only when a producer did not measure; it forbids concluding on absence. */
  partial: string | undefined;
  introduced: string[];
  corrected: string[];
  suppressed: number;
  estimate: { label: string; notice: string; conditions: string[] };
};

/**
 * The local run. The estimate carries the word « estimation » in the text itself: an agent
 * that reads a verdict here would believe its change passes the pipeline, and ship.
 */
export function renderLocalRun(view: LocalRunView): string {
  return lines(
    `${view.projectKey} · ${view.branch} · base ${view.baseRef} · ${view.changedFiles} fichier${view.changedFiles > 1 ? 's' : ''} modifié${view.changedFiles > 1 ? 's' : ''}${view.cappedAt === undefined ? '' : ` — seuls les ${view.cappedAt} premiers ont été analysés (CODEINSIGHTS_MAX_FILES)`}`,
    view.label,
    view.partial,
    view.introduced.length === 0
      ? undefined
      : `Issues introduites sur les lignes ajoutées :\n${view.introduced.map((entry) => `  ${entry}`).join('\n')}`,
    view.corrected.length === 0 ? undefined : `Issues de l'analyse qui ne sont plus rapportées (estimation) :\n${view.corrected.map((entry) => `  ${entry}`).join('\n')}`,
    view.suppressed === 0 ? undefined : `${view.suppressed} finding${view.suppressed > 1 ? 's' : ''} supprimé${view.suppressed > 1 ? 's' : ''} par les exceptions et règles désactivées du projet.`,
    `Estimation du gate : ${view.estimate.label}`,
    view.estimate.conditions.length === 0 ? undefined : view.estimate.conditions.map((entry) => `  ${entry}`).join('\n'),
    view.estimate.notice,
  );
}
