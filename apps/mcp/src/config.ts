/**
 * Configuration read from the environment. The embedded VSIX path passes the hub URL and
 * the token of the extension's SecretStorage; the npm path reads them from `.mcp.json`.
 * Nothing is defaulted to a hostname or a token: an agent silently querying the wrong hub
 * would report another project's verdict as this one's.
 */

export type Config = {
  serverUrl: string;
  token: string | undefined;
  /** Findings returned when the agent does not ask for a size. */
  defaultLimit: number;
  /** Hard ceiling: past this, a listing floods the agent's context and degrades it. */
  maxLimit: number;
  /** Defaults of the local run, mirroring `codeinsights.liveAnalysis.*` of the extension. */
  local: LocalDefaults;
};

export type LocalDefaults = {
  targetBranch: string;
  semgrepConfigs: string[];
  useDocker: boolean;
  images: { semgrep: string; gitleaks: string };
  timeoutMs: number;
  maxFiles: number;
};

// Pinned exactly like the extension's defaults: a workstation run must scan with the same
// rules as the pipeline, and an image floating on a tag would silently change the verdict.
const SEMGREP_IMAGE = 'semgrep/semgrep:1.145.0';
const GITLEAKS_IMAGE = 'zricethezav/gitleaks:v8.30.0';

export const DEFAULT_LIMIT = 30;
export const MAX_LIMIT = 200;

function positive(raw: string | undefined, fallback: number, ceiling: number): number {
  const value = Number(raw);
  if (!Number.isFinite(value) || value <= 0) return fallback;
  return Math.min(Math.floor(value), ceiling);
}

/** Comma- or whitespace-separated list, as a setting array cannot cross an environment. */
function list(raw: string | undefined): string[] {
  return (raw ?? '')
    .split(/[,\s]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

function flag(raw: string | undefined): boolean {
  const value = (raw ?? '').trim().toLowerCase();
  return value === '1' || value === 'true' || value === 'yes';
}

export function readConfig(env: Record<string, string | undefined>): Config {
  const token = env.CODEINSIGHTS_TOKEN?.trim();
  return {
    serverUrl: env.CODEINSIGHTS_URL?.trim() ?? '',
    token: token === undefined || token.length === 0 ? undefined : token,
    defaultLimit: positive(env.CODEINSIGHTS_LIMIT, DEFAULT_LIMIT, MAX_LIMIT),
    maxLimit: MAX_LIMIT,
    local: {
      targetBranch: env.CODEINSIGHTS_TARGET_BRANCH?.trim() || 'origin/master',
      semgrepConfigs: list(env.CODEINSIGHTS_SEMGREP_CONFIG),
      useDocker: flag(env.CODEINSIGHTS_USE_DOCKER),
      images: {
        semgrep: env.CODEINSIGHTS_SEMGREP_IMAGE?.trim() || SEMGREP_IMAGE,
        gitleaks: env.CODEINSIGHTS_GITLEAKS_IMAGE?.trim() || GITLEAKS_IMAGE,
      },
      timeoutMs: positive(env.CODEINSIGHTS_TIMEOUT_SECONDS, 120, 900) * 1000,
      maxFiles: positive(env.CODEINSIGHTS_MAX_FILES, 100, 1000),
    },
  };
}

/** What is missing before any tool can answer, said in terms the agent can act on. */
export function configProblem(config: Config): string | undefined {
  if (config.serverUrl.length === 0)
    return "L'URL du hub CodeInsights n'est pas configurée : définir CODEINSIGHTS_URL (par exemple https://codeinsights.example) dans la configuration du serveur MCP.";
  if (config.token === undefined)
    return "Aucun jeton CodeInsights : créer un jeton personnel depuis le hub (Compte → jetons, scope analysis:read) et le passer dans CODEINSIGHTS_TOKEN.";
  return undefined;
}

/** Turns an API failure into an instruction, not a status code. */
export function apiProblem(status: number, message: string): string {
  if (status === 401)
    return `Jeton refusé par le hub (${message}). Un jeton personnel expire : en créer un nouveau depuis le hub (Compte → jetons, scope analysis:read) et mettre à jour CODEINSIGHTS_TOKEN.`;
  if (status === 403) return `Accès refusé à ce projet (${message}). Le jeton porte les droits de son propriétaire : demander l'accès au projet plutôt que d'élargir le jeton.`;
  if (status === 404) return `Ressource absente du hub (${message}).`;
  if (status === 410) return `Les détails de cette analyse ont été purgés par la rétention (${message}). Relancer une analyse pour disposer à nouveau des findings.`;
  if (status === 0) return message;
  return `Hub CodeInsights indisponible (${message}).`;
}
