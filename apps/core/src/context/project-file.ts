import type { ProjectMapping } from './project-key';

/**
 * `.codeinsights/vscode.json` : ce que le dépôt déclare sur lui-même, versionné avec lui.
 * L'IDE et l'agent le lisent tous les deux — c'est le seul endroit où un dépôt peut dire
 * quel projet il est et comment l'analyser sans que chaque poste le règle de son côté.
 *
 * `projectKey` et `projects` répondent « quel projet » ; `targetBranch` et `semgrepConfig`
 * répondent « avec quoi l'analyser ». Ces deux-là l'emportent sur les réglages du poste,
 * à l'inverse de la clé de projet : la branche de référence et le jeu de règles sont ceux
 * de la CI, et un poste qui en choisit d'autres rend un verdict que la pipeline dément.
 */
export type ProjectFile = ProjectMapping & {
  /** Référence servant de base au diff des changements en cours, ex. `origin/develop`. */
  targetBranch?: string;
  /** Fichiers de règles passés à semgrep, relatifs au dépôt. */
  semgrepConfig?: string[];
};

/** Ce que le dépôt impose, quand il l'impose. Un champ absent laisse le poste décider. */
export type ProjectSettings = {
  targetBranch?: string;
  semgrepConfigs?: string[];
};

/**
 * Lit les deux réglages d'analyse du fichier. Une valeur mal typée est ignorée plutôt que
 * propagée : elle ferait échouer semgrep ou chercher une branche inexistante, et l'agent
 * lirait cet échec comme « rien à signaler ». Un tableau vide, lui, est une déclaration —
 * le dépôt dit qu'il n'a pas de règles, et l'absence de producteur sera annoncée.
 */
export function projectSettings(file: ProjectFile | undefined): ProjectSettings {
  const settings: ProjectSettings = {};
  if (file === undefined) return settings;

  const branch = typeof file.targetBranch === 'string' ? file.targetBranch.trim() : '';
  if (branch.length > 0) settings.targetBranch = branch;

  if (Array.isArray(file.semgrepConfig))
    settings.semgrepConfigs = file.semgrepConfig
      .filter((entry): entry is string => typeof entry === 'string')
      .map((entry) => entry.trim())
      .filter((entry) => entry.length > 0);

  return settings;
}
