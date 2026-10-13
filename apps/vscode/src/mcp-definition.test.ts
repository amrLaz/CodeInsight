import { test } from 'vitest';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('mcp-definition', async () => {

/**
 * Le contrat entre l'extension et le serveur MCP qu'elle embarque : les variables passées,
 * l'identifiant du fournisseur et le nom du bundle. Chacun de ces trois noms peut être
 * renommé d'un seul côté sans rien casser de visible — l'agent repartirait sur d'autres
 * réglages que l'IDE, ou l'hôte lancerait un fichier absent.
 */
const { serverEnvironment } = await import('./mcp/environment');
const { readConfig } = await import('../../mcp/src/config');

const settings = {
  targetBranch: 'origin/develop',
  semgrepConfig: ['.codeinsights/semgrep-mqr-v1.json', 'p/ci'],
  useDocker: true,
  images: { semgrep: 'semgrep/semgrep:1.145.0', gitleaks: 'zricethezav/gitleaks:v8.30.0' },
  timeoutSeconds: 90,
  maxFiles: 42,
};
const environment = serverEnvironment('https://hub.example', settings);
const config = readConfig(environment);

assert.equal(config.serverUrl, 'https://hub.example');
assert.equal(config.local.targetBranch, settings.targetBranch);
assert.deepEqual(config.local.semgrepConfigs, settings.semgrepConfig, 'la liste de règles doit traverser l’environnement intacte');
assert.equal(config.local.useDocker, true);
assert.deepEqual(config.local.images, settings.images);
assert.equal(config.local.timeoutMs, settings.timeoutSeconds * 1000);
assert.equal(config.local.maxFiles, settings.maxFiles);
// Le jeton ne passe jamais par les réglages : il est injecté à la résolution, depuis SecretStorage.
assert.equal('CODEINSIGHTS_TOKEN' in environment, false);
assert.equal(config.token, undefined);
// Docker désactivé doit l'être aussi côté serveur : une chaîne vide y serait lue comme « faux ».
assert.equal(readConfig(serverEnvironment('https://hub.example', { ...settings, useDocker: false })).local.useDocker, false);

const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const provider = await readFile(new URL('./mcp/provider.ts', import.meta.url), 'utf8');
const declared = (manifest.contributes.mcpServerDefinitionProviders ?? []).map((entry) => entry.id);
const registered = /export const PROVIDER_ID = '([^']+)'/.exec(provider)?.[1];
assert.deepEqual(declared, [registered], 'le fournisseur enregistré doit être celui déclaré par le manifeste');

const bundle = /const BUNDLE = '([^']+)'/.exec(provider)?.[1];
const build = await readFile(new URL('../esbuild.mjs', import.meta.url), 'utf8');
assert.ok(build.includes(`serverBundle('dist/${bundle}'`), `le build doit produire dist/${bundle}`);
// Le bundle doit rester dans le VSIX : .vscodeignore ne doit pas exclure dist/.
const ignored = await readFile(new URL('../.vscodeignore', import.meta.url), 'utf8');
assert.equal(
  ignored.split('\n').some((line) => line.trim().startsWith('dist')),
  false,
  'dist/ porte le bundle embarqué et ne doit pas être exclu du paquet',
);

console.log('MCP definition checks passed');

});
