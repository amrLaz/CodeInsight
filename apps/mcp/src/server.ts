import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { readConfig, type Config } from './config';

export { readConfig } from './config';
import { createTools } from './tools';

/**
 * stdio server. The transport is the standard input/output of the process, so nothing but
 * the protocol may ever be written to stdout: a stray `console.log` corrupts the stream
 * and the host loses the server. Every diagnostic goes to stderr.
 */

export const VERSION = '0.1.0';

const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: true } as const;

const CWD = z
  .string()
  .describe("Chemin dans le dépôt analysé. Le projet CodeInsights est déduit de l'URL du dépôt Git : ne jamais inventer de clé de projet.");

const BRANCH = z.string().optional().describe('Branche à interroger. Par défaut, la branche courante du dépôt.');

export function buildServer(config: Config): McpServer {
  const tools = createTools(config);
  const server = new McpServer(
    { name: 'codeinsights', version: VERSION },
    { capabilities: { tools: {} }, instructions: INSTRUCTIONS },
  );

  server.registerTool(
    'get_branch_report',
    {
      title: 'Rapport CodeInsights de la branche',
      description:
        "Verdict du quality gate, complétude, état technique et mesures de la dernière analyse publiée pour la branche. Une branche sans build de branche renvoie le dernier build de pull request issu d'elle, nommé « PR #N · source → cible » : c'est le verdict de la PR, pas celui de la branche. Une mesure absente est annoncée comme telle, jamais comme zéro. À appeler avant de conclure sur l'état qualité d'une branche.",
      inputSchema: { cwd: CWD, branch: BRANCH },
      annotations: READ_ONLY,
    },
    async ({ cwd, branch }) => text(await tools.getBranchReport({ cwd, branch })),
  );

  server.registerTool(
    'list_findings',
    {
      title: 'Issues de la dernière analyse',
      description:
        "Issues de la dernière analyse de la branche, les plus critiques d'abord et bornées. Filtrer par chemins et par criticité plutôt que d'augmenter la limite. Ces issues sont celles de l'analyse publiée par la CI, pas celles des changements en cours.",
      inputSchema: {
        cwd: CWD,
        branch: BRANCH,
        paths: z.array(z.string()).max(5).optional().describe('Chemins ou fragments de chemin relatifs au dépôt (5 au plus).'),
        severity: z.enum(['blocker', 'high', 'medium', 'low', 'info', 'all']).optional().describe('Criticité minimale exacte ; « all » par défaut.'),
        limit: z.number().int().positive().optional().describe('Nombre d’issues rendues (30 par défaut).'),
      },
      annotations: READ_ONLY,
    },
    async (input) => text(await tools.listFindings(input)),
  );

  server.registerTool(
    'explain_finding',
    {
      title: 'Détail d’une issue',
      description:
        "Message, règle, localisations secondaires et contexte source d'une issue, à partir de l'identifiant rendu par list_findings. Les identifiants appartiennent à une analyse : les relire après une nouvelle analyse.",
      inputSchema: {
        cwd: CWD,
        finding_id: z.number().int().positive().describe('Identifiant entre crochets dans list_findings.'),
        include_source: z.boolean().optional().describe('Joindre le contexte source conservé par le hub (vrai par défaut).'),
      },
      annotations: READ_ONLY,
    },
    async (input) => text(await tools.explainFinding(input)),
  );

  server.registerTool(
    'describe_rule',
    {
      title: 'Définition d’une règle',
      description:
        "Identité, criticité, statut dans le profil du projet et lien vers la définition publiée par le producteur. CodeInsights n'héberge pas de texte de règle et n'en invente pas.",
      inputSchema: { cwd: CWD, rule_id: z.string().describe('Identifiant de règle, par exemple « dotnet.permissive-cors » ou « semgrep/dotnet.permissive-cors ».') },
      annotations: READ_ONLY,
    },
    async (input) => text(await tools.describeRule(input)),
  );

  server.registerTool(
    'get_quality_gate',
    {
      title: 'Quality gate de la branche',
      description:
        "Verdict et conditions non tenues de la dernière analyse. Une analyse incomplète ou techniquement en échec rend « Unknown » : ce n'est pas un échec du gate, et ce n'est pas un succès.",
      inputSchema: { cwd: CWD, branch: BRANCH },
      annotations: READ_ONLY,
    },
    async ({ cwd, branch }) => text(await tools.getQualityGate({ cwd, branch })),
  );

  server.registerTool(
    'get_file_quality',
    {
      title: 'Couverture et duplication d’un fichier',
      description:
        "Couverture et blocs dupliqués d'un fichier tels que la dernière analyse les a mesurés. Un fichier absent du rapport de couverture est signalé comme non instrumenté, jamais comme couvert à 0 %.",
      inputSchema: { cwd: CWD, path: z.string().describe('Chemin du fichier relatif à la racine du dépôt.') },
      annotations: READ_ONLY,
    },
    async (input) => text(await tools.getFileQuality(input)),
  );

  server.registerTool(
    'analyze_pending_changes',
    {
      title: 'Analyser les changements en cours',
      description:
        "Exécute les producteurs locaux (semgrep, gitleaks) sur les lignes ajoutées par le changement en cours, applique les exceptions et règles désactivées du projet, et estime l'effet sur le quality gate. C'est une estimation du poste de travail, jamais le verdict de la CI, et un producteur absent est annoncé : « aucune issue » ne vaut alors pas « aucun problème ». À appeler avant de proposer un correctif, puis à rappeler après pour vérifier la disparition du finding.",
      inputSchema: {
        cwd: CWD,
        target_branch: z.string().optional().describe('Branche de comparaison, par exemple origin/master. Par défaut CODEINSIGHTS_TARGET_BRANCH.'),
      },
      // Read-only on the hub, and the producers only read the working tree; they write
      // nothing and reach no network. Not idempotent: the tree changes between two calls.
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false, openWorldHint: true },
    },
    async (input) => text(await tools.analyzePendingChanges(input)),
  );

  return server;
}

const INSTRUCTIONS = `CodeInsights consolide les analyses de code produites par la CI : quality gate, issues, couverture, duplication et règles.
Tous les outils sont en lecture seule et prennent un « cwd » situé dans le dépôt : le projet est résolu depuis « .codeinsights/vscode.json », puis à défaut depuis l'URL du dépôt Git.
Ce que la CI n'a pas publié n'existe pas ici : une branche sans analyse le dit, et une mesure absente n'est jamais rendue comme zéro.`;

function text(body: string): { content: { type: 'text'; text: string }[] } {
  return { content: [{ type: 'text', text: body }] };
}

export async function main(): Promise<void> {
  const config = readConfig(process.env);
  const server = buildServer(config);
  await server.connect(new StdioServerTransport());
  // stderr only: stdout carries the protocol.
  process.stderr.write(`codeinsights-mcp ${VERSION} — hub ${config.serverUrl.length === 0 ? 'non configuré' : config.serverUrl}\n`);
}

/** True when this bundle was executed, false when a check imported it. */
function launched(): boolean {
  const entry = process.argv[1];
  if (entry === undefined) return false;
  try {
    // `bin` is a symlink in node_modules/.bin: compare the real files, not the paths.
    return realpathSync(entry) === fileURLToPath(import.meta.url);
  } catch {
    return false;
  }
}

if (launched()) {
  main().catch((error: unknown) => {
    process.stderr.write(`codeinsights-mcp : démarrage impossible — ${error instanceof Error ? error.message : String(error)}\n`);
    process.exit(1);
  });
}
