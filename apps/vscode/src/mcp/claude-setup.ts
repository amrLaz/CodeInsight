import { spawn } from 'node:child_process';
import * as vscode from 'vscode';
import {
  addJsonArguments,
  carriesToken,
  mergeProjectConfig,
  removeArguments,
  serverEntry,
  setupNotice,
  shellCommand,
  shouldQueryClaude,
  PROJECT_FILE,
  SERVER_NAME,
  TOKEN_VARIABLE,
  type ClaudeScope,
  type Runtime,
  type ServerEntry,
} from './claude-client';
import { BUNDLE, settingsEnvironment } from './provider';
import { activeFolder } from '../context/workspace-folder';
import { gitContext } from '../context/git-context';

/**
 * Configure le serveur MCP embarqué pour Claude Code. Rien n'est écrit sans que
 * l'utilisateur l'ait demandé, et rien n'est écrit avant que le serveur ait répondu :
 * une configuration pointant un serveur qui ne démarre pas se lit, côté agent, comme
 * « CodeInsights n'a rien à dire » — le mode d'échec le plus coûteux de cette intégration.
 */

/** Portée configurée par l'utilisateur : ce qui autorise les mises à jour silencieuses. */
const CONFIGURED_SCOPE = 'codeinsights.claude.scope';
/** Version de l'extension au moment de l'écriture : une mise à jour déplace le bundle. */
const CONFIGURED_VERSION = 'codeinsights.claude.version';
/** « Ne plus proposer » : par poste, comme la configuration personnelle qu'il refuse. */
const DISMISSED = 'codeinsights.claude.dismissed';

const HANDSHAKE_TIMEOUT_MS = 15_000;
const CLAUDE_TIMEOUT_MS = 30_000;

export type ClaudeSetup = {
  /** Commande explicite : choisit la portée, vérifie le serveur, écrit la configuration. */
  configure(): Promise<void>;
  /** Le jeton ou les réglages ont changé : réécrit la portée poste de travail. */
  refresh(): Promise<void>;
  /** Activation : le bundle a changé de place si l'extension a été mise à jour. */
  upgrade(): Promise<void>;
  /** Activation : propose la configuration quand Claude Code est là sans le serveur. */
  prompt(): Promise<void>;
};

export function claudeSetup(
  context: vscode.ExtensionContext,
  token: () => Promise<string | undefined>,
  log: (message: string) => void,
): ClaudeSetup {
  const version = typeof context.extension.packageJSON.version === 'string' ? context.extension.packageJSON.version : '0';

  return {
    configure: () => configure(context, token, log),
    refresh: async () => {
      // Seule la portée poste de travail porte le jeton, donc elle seule périme. Le
      // `.mcp.json` du dépôt est commité et partagé : le réécrire sur un changement de
      // réglage local imposerait à l'équipe une décision qui n'a pas été prise.
      if (context.globalState.get<ClaudeScope>(CONFIGURED_SCOPE) !== 'user') return;
      const secret = await token();
      if (secret === undefined || secret.length === 0) {
        // Déconnexion : laisser l'entrée en place laisserait un jeton révoqué configuré,
        // et Claude échouerait sur chaque outil sans dire pourquoi.
        const removal = await runClaude(removeArguments('user'));
        await context.globalState.update(CONFIGURED_SCOPE, undefined);
        log(removal.ok ? 'Claude Code : serveur CodeInsights retiré (portée utilisateur).' : `Claude Code : retrait impossible — ${removal.message}`);
        return;
      }
      const written = await write(context, 'user', secret, log);
      log(written === undefined ? 'Claude Code : configuration mise à jour.' : `Claude Code : mise à jour impossible — ${written}`);
    },
    prompt: () => prompt(context, token, log),
    upgrade: async () => {
      const scope = context.globalState.get<ClaudeScope>(CONFIGURED_SCOPE);
      if (scope === undefined) return;
      if (context.globalState.get<string>(CONFIGURED_VERSION) === version) return;
      // Le chemin du VSIX porte le numéro de version : sans recopie, la configuration de
      // Claude désignerait le répertoire de la version précédente, supprimé par la mise à jour.
      const secret = scope === 'user' ? await token() : undefined;
      if (scope === 'user' && (secret === undefined || secret.length === 0)) return;
      const failure = await write(context, scope, secret, log);
      if (failure !== undefined) {
        log(`Claude Code : configuration non mise à jour après la montée de version — ${failure}`);
        return;
      }
      await context.globalState.update(CONFIGURED_VERSION, version);
      log(`Claude Code : configuration réalignée sur la version ${version}.`);
    },
  };
}

async function configure(context: vscode.ExtensionContext, token: () => Promise<string | undefined>, log: (message: string) => void): Promise<void> {
  if (serverUrl().length === 0) {
    await vscode.commands.executeCommand('workbench.action.openSettings', 'codeinsights.serverUrl');
    return;
  }

  const scope = await pickScope();
  if (scope === undefined) return;

  const secret = await token();
  if (scope === 'user' && (secret === undefined || secret.length === 0)) {
    const choice = await vscode.window.showWarningMessage('CodeInsights : se connecter avant de configurer Claude Code.', 'Se connecter');
    if (choice !== undefined) await vscode.commands.executeCommand('codeinsights.signIn');
    return;
  }

  const failure = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: 'CodeInsights : configuration de Claude Code' },
    async (progress) => {
      progress.report({ message: 'vérification du serveur' });
      return write(context, scope, scope === 'user' ? secret : undefined, log);
    },
  );
  if (failure !== undefined) {
    void vscode.window.showErrorMessage(`CodeInsights : ${failure}`);
    log(`Claude Code : configuration abandonnée — ${failure}`);
    return;
  }

  await context.globalState.update(CONFIGURED_SCOPE, scope);
  await context.globalState.update(CONFIGURED_VERSION, context.extension.packageJSON.version);
  // Configurer depuis la palette après avoir écarté la proposition rouvre celle-ci : sans
  // cela, « ne plus proposer » serait irrévocable et ce refus n'a pas de réglage.
  await context.globalState.update(DISMISSED, undefined);
  announce(scope);
}

/**
 * Au démarrage : Claude Code est installé, le hub est configuré, mais le serveur ne lui
 * est pas déclaré. Une notification, deux réponses, et le silence ensuite si elle est
 * écartée — une proposition qui revient à chaque démarrage s'apprend à être ignorée, et
 * ce qu'on ignore emporte les notifications qui comptent.
 */
async function prompt(context: vscode.ExtensionContext, token: () => Promise<string | undefined>, log: (message: string) => void): Promise<void> {
  const secret = await token();
  const before = {
    dismissed: context.globalState.get<boolean>(DISMISSED) === true,
    hubConfigured: serverUrl().length > 0,
    signedIn: secret !== undefined && secret.length > 0,
  };

  let claudeInstalled = true;
  let declared = false;
  if (shouldQueryClaude(before)) {
    // Un seul processus répond aux deux questions : « claude » existe-t-il, et connaît-il
    // déjà le serveur ? L'état écrit dans globalState ne suffit pas — l'utilisateur a pu
    // retirer l'entrée lui-même, et la proposition doit alors revenir.
    const known = await runClaude(['mcp', 'get', SERVER_NAME], await root());
    claudeInstalled = !known.missing;
    declared = known.ok;
    if (claudeInstalled && !declared && context.globalState.get<ClaudeScope>(CONFIGURED_SCOPE) !== undefined) {
      // Retrait délibéré : oublier la portée, sinon le prochain changement de jeton
      // redéclarerait un serveur que l'utilisateur venait d'enlever.
      await context.globalState.update(CONFIGURED_SCOPE, undefined);
      log('Claude Code : le serveur a été retiré hors de l’extension ; la portée enregistrée est oubliée.');
    }
  }

  const notice = setupNotice({ ...before, claudeInstalled, declared });
  if (!notice.propose) {
    log(`Claude Code : configuration non proposée — ${notice.reason}.`);
    return;
  }

  const choice = await vscode.window.showInformationMessage(
    'CodeInsights : le serveur MCP n’est pas déclaré à Claude Code. Ses sept outils y sont indisponibles.',
    'Configurer',
    'Ne plus proposer',
  );
  if (choice === 'Configurer') {
    await vscode.commands.executeCommand('codeinsights.configureMcpClient');
    return;
  }
  if (choice === 'Ne plus proposer') {
    await context.globalState.update(DISMISSED, true);
    log('Claude Code : proposition écartée sur ce poste. La commande reste dans la palette.');
    return;
  }
  // Notification fermée sans réponse : reproposée au prochain démarrage, pas oubliée.
  log('Claude Code : proposition laissée sans réponse.');
}

/**
 * Installe le bundle, vérifie qu'il parle MCP, puis écrit. Rend le message d'échec, ou
 * `undefined` si tout est en place — les appels silencieux n'ont rien à afficher.
 */
async function write(context: vscode.ExtensionContext, scope: ClaudeScope, secret: string | undefined, log: (message: string) => void): Promise<string | undefined> {
  let entry: ServerEntry;
  try {
    const runtime = await installBundle(context);
    // Les réglages sont ceux du dossier ouvert, pas ceux du poste : c'est ce projet-là que
    // l'utilisateur configure, et ses règles semgrep comme sa branche cible lui appartiennent.
    const resource = activeFolder()?.uri;
    entry = serverEntry(runtime, settingsEnvironment(serverUrl(), resource), secret);
  } catch (error) {
    return `serveur MCP introuvable dans le paquet (${message(error)}).`;
  }

  const refused = await handshake(entry);
  if (refused !== undefined) return refused;

  return scope === 'user' ? await writeUserScope(entry, log) : await writeProjectScope(entry);
}

async function writeUserScope(entry: ServerEntry, log: (message: string) => void): Promise<string | undefined> {
  const args = addJsonArguments(entry, 'user');
  const result = await runClaude(args);
  if (result.ok) {
    log(`Claude Code : serveur ${SERVER_NAME} déclaré en portée utilisateur.`);
    return undefined;
  }
  if (!result.missing) return `Claude Code a refusé la configuration (${result.message}).`;

  // `claude` absent du PATH de l'hôte d'extension. La commande est proposée dans un
  // terminal sans être exécutée : elle porte le jeton, et le choix de la faire entrer
  // dans l'historique du shell appartient à l'utilisateur.
  const choice = await vscode.window.showWarningMessage(
    'CodeInsights : la commande « claude » est introuvable. Ouvrir un terminal avec la commande de configuration ?',
    'Ouvrir un terminal',
  );
  if (choice === undefined) return 'commande « claude » introuvable : configuration non écrite.';
  const terminal = vscode.window.createTerminal('CodeInsights — Claude Code');
  terminal.show();
  terminal.sendText(shellCommand(args, process.platform === 'win32' ? 'win32' : 'posix'), false);
  return undefined;
}

async function writeProjectScope(entry: ServerEntry): Promise<string | undefined> {
  if (carriesToken(entry)) return `${PROJECT_FILE} est versionné et ne doit pas porter de jeton.`;
  const root = await projectRoot();
  if (root === undefined) return 'ouvrir un dépôt Git pour écrire un ' + PROJECT_FILE + ' de projet.';
  const file = vscode.Uri.joinPath(root, PROJECT_FILE);

  let existing: string | undefined;
  try {
    existing = new TextDecoder().decode(await vscode.workspace.fs.readFile(file));
  } catch {
    existing = undefined;
  }
  if (existing !== undefined) {
    const confirmed = await vscode.window.showWarningMessage(
      `CodeInsights va réécrire ${PROJECT_FILE} en y posant le serveur ${SERVER_NAME}. Les autres serveurs déclarés sont conservés.`,
      { modal: true },
      'Écrire',
    );
    if (confirmed === undefined) return `${PROJECT_FILE} laissé intact.`;
  }

  let content: string;
  try {
    content = mergeProjectConfig(existing, entry);
  } catch (error) {
    return message(error);
  }
  await vscode.workspace.fs.writeFile(file, new TextEncoder().encode(content));
  return undefined;
}

function announce(scope: ClaudeScope): void {
  if (scope === 'user') {
    void vscode.window.showInformationMessage('CodeInsights : Claude Code est configuré sur ce poste. Les sept outils sont disponibles dans tout dépôt suivi par le hub.');
    return;
  }
  void vscode.window.showInformationMessage(
    `CodeInsights : ${PROJECT_FILE} écrit. Le jeton n'y figure pas — définir ${TOKEN_VARIABLE} dans l'environnement avant de lancer Claude Code, et approuver le serveur au premier démarrage.`,
  );
}

async function pickScope(): Promise<ClaudeScope | undefined> {
  const choice = await vscode.window.showQuickPick(
    [
      {
        label: 'Ce poste de travail',
        detail: 'Configuration personnelle de Claude Code, jeton inclus depuis le trousseau de l’extension. Rien à écrire, rien à commiter.',
        scope: 'user' as const,
      },
      {
        label: `Ce dépôt (${PROJECT_FILE})`,
        detail: `Fichier versionné, partagé par l’équipe. Le jeton n’y figure pas : chacun définit ${TOKEN_VARIABLE} dans son environnement.`,
        scope: 'project' as const,
      },
    ],
    { title: 'CodeInsights : configurer Claude Code', placeHolder: 'Où déclarer le serveur MCP ?' },
  );
  return choice?.scope;
}

/**
 * Recopie le bundle hors du répertoire d'installation. Le chemin du VSIX porte le numéro
 * de version — `upcoop.codeinsights-0.5.0` — et disparaît à la mise à jour suivante ;
 * `globalStorageUri` ne bouge pas. Le fichier lancé reste celui du paquet, jamais un `npx`.
 */
async function installBundle(context: vscode.ExtensionContext): Promise<Runtime> {
  const source = vscode.Uri.joinPath(context.extensionUri, 'dist', BUNDLE);
  await vscode.workspace.fs.stat(source);
  await vscode.workspace.fs.createDirectory(context.globalStorageUri);
  const target = vscode.Uri.joinPath(context.globalStorageUri, BUNDLE);
  await vscode.workspace.fs.copy(source, target, { overwrite: true });
  return {
    command: process.execPath,
    args: [target.fsPath],
    // Le Node de l'éditeur : Claude Code peut être installé sans Node sur le PATH, et un
    // serveur MCP qui ne démarre pas ne dit jamais pourquoi à l'agent.
    env: { ELECTRON_RUN_AS_NODE: '1' },
  };
}

/**
 * Parle réellement MCP au serveur avant d'écrire quoi que ce soit : `process.execPath`
 * lancé hors de l'éditeur, c'est ce que fera Claude. Le jeton n'entre pas dans la
 * vérification — `initialize` n'en a pas besoin, et un secret de moins traverse un spawn.
 */
function handshake(entry: ServerEntry): Promise<string | undefined> {
  return new Promise((resolve) => {
    const request = {
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'codeinsights-vscode', version: '1.0.0' } },
    };
    let child: ReturnType<typeof spawn>;
    try {
      child = spawn(entry.command, entry.args, { env: { ...process.env, ...entry.env, [TOKEN_VARIABLE]: '' } });
    } catch (error) {
      resolve(`le serveur MCP n'a pas pu être lancé (${message(error)}).`);
      return;
    }

    let answered = false;
    let out = '';
    let errors = '';
    const done = (failure: string | undefined): void => {
      if (answered) return;
      answered = true;
      clearTimeout(timer);
      child.kill();
      resolve(failure);
    };
    const timer = setTimeout(() => done("le serveur MCP n'a pas répondu en quinze secondes : configuration non écrite."), HANDSHAKE_TIMEOUT_MS);

    child.stdout?.on('data', (chunk: Buffer) => {
      out += chunk.toString('utf8');
      const lines = out.split('\n');
      out = lines.pop() ?? '';
      for (const line of lines) {
        if (line.trim().length === 0) continue;
        try {
          const parsed: unknown = JSON.parse(line);
          if (typeof parsed === 'object' && parsed !== null && 'id' in parsed && (parsed as { id: unknown }).id === 1 && 'result' in parsed) done(undefined);
        } catch {
          // Une ligne partielle ou un journal égaré sur stdout : la suite tranchera.
        }
      }
    });
    child.stderr?.on('data', (chunk: Buffer) => {
      errors += chunk.toString('utf8');
    });
    child.on('error', (error) => done(`le serveur MCP n'a pas pu être lancé (${error.message}).`));
    child.on('close', (code) => done(`le serveur MCP s'est arrêté (code ${String(code)}) ${errors.trim().length === 0 ? 'sans rien dire' : `— ${errors.trim().split('\n').slice(-1)[0] ?? ''}`}.`));
    child.stdin?.write(`${JSON.stringify(request)}\n`);
  });
}

type ClaudeResult = { ok: boolean; missing: boolean; message: string };

function runClaude(args: string[], cwd?: string): Promise<ClaudeResult> {
  return new Promise((resolve) => {
    let child: ReturnType<typeof spawn>;
    try {
      // Le répertoire compte : `claude mcp get` résout aussi les portées locale et projet,
      // qui sont attachées au dépôt et non au poste.
      child = spawn('claude', args, cwd === undefined ? {} : { cwd });
    } catch (error) {
      resolve({ ok: false, missing: true, message: message(error) });
      return;
    }
    let errors = '';
    let out = '';
    let answered = false;
    const done = (result: ClaudeResult): void => {
      if (answered) return;
      answered = true;
      clearTimeout(timer);
      child.kill();
      resolve(result);
    };
    // `claude mcp get` démarre le serveur pour l'ausculter : la réponse peut tarder, et un
    // appel qui ne revient jamais laisserait la proposition du démarrage muette.
    const timer = setTimeout(() => done({ ok: false, missing: false, message: `« claude ${args[0] ?? ''} » n'a pas répondu en trente secondes.` }), CLAUDE_TIMEOUT_MS);
    child.stdout?.on('data', (chunk: Buffer) => {
      out += chunk.toString('utf8');
    });
    child.stderr?.on('data', (chunk: Buffer) => {
      errors += chunk.toString('utf8');
    });
    child.on('error', (error: NodeJS.ErrnoException) => done({ ok: false, missing: error.code === 'ENOENT', message: error.message }));
    child.on('close', (code) => done({ ok: code === 0, missing: false, message: (errors.trim().length === 0 ? out : errors).trim().split('\n').slice(-1)[0] ?? `code ${String(code)}` }));
  });
}

/** Le chemin du dépôt, quand il y en a un : `claude` y résout ses portées locale et projet. */
async function root(): Promise<string | undefined> {
  return (await projectRoot())?.fsPath;
}

/** Claude Code lit `.mcp.json` à la racine du répertoire où il est lancé : celle du dépôt. */
async function projectRoot(): Promise<vscode.Uri | undefined> {
  const folder = activeFolder();
  if (folder === undefined) return undefined;
  const git = await gitContext(folder);
  return git === undefined ? folder.uri : vscode.Uri.file(git.rootPath);
}

function serverUrl(): string {
  return vscode.workspace.getConfiguration('codeinsights', activeFolder()?.uri).get<string>('serverUrl', '').trim();
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
