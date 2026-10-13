import { build, context } from 'esbuild';
import { serverBundle } from '../mcp/esbuild.options.mjs';

// The extension host loads a single CommonJS bundle; `vscode` is provided by the host.
const production = process.argv.includes('--production');
const options = {
  entryPoints: ['src/extension.ts'],
  outfile: 'dist/extension.js',
  bundle: true,
  platform: 'node',
  target: 'node22',
  format: 'cjs',
  external: ['vscode'],
  sourcemap: !production,
  minify: production,
  logLevel: 'info',
};

/**
 * Le serveur MCP est embarqué dans le VSIX, construit depuis `apps/mcp` avec ses options :
 * l'utilisateur n'a ni configuration à écrire ni paquet à installer, et le fichier lancé
 * est celui livré — jamais un `npx` résolu au démarrage sur un poste peut-être hors ligne.
 * `npm ci` dans `apps/mcp` est donc un prérequis du build de l'extension.
 */
const mcp = serverBundle('dist/mcp-server.mjs', production);

if (process.argv.includes('--watch')) {
  const ctx = await context(options);
  await ctx.watch();
  const mcpContext = await context(mcp);
  await mcpContext.watch();
} else {
  await build(options);
  await build(mcp);
}
