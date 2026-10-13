import type { BranchReport } from './branch-report';
import { verdictReason } from './branch-report';
import { gateLabel } from './measure';
import { palette, ratingColor } from './palette';

/**
 * Shape of the side view, built without the extension host so the tree contents are
 * asserted directly by the checks.
 */

export type ReportNode = {
  id: string;
  label: string;
  description?: string;
  tooltip?: string;
  icon?: string;
  /** Theme colour id painting the icon; absent when the node carries no result. */
  color?: string;
  children?: ReportNode[];
  /** Location to reveal when the node is selected, for local findings. */
  open?: { path: string; line: number };
};

export type LocalEstimateNode = {
  label: string;
  notice: string;
  effect: 'worsens' | 'improves' | 'no-worsening-detected' | 'unknown';
  conditions: { metric: string; label: string; display: string; result: string }[];
};

/** Result of the local run on the pending change, as displayed under the branch report. */
export type LocalSection = {
  label: string;
  notice: string | undefined;
  baseRef: string;
  producers: { tool: string; state: string; reason: string | undefined }[];
  introduced: { id: string; label: string; description: string; path: string; line: number }[];
  corrected: { id: string; label: string; description: string; path: string; line: number }[];
  suppressed: number;
  estimate: LocalEstimateNode;
};

export type ViewState =
  | { kind: 'message'; message: string; detail?: string }
  | { kind: 'report'; report: BranchReport; context: ContextLine; staleness?: string; local?: LocalSection };

export type ContextLine = { branch: string; headSha?: string; behind?: string };

/** Nodes of the side view; kept pure so the shape is asserted without the extension host. */
export function reportNodes(state: ViewState): ReportNode[] {
  if (state.kind === 'message')
    return [
      state.detail === undefined
        ? { id: 'message', label: state.message }
        : { id: 'message', label: state.message, description: state.detail },
    ];
  const { report } = state;
  const reason = verdictReason(report);
  const verdict: ReportNode = {
    id: 'verdict',
    label: `Quality Gate : ${gateLabel(report.verdict)}`,
    icon: report.verdict === 'pass' ? 'pass' : report.verdict === 'fail' ? 'error' : 'question',
    color: report.verdict === 'pass' ? palette.pass : report.verdict === 'fail' ? palette.fail : palette.muted,
  };
  if (reason !== undefined) verdict.description = reason;
  if (state.staleness !== undefined) verdict.tooltip = `Rapport en cache, ${state.staleness}`;
  const identity: ReportNode = {
    id: 'identity',
    label: 'Analyse',
    children: [
      { id: 'identity.project', label: 'Projet', description: report.projectKey },
      { id: 'identity.branch', label: 'Branche', description: report.branch },
      ...(report.pullRequest === undefined
        ? []
        : [{ id: 'identity.pull-request', label: 'Pull request', description: `#${report.pullRequest.id} → ${report.pullRequest.targetBranch ?? 'cible non enregistrée'}` }]),
      { id: 'identity.commit', label: 'Commit analysé', description: report.commitSha.slice(0, 8) },
      { id: 'identity.date', label: 'Terminée le', description: report.createdAt ?? 'inconnue' },
      { id: 'identity.profile', label: 'Profil', description: report.profile ?? 'inconnu' },
      { id: 'identity.state', label: 'État technique', description: report.technicalState },
      { id: 'identity.completeness', label: 'Complétude', description: report.completeness },
      { id: 'identity.head', label: 'HEAD local', description: state.context.headSha?.slice(0, 8) ?? 'inconnu' },
      { id: 'identity.drift', label: 'Écart', description: state.context.behind ?? 'inconnu' },
    ],
  };
  const metrics: ReportNode = {
    id: 'metrics',
    label: 'Mesures',
    // A rating carries the A-E scale, so its dot is coloured; a raw count carries no
    // threshold of its own and stays uncoloured rather than inventing a verdict.
    children: report.metrics.map((metric) =>
      !metric.measured
        ? {
            id: `metric.${metric.key}`,
            label: metric.label,
            description: metric.display,
            icon: 'circle-slash',
            color: palette.muted,
          }
        : metric.key.endsWith('_rating')
          ? {
              id: `metric.${metric.key}`,
              label: metric.label,
              description: metric.display,
              icon: 'circle-filled',
              color: ratingColor(metric.display),
            }
          : { id: `metric.${metric.key}`, label: metric.label, description: metric.display },
    ),
  };
  const conditions: ReportNode = {
    id: 'conditions',
    label: 'Conditions du gate',
    children:
      report.conditions.length === 0
        ? [{ id: 'conditions.none', label: 'Aucune condition publiée' }]
        : report.conditions.map((condition) => ({
            id: `condition.${condition.metric}`,
            label: condition.label,
            description: condition.display,
            icon: condition.result === 'pass' ? 'pass' : condition.result === 'fail' ? 'error' : 'question',
            color: condition.result === 'pass' ? palette.pass : condition.result === 'fail' ? palette.fail : palette.muted,
            tooltip: condition.required ? 'Condition requise par le profil' : 'Condition informative',
          })),
  };
  if (state.local === undefined) return [verdict, identity, metrics, conditions];
  const local: ReportNode = {
    id: 'local',
    label: 'Changements en cours',
    description: state.local.label,
    icon: 'beaker',
    color: palette.info,
    children: [
      { id: 'local.base', label: 'Base du diff', description: state.local.baseRef },
      ...state.local.producers.map((producer) => ({
        id: `local.producer.${producer.tool}`,
        label: producer.tool,
        description: producer.state === 'measured' ? 'mesuré' : `${producer.state === 'absent' ? 'absent' : 'en échec'}${producer.reason === undefined ? '' : ` — ${producer.reason}`}`,
        icon: producer.state === 'measured' ? 'pass' : 'circle-slash',
        color: producer.state === 'measured' ? palette.pass : producer.state === 'failed' ? palette.warn : palette.muted,
      })),
      {
        id: 'local.estimate',
        label: `Estimation du gate : ${state.local.estimate.label}`,
        tooltip: state.local.estimate.notice,
        icon:
          state.local.estimate.effect === 'worsens'
            ? 'error'
            : state.local.estimate.effect === 'improves'
              ? 'arrow-up'
              : state.local.estimate.effect === 'no-worsening-detected'
                ? 'info'
                : 'question',
        color:
          state.local.estimate.effect === 'worsens'
            ? palette.fail
            : state.local.estimate.effect === 'improves'
              ? palette.pass
              : state.local.estimate.effect === 'no-worsening-detected'
                ? palette.info
                : palette.muted,
        children:
          state.local.estimate.conditions.length === 0
            ? [{ id: 'local.estimate.none', label: 'Aucune condition chiffrable à estimer' }]
            : state.local.estimate.conditions.map((condition) => ({
                id: `local.estimate.${condition.metric}`,
                label: condition.label,
                description: condition.display,
                icon:
                  condition.result === 'worsens'
                    ? 'error'
                    : condition.result === 'improves'
                      ? 'arrow-up'
                      : condition.result === 'unchanged'
                        ? 'info'
                        : condition.result === 'not-estimable'
                          ? 'circle-slash'
                          : 'question',
                color:
                  condition.result === 'worsens'
                    ? palette.fail
                    : condition.result === 'improves'
                      ? palette.pass
                      : condition.result === 'unchanged'
                        ? palette.info
                        : palette.muted,
              })),
      },
      ...(state.local.suppressed === 0
        ? []
        : [
            {
              id: 'local.suppressed',
              label: 'Masquées',
              description: `${state.local.suppressed} par exception ou règle désactivée`,
            },
          ]),
      ...(state.local.introduced.length === 0
        ? [{ id: 'local.none', label: 'Aucune issue sur les lignes ajoutées' }]
        : state.local.introduced.map((finding) => ({
            id: finding.id,
            label: finding.label,
            description: finding.description,
            icon: 'error',
            color: palette.fail,
            open: { path: finding.path, line: finding.line },
          }))),
      ...(state.local.corrected.length === 0
        ? []
        : state.local.corrected.map((finding) => ({
            id: finding.id,
            label: finding.label,
            description: finding.description,
            icon: 'check',
            color: palette.pass,
            open: { path: finding.path, line: finding.line },
          }))),
    ],
  };
  if (state.local.notice !== undefined) local.tooltip = state.local.notice;
  return [verdict, local, identity, metrics, conditions];
}
