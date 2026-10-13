import type { Issue } from './issues';
import { severityLabel } from './issues';
import { flows, locationLabel, secondary } from './locations';
import { anchorNote, type AnchorState } from './drift';

/**
 * Content of the details panel. It shows what the analysis actually produced —
 * identity, criticality, provenance, locations — and links out to the definition the
 * producer declared. CodeInsights stores no rule prose, so none is displayed or
 * invented here: DESIGN.md keeps rule explanations external.
 */

export type DetailsRow = { label: string; value: string; mono?: boolean };

export type Details = {
  title: string;
  rows: DetailsRow[];
  locations: string[];
  documentationUrl: string | undefined;
  note: string | undefined;
  /** Why no rule description is rendered; shown so the absence is explicit. */
  descriptionNotice: string;
};

export const DESCRIPTION_NOTICE =
  "CodeInsights n'héberge pas de description de règle : la définition reste celle publiée par le producteur de l'analyse.";

export function details(issue: Issue, anchorState: AnchorState | undefined, commitSha: string | undefined): Details {
  const rows: DetailsRow[] = [
    { label: 'Criticité', value: severityLabel(issue.severity) },
    { label: 'Catégorie', value: issue.category },
    { label: 'Règle', value: `${issue.tool}/${issue.rule}`, mono: true },
    { label: 'Fichier', value: issue.path ?? 'localisation indisponible', mono: true },
    { label: 'Ligne analysée', value: issue.line === undefined ? 'inconnue' : String(issue.line) },
  ];
  if (commitSha !== undefined && commitSha.length > 0) rows.push({ label: 'Commit analysé', value: commitSha.slice(0, 8), mono: true });
  if (issue.excepted) rows.push({ label: 'Exception', value: 'acceptée sur cette issue' });
  const steps = flows(issue.locations).flatMap((flow) =>
    flow.steps.map((step, index) => `${flow.flow === undefined ? 'Flux' : `Flux ${flow.flow}`} · ${locationLabel(step, index + 1)}`),
  );
  const others = secondary(issue.locations)
    .filter((location) => location.kind !== 'flow')
    .map(locationLabel);
  const locations = [...steps, ...others];
  if (issue.locationsTruncated) locations.push('Le producteur a tronqué la liste des localisations.');
  const note = anchorState === undefined ? undefined : anchorNote(anchorState);
  return {
    title: issue.message.length === 0 ? issue.rule : issue.message,
    rows,
    locations,
    documentationUrl: issue.documentationUrl,
    note,
    descriptionNotice: DESCRIPTION_NOTICE,
  };
}

export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** Only http(s) links are rendered: a documentation URL comes from an analysis report. */
export function safeLink(url: string | undefined): string | undefined {
  if (url === undefined) return undefined;
  return /^https?:\/\//i.test(url) ? url : undefined;
}

/** Static HTML of the panel; the colours are the editor's own theme variables. */
export function detailsHtml(view: Details | undefined): string {
  if (view === undefined)
    return body('<p class="empty">Sélectionner une issue dans la liste pour afficher son détail.</p>');
  const rows = view.rows
    .map(
      (row) =>
        `<div class="row"><span class="label">${escapeHtml(row.label)}</span><span class="value${row.mono === true ? ' mono' : ''}">${escapeHtml(row.value)}</span></div>`,
    )
    .join('');
  const locations =
    view.locations.length === 0
      ? ''
      : `<h3>Localisations</h3><ul>${view.locations.map((location) => `<li class="mono">${escapeHtml(location)}</li>`).join('')}</ul>`;
  const link = safeLink(view.documentationUrl);
  const documentation =
    link === undefined
      ? `<p class="notice">${escapeHtml(view.descriptionNotice)}</p>`
      : `<p><a href="${escapeHtml(link)}">Définition de la règle publiée par le producteur</a></p><p class="notice">${escapeHtml(view.descriptionNotice)}</p>`;
  const note = view.note === undefined ? '' : `<p class="note">${escapeHtml(view.note)}</p>`;
  return body(`<h2>${escapeHtml(view.title)}</h2>${note}<div class="rows">${rows}</div>${locations}${documentation}`);
}

function body(content: string): string {
  return `<!DOCTYPE html><html lang="fr"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
<style>
body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground); padding: 0.75rem 1rem; }
h2 { font-size: 1.05em; margin: 0 0 0.75rem; }
h3 { font-size: 0.95em; margin: 1rem 0 0.35rem; }
.rows { display: grid; gap: 0.25rem; }
.row { display: grid; grid-template-columns: minmax(0, 9rem) minmax(0, 1fr); gap: 0.75rem; }
.label { color: var(--vscode-descriptionForeground); }
.value { overflow-wrap: anywhere; }
.mono { font-family: var(--vscode-editor-font-family); }
ul { margin: 0; padding-left: 1.1rem; }
li { overflow-wrap: anywhere; }
a { color: var(--vscode-textLink-foreground); }
.note { color: var(--vscode-editorWarning-foreground); margin: 0 0 0.75rem; }
.notice, .empty { color: var(--vscode-descriptionForeground); }
</style></head><body>${content}</body></html>`;
}
