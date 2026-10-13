/**
 * Déclaration du serveur MCP embarqué aux clients qui ne lisent pas le registre de VS Code.
 * `registerMcpServerDefinitionProvider` alimente l'hôte MCP de l'éditeur — Copilot agent
 * mode — et lui seul : Claude Code découvre ses serveurs dans sa propre configuration,
 * même lancé depuis le terminal intégré. Le bundle est le même, il faut le lui déclarer.
 *
 * Ce module ne construit que des valeurs : le fichier écrit et le processus lancé sont
 * l'affaire de `claude-setup.ts`, qui dépend de l'éditeur. La séparation existe pour que
 * `claude-client.test.ts` puisse rejouer la forme de la configuration — en particulier
 * qu'aucun jeton n'entre dans un fichier versionné.
 */

/** Là où Claude Code range le serveur. `user` porte le jeton, `project` est versionné. */
export type ClaudeScope = 'user' | 'project';

export type Runtime = { command: string; args: string[]; env: Record<string, string> };

export type ServerEntry = { type: 'stdio'; command: string; args: string[]; env: Record<string, string> };

export const SERVER_NAME = 'codeinsights';
export const TOKEN_VARIABLE = 'CODEINSIGHTS_TOKEN';
export const PROJECT_FILE = '.mcp.json';

/**
 * `.mcp.json` est commité et partagé par l'équipe : le jeton n'y entre jamais. Claude Code
 * développe `${VAR}` depuis l'environnement au démarrage du serveur, donc le secret reste
 * dans le shell de chacun — un jeton porte les droits de son propriétaire, pas ceux du dépôt.
 */
export const TOKEN_PLACEHOLDER = `\${${TOKEN_VARIABLE}}`;

/**
 * Le serveur tel que Claude le lancera. `environment` vient de `serverEnvironment()` : les
 * mêmes variables que reçoit le serveur embarqué, pour que l'agent et l'IDE analysent avec
 * les mêmes règles. Sans jeton, la valeur est le marqueur, jamais une chaîne vide — qui
 * ferait démarrer un serveur répondant « jeton absent » à chaque outil.
 */
export function serverEntry(runtime: Runtime, environment: Record<string, string>, token: string | undefined): ServerEntry {
  return {
    type: 'stdio',
    command: runtime.command,
    args: [...runtime.args],
    env: { ...runtime.env, ...environment, [TOKEN_VARIABLE]: token ?? TOKEN_PLACEHOLDER },
  };
}

/** Vrai quand l'entrée porte un jeton en clair : interdit dans un fichier versionné. */
export function carriesToken(entry: ServerEntry): boolean {
  const value = entry.env[TOKEN_VARIABLE];
  return value !== undefined && value !== TOKEN_PLACEHOLDER;
}

/** `claude mcp add-json` écrase une entrée existante : la commande est aussi la mise à jour. */
export function addJsonArguments(entry: ServerEntry, scope: ClaudeScope): string[] {
  return ['mcp', 'add-json', SERVER_NAME, JSON.stringify(entry), '--scope', scope];
}

/** Retrait ciblé : une déconnexion doit emporter le jeton, pas les serveurs des autres. */
export function removeArguments(scope: ClaudeScope): string[] {
  return ['mcp', 'remove', SERVER_NAME, '--scope', scope];
}

/**
 * Le `.mcp.json` du dépôt, l'entrée CodeInsights posée dans ce qui s'y trouve déjà. Un
 * fichier illisible arrête l'écriture : réécrire un fichier de configuration que l'on n'a
 * pas su lire effacerait les serveurs d'un collègue.
 */
export function mergeProjectConfig(existing: string | undefined, entry: ServerEntry): string {
  if (carriesToken(entry)) throw new Error(`${PROJECT_FILE} est versionné : il ne doit porter que ${TOKEN_PLACEHOLDER}.`);
  const document = parseDocument(existing);
  const servers = isRecord(document['mcpServers']) ? document['mcpServers'] : {};
  const merged = { ...document, mcpServers: { ...servers, [SERVER_NAME]: entry } };
  return `${JSON.stringify(merged, undefined, 2)}\n`;
}

function parseDocument(existing: string | undefined): Record<string, unknown> {
  if (existing === undefined || existing.trim().length === 0) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(existing);
  } catch (error) {
    throw new Error(`${PROJECT_FILE} existe mais n'est pas un JSON lisible (${error instanceof Error ? error.message : String(error)}) : le corriger avant de le régénérer.`);
  }
  if (!isRecord(parsed)) throw new Error(`${PROJECT_FILE} existe mais ne contient pas un objet : le corriger avant de le régénérer.`);
  return parsed;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * La même commande, écrite pour un terminal, quand `claude` n'est pas sur le PATH de
 * l'hôte d'extension. Les guillemets simples protègent le JSON — qui porte `"`, `$` et
 * parfois le jeton — aussi bien dans un shell POSIX que dans PowerShell ; seul leur
 * échappement diffère.
 */
export function shellCommand(args: string[], platform: 'win32' | 'posix'): string {
  return ['claude', ...args].map((argument) => quote(argument, platform)).join(' ');
}

function quote(argument: string, platform: 'win32' | 'posix'): string {
  if (/^[A-Za-z0-9_./:=-]+$/.test(argument)) return argument;
  const escaped = platform === 'win32' ? argument.replace(/'/g, "''") : argument.replace(/'/g, String.raw`'\''`);
  return `'${escaped}'`;
}

/**
 * Ce qui décide d'interrompre au démarrage. Chaque « non » porte sa raison : une
 * proposition qui ne s'affiche pas doit s'expliquer dans le journal plutôt que
 * disparaître, sans quoi une notification manquante est indébogable.
 *
 * L'ordre compte. La présence de `claude` est demandée avant l'état de la déclaration
 * parce qu'elle seule dit si l'utilisateur se sert de Claude Code : proposer une
 * configuration Claude à quelqu'un qui ne l'a pas installé est du bruit, et une
 * notification que l'on apprend à ignorer emporte les suivantes.
 */
export type SetupState = {
  /** « Ne plus proposer » a été choisi sur ce poste. */
  dismissed: boolean;
  /** `codeinsights.serverUrl` est renseignée. */
  hubConfigured: boolean;
  /** Un jeton est présent dans le SecretStorage. */
  signedIn: boolean;
  /** La commande `claude` répond sur ce poste. */
  claudeInstalled: boolean;
  /** Claude Code connaît déjà le serveur, quelle que soit la portée. */
  declared: boolean;
};

export type SetupNotice = { propose: true } | { propose: false; reason: string };

export function setupNotice(state: SetupState): SetupNotice {
  if (state.dismissed) return { propose: false, reason: 'proposition écartée sur ce poste' };
  if (!state.hubConfigured) return { propose: false, reason: 'codeinsights.serverUrl est vide' };
  if (!state.signedIn) return { propose: false, reason: 'aucun jeton dans le SecretStorage' };
  if (!state.claudeInstalled) return { propose: false, reason: 'la commande claude est absente de ce poste' };
  if (state.declared) return { propose: false, reason: `le serveur ${SERVER_NAME} est déjà déclaré à Claude Code` };
  return { propose: true };
}

/** Interroger la déclaration coûte un processus : ne le payer que si le reste s'y prête. */
export function shouldQueryClaude(state: Omit<SetupState, 'claudeInstalled' | 'declared'>): boolean {
  return setupNotice({ ...state, claudeInstalled: true, declared: false }).propose;
}
