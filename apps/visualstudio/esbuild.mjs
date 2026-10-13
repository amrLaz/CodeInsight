import { build, context } from 'esbuild';
import { fileURLToPath } from 'node:url';

const production = process.argv.includes('--production');
const entry = fileURLToPath(new URL('src/server.ts', import.meta.url));

const options = {
  entryPoints: [entry],
  outfile: 'dist/host.mjs',
  bundle: true,
  platform: 'node',
  target: 'node22',
  format: 'esm',
  banner: { js: '#!/usr/bin/env node' },
  sourcemap: !production,
  minify: production,
  logLevel: 'info',
};

if (process.argv.includes('--watch')) {
  const ctx = await context(options);
  await ctx.watch();
} else {
  await build(options);
}
