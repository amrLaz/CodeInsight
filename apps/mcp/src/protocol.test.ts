import { test } from 'vitest';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { buildServer, readConfig, VERSION } from '../dist/server.js';

test('protocol', async () => {

/**
 * Parle réellement MCP au serveur : la surface d'outils est un contrat avec l'agent, et
 * un outil renommé ou une entrée devenue obligatoire casse la boucle sans erreur visible.
 * Le bundle est importé tel qu'il est distribué : c'est lui qui tournera chez l'utilisateur.
 */
// La configuration est construite comme au démarrage : un champ ajouté ne peut pas
// manquer ici sans manquer aussi en production.
const server = buildServer(readConfig({}));
const client = new Client({ name: 'protocol.check', version: '1.0.0' });
const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
await Promise.all([client.connect(clientTransport), server.connect(serverTransport)]);

const { tools } = await client.listTools();
const names = tools.map((tool) => tool.name).sort();
assert.deepEqual(names, [
  'analyze_pending_changes',
  'describe_rule',
  'explain_finding',
  'get_branch_report',
  'get_file_quality',
  'get_quality_gate',
  'list_findings',
]);

for (const tool of tools) {
  assert.ok((tool.description ?? '').length > 40, `${tool.name} doit décrire quand l'appeler`);
  // Première livraison en lecture seule : la garantie est l'absence d'outil d'écriture,
  // et l'annotation dit aux hôtes MCP qu'aucune confirmation n'est nécessaire.
  assert.equal(tool.annotations?.readOnlyHint, true, `${tool.name} doit être annoté readOnlyHint`);
  assert.equal(tool.annotations?.destructiveHint, false, `${tool.name} ne doit rien détruire`);
  const properties = tool.inputSchema.properties ?? {};
  assert.ok('cwd' in properties || tool.name === 'explain_finding', `${tool.name} doit prendre un cwd`);
  // Aucun outil ne demande de project_key : l'agent en inventerait une.
  assert.equal('project_key' in properties, false, `${tool.name} ne doit pas demander de clé de projet`);
  assert.deepEqual(
    (tool.inputSchema.required ?? []).filter((name) => name === 'cwd'),
    ['cwd'],
    `${tool.name} doit exiger cwd`,
  );
}

const findings = tools.find((tool) => tool.name === 'list_findings');
assert.ok(findings.inputSchema.properties.limit, 'la borne de sortie doit être réglable');
assert.deepEqual(findings.inputSchema.properties.severity.enum, ['blocker', 'high', 'medium', 'low', 'info', 'all']);

// Sans configuration, chaque outil dit ce qui manque et comment le fournir — pas « HTTP 401 ».
const answer = await client.callTool({ name: 'get_branch_report', arguments: { cwd: process.cwd() } });
const text = answer.content.map((part) => part.text).join('\n');
assert.match(text, /CODEINSIGHTS_URL/);
assert.equal(answer.isError, true, 'une configuration absente est une erreur, pas un rapport vide');

await client.close();
await server.close();

// L'outil d'analyse locale lit le working tree et n'est donc pas idempotent : l'hôte MCP
// ne doit pas réutiliser une réponse précédente après un correctif de l'agent.
const pending = tools.find((tool) => tool.name === 'analyze_pending_changes');
assert.equal(pending.annotations.idempotentHint, false);
assert.match(pending.description, /estimation/i);

console.log(`protocol checks passed (codeinsights-mcp ${VERSION}, ${names.length} outils en lecture seule)`);

});
