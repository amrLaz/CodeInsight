import { ApiError, CodeInsightsApi } from '../../core/src/api/client';
import { latestAnalysis, projectsForRepository, type RawAnalysis } from '../../core/src/api/codeinsights';
import { projectSettings } from '../../core/src/context/project-file';
import type { ProjectKeySources } from '../../core/src/context/project-key';
import { resolutionLabel, resolveProject } from '../../core/src/context/project-resolution';
import { apiProblem, configProblem, type Config, type LocalDefaults } from './config';
import { projectConfig, type ProjectConfig } from './project-config';
import { currentBranch, GitUnavailable, remoteUrl, repositoryRoot } from './workspace';

/**
 * From a working directory to the analysis that describes it. The agent never names a
 * project: la configuration du dépôt puis `git remote` → `repo_url` → projet, c'est la
 * chaîne que l'extension parcourt déjà, et demander sa clé à un agent la lui fait inventer.
 */

/** A failure the agent can act on. Its message is the whole answer of the tool. */
export class ToolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ToolError';
  }
}

export type Target = {
  rootPath: string;
  branch: string;
  projectKey: string;
  /**
   * Réglages d'analyse locale effectifs : ceux que le dépôt déclare, à défaut ceux de
   * l'environnement. L'environnement est écrit une fois par l'extension, pour le projet
   * ouvert ce jour-là ; en portée utilisateur il suit l'agent dans tous les autres dépôts.
   */
  local: LocalDefaults;
  /** null when the branch exists locally but the hub holds no analysis for it. */
  analysis: RawAnalysis | null;
};

export function client(config: Config): CodeInsightsApi {
  const problem = configProblem(config);
  if (problem !== undefined) throw new ToolError(problem);
  return new CodeInsightsApi({ serverUrl: config.serverUrl, token: async () => config.token });
}

/** Reports what the hub refused in terms of what to do about it. */
export function fail(error: unknown): never {
  if (error instanceof ToolError) throw error;
  if (error instanceof ApiError) throw new ToolError(apiProblem(error.status, error.message));
  if (error instanceof GitUnavailable)
    throw new ToolError(`${error.message} L'analyse locale et la résolution du projet lisent le dépôt avec « git » : l'installer ou l'ajouter au PATH.`);
  throw new ToolError(error instanceof Error ? error.message : String(error));
}

// Ni la configuration d'un dossier ni l'URL de son clone ne changent pendant une session ;
// la résolution coûte alors un appel HTTP par dossier, et non un par appel d'outil.
const projects = new Map<string, string>();

export async function resolveTarget(api: CodeInsightsApi, config: Config, cwd: string, branch?: string): Promise<Target> {
  const rootPath = await repositoryRoot(cwd);
  if (rootPath === undefined)
    throw new ToolError(`« ${cwd} » n'est pas dans un dépôt Git. CodeInsights identifie le projet par l'URL du dépôt : ouvrir le serveur sur un clone.`);
  const requested = branch?.trim();
  const checkedOut = await currentBranch(rootPath);
  const target = requested !== undefined && requested.length > 0 ? requested : checkedOut;
  if (target === undefined)
    throw new ToolError(`Aucune branche courante dans ${rootPath} (HEAD détaché). Préciser « branch », ou revenir sur une branche.`);

  // Le dossier configuré, pas le clone : un monorepo porte plusieurs projets, et c'est
  // celui où l'agent travaille qui doit répondre.
  const project = await projectConfig(cwd, rootPath);
  const cached = projects.get(project.path);
  const projectKey = cached ?? (await lookupProject(api, project, rootPath));
  projects.set(project.path, projectKey);

  const analysis = await latestAnalysis(api, projectKey, target);
  return { rootPath, branch: target, projectKey, local: localSettings(config, project), analysis };
}

/**
 * La branche de référence et le jeu de règles du dépôt l'emportent sur l'environnement :
 * ils doivent être ceux de la CI, et un serveur déclaré en portée utilisateur porte ceux
 * du projet qui était ouvert quand l'extension l'a écrit — pas ceux de ce dépôt-ci.
 */
function localSettings(config: Config, project: ProjectConfig): LocalDefaults {
  const declared = projectSettings(project.file);
  return {
    ...config.local,
    targetBranch: declared.targetBranch ?? config.local.targetBranch,
    semgrepConfigs: declared.semgrepConfigs ?? config.local.semgrepConfigs,
  };
}

async function lookupProject(api: CodeInsightsApi, project: ProjectConfig, rootPath: string): Promise<string> {
  const url = await remoteUrl(rootPath);
  const sources: ProjectKeySources = { folderName: project.folderName };
  if (project.file !== undefined) sources.mapping = project.file;
  if (url !== undefined) sources.repositoryUrl = url;
  const resolution = await resolveProject(sources, (repositoryUrl) => projectsForRepository(api, repositoryUrl));
  if (resolution.state === 'resolved') return resolution.projectKey;
  throw new ToolError(
    `Projet CodeInsights non résolu pour ${project.path} : ${resolutionLabel(resolution)}. Nommer le projet dans .codeinsights/vscode.json, ou lier le dépôt à son projet dans le hub (Administration → intégration).`,
  );
}

/** The analysis or the reason there is none — never a verdict invented for an empty branch. */
export function requireAnalysis(target: Target): RawAnalysis {
  if (target.analysis === null)
    throw new ToolError(
      `Aucune analyse pour ${target.projectKey} · ${target.branch}. Le hub ne contient que ce que la CI y publie : lancer la pipeline sur cette branche, ou interroger la branche cible.`,
    );
  return target.analysis;
}
