import { test } from 'vitest';
import assert from 'node:assert/strict';

test('config', async () => {

const { readConfig, configProblem, apiProblem, DEFAULT_LIMIT, MAX_LIMIT } = await import('./config');

// Rien n'est deviné : sans URL ni jeton, le serveur le dit au lieu d'interroger un hub au hasard.
const empty = readConfig({});
assert.equal(empty.serverUrl, '');
assert.equal(empty.token, undefined);
assert.equal(empty.defaultLimit, DEFAULT_LIMIT);
assert.equal(empty.maxLimit, MAX_LIMIT);
// Sans règles configurées, semgrep sera rapporté « absent » plutôt que silencieusement vide.
assert.deepEqual(empty.local.semgrepConfigs, []);
assert.equal(empty.local.targetBranch, 'origin/master');
assert.equal(empty.local.useDocker, false);
assert.equal(empty.local.timeoutMs, 120_000);
assert.equal(empty.local.maxFiles, 100);
// Les images sont figées par version, comme celles de l'extension et de la CI.
assert.match(empty.local.images.semgrep, /^semgrep\/semgrep:\d/);
assert.match(empty.local.images.gitleaks, /^zricethezav\/gitleaks:v\d/);

const local = readConfig({ CODEINSIGHTS_SEMGREP_CONFIG: '.semgrep.yml, p/ci  p/secrets', CODEINSIGHTS_USE_DOCKER: 'true', CODEINSIGHTS_TIMEOUT_SECONDS: '30', CODEINSIGHTS_TARGET_BRANCH: ' origin/develop ' }).local;
assert.deepEqual(local.semgrepConfigs, ['.semgrep.yml', 'p/ci', 'p/secrets'], 'une liste doit traverser une variable d’environnement');
assert.equal(local.useDocker, true);
assert.equal(local.timeoutMs, 30_000);
assert.equal(local.targetBranch, 'origin/develop');
assert.equal(readConfig({ CODEINSIGHTS_TOKEN: '   ' }).token, undefined);
assert.equal(readConfig({ CODEINSIGHTS_URL: ' https://hub/ ' }).serverUrl, 'https://hub/');
assert.equal(readConfig({ CODEINSIGHTS_LIMIT: '5' }).defaultLimit, 5);
assert.equal(readConfig({ CODEINSIGHTS_LIMIT: '5000' }).defaultLimit, MAX_LIMIT, 'la borne haute protège la fenêtre de contexte');
assert.equal(readConfig({ CODEINSIGHTS_LIMIT: 'beaucoup' }).defaultLimit, DEFAULT_LIMIT);
assert.equal(readConfig({ CODEINSIGHTS_LIMIT: '-3' }).defaultLimit, DEFAULT_LIMIT);

const missingUrl = configProblem(readConfig({}));
assert.match(missingUrl, /CODEINSIGHTS_URL/);
const missingToken = configProblem(readConfig({ CODEINSIGHTS_URL: 'https://hub' }));
assert.match(missingToken, /CODEINSIGHTS_TOKEN/);
assert.equal(configProblem(readConfig({ CODEINSIGHTS_URL: 'https://hub', CODEINSIGHTS_TOKEN: 't' })), undefined);

// Une erreur doit dire quoi faire : « HTTP 401 » n'apprend rien à un agent.
assert.match(apiProblem(401, 'Token expired'), /jeton/i);
assert.match(apiProblem(401, 'Token expired'), /créer un nouveau|en créer un nouveau/i);
assert.match(apiProblem(403, 'Forbidden'), /accès/i);
assert.match(apiProblem(410, 'purged'), /rétention/i);
assert.equal(apiProblem(0, 'URL manquante'), 'URL manquante');

console.log('config checks passed');

});
