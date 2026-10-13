/**
 * Traduit les réglages du workspace dans les variables que lit le serveur MCP
 * (`apps/mcp/src/config.ts`). C'est le seul canal entre l'extension et le serveur :
 * `mcp-definition.test.ts` rejoue ce passage des deux côtés, parce qu'un nom de
 * variable renommé d'un seul côté ne casse rien de visible — le serveur repartirait
 * simplement sur ses défauts, et l'agent analyserait avec d'autres règles que l'IDE.
 */

export type LiveAnalysisSettings = {
  targetBranch: string;
  semgrepConfig: string[];
  useDocker: boolean;
  images: { semgrep: string; gitleaks: string };
  timeoutSeconds: number;
  maxFiles: number;
};

export function serverEnvironment(serverUrl: string, settings: LiveAnalysisSettings): Record<string, string> {
  return {
    CODEINSIGHTS_URL: serverUrl,
    CODEINSIGHTS_TARGET_BRANCH: settings.targetBranch,
    // Une liste ne traverse pas un environnement : le serveur redécoupe sur virgule et
    // espaces, ce qui exclut un chemin de règles contenant une espace — comme aujourd'hui.
    CODEINSIGHTS_SEMGREP_CONFIG: settings.semgrepConfig.join(','),
    CODEINSIGHTS_USE_DOCKER: settings.useDocker ? '1' : '0',
    CODEINSIGHTS_SEMGREP_IMAGE: settings.images.semgrep,
    CODEINSIGHTS_GITLEAKS_IMAGE: settings.images.gitleaks,
    CODEINSIGHTS_TIMEOUT_SECONDS: String(settings.timeoutSeconds),
    CODEINSIGHTS_MAX_FILES: String(settings.maxFiles),
  };
}
