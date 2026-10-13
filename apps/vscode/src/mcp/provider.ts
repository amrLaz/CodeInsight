import * as vscode from 'vscode';
import { serverEnvironment, type LiveAnalysisSettings } from './environment';
import { activeFolder } from '../context/workspace-folder';

/**
 * Déclare le serveur MCP embarqué aux hôtes de l'éditeur. L'utilisateur n'écrit aucune
 * configuration et ne manipule aucun secret : l'URL vient des réglages, le jeton du
 * SecretStorage, et le fichier lancé est celui du VSIX.
 */

export const PROVIDER_ID = 'codeinsights.mcp';

// `.mjs` : le paquet de l'extension est CommonJS, et Node refuserait de lire un `.js`
// ESM sans le reparser — un avertissement dans le journal du serveur à chaque démarrage.
export const BUNDLE = 'mcp-server.mjs';

export type McpRegistration = { disposable: vscode.Disposable; refresh: () => void };

export function registerMcpServer(
  context: vscode.ExtensionContext,
  token: () => Promise<string | undefined>,
  log: (message: string) => void,
): McpRegistration | undefined {
  // Disponible depuis VS Code 1.101 ; un fork peut ne pas l'avoir. Même garde d'existence
  // que le participant de chat : l'extension se dégrade au lieu de refuser de s'activer.
  const register = vscode.lm?.registerMcpServerDefinitionProvider;
  if (typeof register !== 'function') {
    log('Serveur MCP non déclaré : cet éditeur n’expose pas registerMcpServerDefinitionProvider.');
    return undefined;
  }
  const bundle = vscode.Uri.joinPath(context.extensionUri, 'dist', BUNDLE);
  const changed = new vscode.EventEmitter<void>();
  const version = typeof context.extension.packageJSON.version === 'string' ? context.extension.packageJSON.version : undefined;

  const disposable = register.call(vscode.lm, PROVIDER_ID, {
    onDidChangeMcpServerDefinitions: changed.event,
    provideMcpServerDefinitions: async () => {
      // `liveAnalysis.*` et `projectKey` sont déclarés « resource » : lus sans dossier, ils
      // rendent la valeur du poste et non celle du projet ouvert. Le serveur analyserait
      // alors avec la branche cible et les règles d'un autre dépôt, sans jamais le dire.
      const resource = activeFolder()?.uri;
      const url = configuration(resource).get<string>('serverUrl', '').trim();
      if (url.length === 0) {
        log('Serveur MCP non déclaré : codeinsights.serverUrl est vide.');
        return [];
      }
      try {
        await vscode.workspace.fs.stat(bundle);
      } catch {
        // Un VSIX sans son bundle ne doit pas déclarer un serveur qui ne démarrera pas.
        log(`Serveur MCP non déclaré : ${BUNDLE} est absent du paquet.`);
        return [];
      }
      const definition = new vscode.McpStdioServerDefinition(
        'CodeInsights',
        // Le Node de l'éditeur : rien à installer sur le poste. `ELECTRON_RUN_AS_NODE`
        // fait exécuter le script au binaire de l'éditeur au lieu d'ouvrir une fenêtre.
        process.execPath,
        [bundle.fsPath],
        { ...settingsEnvironment(url, resource), ELECTRON_RUN_AS_NODE: '1' },
        version,
      );
      return [definition];
    },
    // Le jeton n'est lu qu'ici : `provideMcpServerDefinitions` est appelé sans interaction
    // possible, et le contrat de l'API interdit d'y demander une authentification.
    resolveMcpServerDefinition: async (server) => {
      if (!(server instanceof vscode.McpStdioServerDefinition)) return server;
      const secret = await token();
      if (secret === undefined || secret.length === 0) {
        void vscode.window
          .showWarningMessage('CodeInsights : se connecter avant de démarrer le serveur MCP.', 'Se connecter')
          .then((choice) => {
            if (choice !== undefined) void vscode.commands.executeCommand('codeinsights.signIn');
          });
        // Démarrer sans jeton ferait répondre « jeton absent » à chaque outil : l'hôte
        // doit plutôt savoir que le serveur n'est pas démarrable en l'état.
        return undefined;
      }
      server.env = { ...server.env, CODEINSIGHTS_TOKEN: secret };
      return server;
    },
  });

  return {
    disposable: vscode.Disposable.from(disposable, changed),
    // Les outils sont lancés avec l'environnement du moment : après un changement de
    // réglage ou de jeton, l'hôte doit redémarrer le serveur pour en tenir compte.
    refresh: () => changed.fire(),
  };
}

function configuration(resource?: vscode.Uri): vscode.WorkspaceConfiguration {
  return vscode.workspace.getConfiguration('codeinsights', resource);
}

/** Les réglages du dossier passé, faute de quoi ceux du poste : un multi-root en a un par dépôt. */
export function settingsEnvironment(url: string, resource?: vscode.Uri): Record<string, string> {
  const live = configuration(resource);
  const settings: LiveAnalysisSettings = {
    targetBranch: live.get<string>('liveAnalysis.targetBranch', 'origin/master'),
    semgrepConfig: live.get<string[]>('liveAnalysis.semgrepConfig', []).filter((entry) => entry.trim().length > 0),
    useDocker: live.get<boolean>('liveAnalysis.useDocker', false),
    images: live.get<{ semgrep: string; gitleaks: string }>('liveAnalysis.images', {
      semgrep: 'semgrep/semgrep:1.145.0',
      gitleaks: 'zricethezav/gitleaks:v8.30.0',
    }),
    timeoutSeconds: live.get<number>('liveAnalysis.timeoutSeconds', 120),
    maxFiles: live.get<number>('liveAnalysis.maxFiles', 100),
  };
  return serverEnvironment(url, settings);
}
