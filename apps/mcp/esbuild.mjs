import { build } from 'esbuild';
import { serverBundle } from './esbuild.options.mjs';

await build(serverBundle('dist/server.js', process.argv.includes('--production')));
