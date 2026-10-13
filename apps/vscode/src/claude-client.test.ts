import { test } from 'vitest';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';

test('claude-client', async () => {

/**
 * La configuration écrite pour Claude Code. Deux propriétés ne peuvent pas être vérifiées
 * à l'œil : qu'un jeton n'entre jamais dans le fichier versionné du dépôt, et que l'agent
 * reçoive exactement les réglages du serveur embarqué — sans quoi l'IDE et l'agent
 * analyseraient le même dépôt avec des règles différentes, sans rien signaler.
 */
const {
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
  TOKEN_PLACEHOLDER,
  TOKEN_VARIABLE,
} = await import('./mcp/claude-client');
const { serverEnvironment } = await import('./mcp/environment');
const { readConfig } = await import('../../mcp/src/config');

const settings = {
  targetBranch: 'origin/develop',
  semgrepConfig: ['.codeinsights/semgrep-mqr-v1.json'],
  useDocker: true,
  images: { semgrep: 'semgrep/semgrep:1.145.0', gitleaks: 'zricethezav/gitleaks:v8.30.0' },
  timeoutSeconds: 90,
  maxFiles: 42,
};
const environment = serverEnvironment('https://hub.example', settings);
const runtime = { command: '/Applications/Code.app/Contents/MacOS/Electron', args: ['/global/storage/mcp-server.mjs'], env: { ELECTRON_RUN_AS_NODE: '1' } };

const personal = serverEntry(runtime, environment, 'pat-secret');
const shared = serverEntry(runtime, environment, undefined);

// Le serveur déclaré à Claude est lancé comme celui de l'éditeur : le Node de l'éditeur,
// le bundle du paquet. Un `npx` résolu au démarrage échouerait sur un poste hors ligne.
assert.equal(personal.type, 'stdio');
assert.equal(personal.command, runtime.command);
assert.deepEqual(personal.args, runtime.args);
assert.equal(personal.env.ELECTRON_RUN_AS_NODE, '1');

// La propriété qui compte : le fichier du dépôt est commité, le jeton n'y entre jamais.
assert.equal(carriesToken(personal), true);
assert.equal(carriesToken(shared), false);
assert.equal(shared.env[TOKEN_VARIABLE], TOKEN_PLACEHOLDER);
assert.equal(personal.env[TOKEN_VARIABLE], 'pat-secret');
assert.throws(() => mergeProjectConfig(undefined, personal), /versionné/, `${PROJECT_FILE} doit refuser une entrée portant un jeton`);

// Une variable absente n'est pas une chaîne vide : sans marqueur, Claude lancerait un
// serveur qui répondrait « jeton absent » à chaque outil au lieu de ne pas démarrer.
assert.notEqual(shared.env[TOKEN_VARIABLE], '');

// L'agent et l'IDE doivent analyser avec les mêmes règles : l'environnement déclaré à
// Claude se relit côté serveur comme celui du fournisseur embarqué.
const config = readConfig(personal.env);
assert.equal(config.serverUrl, 'https://hub.example');
assert.equal(config.token, 'pat-secret');
assert.equal(config.local.targetBranch, settings.targetBranch);
assert.deepEqual(config.local.semgrepConfigs, settings.semgrepConfig);
assert.equal(config.local.useDocker, true);
assert.equal(config.local.maxFiles, settings.maxFiles);
// Le marqueur n'est pas un jeton : développé par Claude, il ne doit jamais l'être ici.
assert.equal(readConfig(shared.env).token, TOKEN_PLACEHOLDER);

// Le fichier du dépôt appartient à l'équipe : les serveurs des autres survivent à l'écriture.
const existing = JSON.stringify({ mcpServers: { autre: { command: 'node', args: ['autre.js'] } }, $schema: 'https://example/schema' });
const merged = JSON.parse(mergeProjectConfig(existing, shared));
assert.deepEqual(Object.keys(merged.mcpServers).sort(), ['autre', SERVER_NAME]);
assert.equal(merged.$schema, 'https://example/schema', 'les clés inconnues du fichier doivent être conservées');
assert.deepEqual(merged.mcpServers[SERVER_NAME], shared);
assert.equal(mergeProjectConfig(undefined, shared).endsWith('\n'), true);
assert.deepEqual(JSON.parse(mergeProjectConfig('   ', shared)).mcpServers[SERVER_NAME], shared);
// Un fichier illisible arrête l'écriture : le réécrire effacerait les serveurs d'un collègue.
assert.throws(() => mergeProjectConfig('{ pas du json', shared), /JSON lisible/);
assert.throws(() => mergeProjectConfig('[]', shared), /objet/);

// La commande écrase l'entrée existante : configurer et mettre à jour sont le même geste.
const args = addJsonArguments(personal, 'user');
assert.deepEqual(args.slice(0, 3), ['mcp', 'add-json', SERVER_NAME]);
assert.deepEqual(args.slice(-2), ['--scope', 'user']);
assert.deepEqual(JSON.parse(args[3]), personal, 'le JSON passé à claude doit être l’entrée telle quelle');
assert.deepEqual(removeArguments('user'), ['mcp', 'remove', SERVER_NAME, '--scope', 'user']);

// Le repli terminal passe par un shell : le JSON porte des guillemets, des accolades et
// parfois le jeton. Un découpage fautif y écrirait une configuration silencieusement fausse.
if (process.platform !== 'win32') {
  const line = shellCommand(args, 'posix');
  assert.equal(line.startsWith('claude '), true);
  const echoed = execFileSync('/bin/sh', ['-c', `for a in ${line.slice('claude '.length)}; do printf '%s\\n' "$a"; done`], { encoding: 'utf8' });
  assert.deepEqual(echoed.split('\n').slice(0, -1), args, 'le shell doit reconstituer exactement les arguments');
}
const quoted = shellCommand(["a'b"], 'win32');
assert.equal(quoted, "claude 'a''b'", 'PowerShell double le guillemet simple au lieu de l’échapper');

// La proposition du démarrage. Une notification qui revient à chaque démarrage s'apprend à
// être ignorée, et ce qu'on ignore emporte les notifications qui comptent : chaque « non »
// doit donc être vérifiable, et porter sa raison pour le journal.
const ready = { dismissed: false, hubConfigured: true, signedIn: true, claudeInstalled: true, declared: false };
assert.deepEqual(setupNotice(ready), { propose: true }, 'Claude installé, hub configuré, serveur absent : c’est le seul cas qui interrompt');
for (const [state, motif] of [
  [{ ...ready, dismissed: true }, /écartée/],
  [{ ...ready, hubConfigured: false }, /serverUrl/],
  [{ ...ready, signedIn: false }, /jeton/],
  [{ ...ready, claudeInstalled: false }, /claude est absente/],
  [{ ...ready, declared: true }, new RegExp(SERVER_NAME)],
]) {
  const notice = setupNotice(state);
  assert.equal(notice.propose, false);
  assert.match(notice.reason, motif, 'un refus doit s’expliquer dans le journal');
}
// « Ne plus proposer » prime sur tout : après un refus, plus aucun état ne réinterrompt.
assert.equal(setupNotice({ dismissed: true, hubConfigured: false, signedIn: false, claudeInstalled: false, declared: false }).propose, false);

// Interroger Claude coûte un processus au démarrage : il ne doit être payé que si la
// réponse peut encore changer quelque chose.
assert.equal(shouldQueryClaude({ dismissed: false, hubConfigured: true, signedIn: true }), true);
assert.equal(shouldQueryClaude({ dismissed: true, hubConfigured: true, signedIn: true }), false);
assert.equal(shouldQueryClaude({ dismissed: false, hubConfigured: false, signedIn: true }), false);
assert.equal(shouldQueryClaude({ dismissed: false, hubConfigured: true, signedIn: false }), false);

// Le contrat avec l'extension : la commande déclarée, la commande enregistrée et le bundle
// recopié. Un renommage d'un seul côté laisse une entrée de palette qui ne fait rien.
const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const source = await readFile(new URL('./extension.ts', import.meta.url), 'utf8');
const declared = manifest.contributes.commands.map((command) => command.command);
assert.ok(declared.includes('codeinsights.configureMcpClient'), 'la commande doit être contribuée par le manifeste');
assert.match(source, /registerCommand\('codeinsights\.configureMcpClient'/, 'la commande contribuée doit être enregistrée');

const setup = await readFile(new URL('./mcp/claude-setup.ts', import.meta.url), 'utf8');
assert.match(setup, /import \{ BUNDLE, settingsEnvironment \} from '\.\/provider'/, 'le bundle et les réglages doivent venir du fournisseur, pas d’une seconde définition');
// La vérification précède l'écriture : une configuration désignant un serveur qui ne
// démarre pas se lit, côté agent, comme « CodeInsights n'a rien à dire ».
assert.ok(setup.indexOf('const refused = await handshake(entry)') < setup.indexOf("return scope === 'user'"), 'le handshake doit précéder l’écriture');
// La proposition du démarrage doit suivre la remise à niveau, sinon elle proposerait de
// configurer ce que `upgrade()` est en train d'écrire.
assert.match(source, /\.upgrade\(\)\s*\n\s*\.then\(\(\) => claude\.prompt\(\)\)/, 'prompt() doit suivre upgrade() à l’activation');
// Configurer depuis la palette lève le refus : « ne plus proposer » ne doit pas être définitif.
assert.ok(setup.indexOf('update(DISMISSED, undefined)') > 0, 'la commande explicite doit lever le refus');

console.log('Claude client checks passed');

});
