import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('project-config', async () => {

/**
 * La découverte de `.codeinsights/vscode.json` : c'est par ce fichier que l'agent et l'IDE
 * nomment le même projet pour un même dossier. Une remontée qui s'arrête trop tôt, ou qui
 * dépasse la racine du clone, ferait répondre l'outil sur un projet voisin sans le dire.
 */
const { projectConfig } = await import('./project-config');

const root = await mkdtemp(join(tmpdir(), 'codeinsights-project-'));
try {
  const write = async (directory, content) => {
    await mkdir(join(directory, '.codeinsights'), { recursive: true });
    await writeFile(join(directory, '.codeinsights', 'vscode.json'), content, 'utf8');
  };

  // Sans configuration, le dossier du clone répond, et rien n'est inventé à sa place.
  const bare = await projectConfig(join(root, 'apps', 'web'), root);
  assert.equal(bare.path, root);
  assert.equal(bare.file, undefined);

  // Posée à la racine, elle vaut pour un sous-dossier qui n'en a pas.
  await write(root, JSON.stringify({ projectKey: 'demo-root' }));
  const inherited = await projectConfig(join(root, 'apps', 'web'), root);
  assert.equal(inherited.path, root);
  assert.deepEqual(inherited.file, { projectKey: 'demo-root' });

  // Posée dans le dossier livrable, c'est elle qui répond : un monorepo porte plusieurs projets.
  const web = join(root, 'apps', 'web');
  const declared = { projectKey: 'demo-web', targetBranch: 'origin/develop', semgrepConfig: ['.codeinsights/web.json'] };
  await write(web, JSON.stringify(declared));
  const nearest = await projectConfig(web, root);
  assert.equal(nearest.path, web);
  assert.equal(nearest.folderName, 'web');
  // Les réglages d'analyse remontent tels quels : c'est `projectSettings` du cœur qui les valide.
  assert.deepEqual(nearest.file, declared);

  // La remontée s'arrête à la racine du clone : au-delà, le fichier appartient à un autre dépôt.
  const outside = join(root, 'outside');
  await write(root, JSON.stringify({ projectKey: 'demo-root' }));
  const bounded = await projectConfig(join(outside, 'nested'), outside);
  assert.equal(bounded.path, outside);
  assert.equal(bounded.file, undefined);

  // Un fichier illisible est dit, pas ignoré : le repli silencieux nommerait un autre projet.
  await write(web, '{ "projectKey": ');
  await assert.rejects(() => projectConfig(web, root), /illisible/);
} finally {
  await rm(root, { recursive: true, force: true });
}

console.log('project config checks passed');

});
