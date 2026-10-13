/**
 * How the MCP server is bundled, defined once. Two artefacts are produced from these
 * options — `apps/mcp/dist/server.js` pour le paquet npm, `apps/vscode/dist/mcp-server.js`
 * embarqué dans le VSIX — et les deux voies ne peuvent donc pas diverger.
 *
 * Le bundle est autonome : aucune dépendance n'est résolue au démarrage. Un poste hors
 * ligne, ou un `npx` qui ne répond pas, est un mode d'échec silencieux pour un serveur MCP.
 */
import { fileURLToPath } from 'node:url';

export const ENTRY = fileURLToPath(new URL('src/server.ts', import.meta.url));

export function serverBundle(outfile, production) {
  return {
    entryPoints: [ENTRY],
    outfile,
    bundle: true,
    platform: 'node',
    target: 'node22',
    format: 'esm',
    banner: { js: '#!/usr/bin/env node' },
    sourcemap: !production,
    minify: production,
    logLevel: 'info',
  };
}
