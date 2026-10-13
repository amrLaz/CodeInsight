import { readFile } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';
import type { ProjectFile } from '../../core/src/context/project-file';

/**
 * La configuration que le dépôt porte sur lui-même : `.codeinsights/vscode.json`, le
 * fichier que lit déjà l'extension VS Code. Sans elle, l'agent ne saurait nommer le projet
 * que par l'URL du clone, alors que l'IDE, lui, obéit au dossier ouvert — deux verdicts
 * possibles pour un même dépôt, et rien à l'écran pour le dire.
 */

const DIRECTORY = '.codeinsights';
const FILE = 'vscode.json';

/** Le dossier qui porte la configuration, et ce qu'elle déclare. */
export type ProjectConfig = {
  /** Racine du dossier configuré, ou celle du clone à défaut. Identifie le projet en cache. */
  path: string;
  /** Nom de ce dossier, tel que l'extension le passe : une clé de `projects` peut le viser. */
  folderName: string;
  file?: ProjectFile;
};

/**
 * Cherche la configuration depuis `cwd`, puis en remontant jusqu'à la racine du clone. Un
 * monorepo pose un fichier par dossier livrable ; l'agent travaille dans l'un d'eux, et
 * c'est celui-là qui doit répondre, pas la racine qui les contient tous.
 */
export async function projectConfig(cwd: string, rootPath: string): Promise<ProjectConfig> {
  const root = resolve(rootPath);
  let directory = resolve(cwd);
  for (;;) {
    const file = await readFileAt(join(directory, DIRECTORY, FILE));
    if (file !== undefined) return { path: directory, folderName: basename(directory), file };
    if (directory === root) break;
    const parent = dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
  return { path: root, folderName: basename(root) };
}

async function readFileAt(path: string): Promise<ProjectFile | undefined> {
  let raw: string;
  try {
    raw = await readFile(path, 'utf8');
  } catch {
    return undefined;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    // Un fichier illisible n'est pas une absence de configuration : retomber en silence sur
    // l'URL du dépôt nommerait peut-être un autre projet que celui que l'IDE affiche.
    throw new Error(`${path} est illisible (${error instanceof Error ? error.message : String(error)}). Corriger ce fichier, ou le retirer pour résoudre le projet par l'URL du dépôt.`);
  }
  return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as ProjectFile) : undefined;
}
