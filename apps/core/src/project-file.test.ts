import { test } from 'vitest';
import assert from 'node:assert/strict';

test('project-file', async () => {

/**
 * Ce que le dépôt déclare sur son analyse. Une valeur mal typée qui passerait telle quelle
 * ferait échouer semgrep ou chercher une branche inexistante, et un agent lit un producteur
 * en échec comme « aucune issue » — le mode d'échec que toute cette chaîne évite.
 */
const { projectSettings } = await import('./context/project-file');

assert.deepEqual(projectSettings(undefined), {});
assert.deepEqual(projectSettings({}), {});
assert.deepEqual(projectSettings({ projectKey: 'demo' }), {});

assert.deepEqual(projectSettings({ targetBranch: ' origin/develop ' }), { targetBranch: 'origin/develop' });
// Une chaîne vide n'est pas une branche : le poste garde la main plutôt que de diffuser contre rien.
assert.deepEqual(projectSettings({ targetBranch: '   ' }), {});
assert.deepEqual(projectSettings({ targetBranch: 42 }), {});

assert.deepEqual(
  projectSettings({ semgrepConfig: ['.codeinsights/semgrep-mqr-v1.json', ' p/ci ', '', 7] }),
  { semgrepConfigs: ['.codeinsights/semgrep-mqr-v1.json', 'p/ci'] },
);
// Un tableau vide est une déclaration du dépôt, pas une absence de réglage : semgrep sera dit absent.
assert.deepEqual(projectSettings({ semgrepConfig: [] }), { semgrepConfigs: [] });
// Un type qui n'est pas un tableau ne l'est pas : le poste décide, et ses règles sont connues.
assert.deepEqual(projectSettings({ semgrepConfig: 'p/ci' }), {});

console.log('project file checks passed');

});
