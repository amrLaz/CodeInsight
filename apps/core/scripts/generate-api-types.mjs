#!/usr/bin/env node
/**
 * Derives src/api/schema.d.ts from the hub's OpenAPI document. The types are
 * never hand-written: the API owns the snake_case contract and changes it
 * without notifying the IDE.
 *
 * Usage: npm run api -- [--url https://hub.example/openapi.json | --file path]
 */
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, 'src/api/schema.d.ts');
const args = process.argv.slice(2);
const value = (flag) => {
  const index = args.indexOf(flag);
  return index >= 0 ? args[index + 1] : undefined;
};

const source =
  value('--file') ??
  value('--url') ??
  process.env.CODEINSIGHTS_OPENAPI ??
  'http://localhost:8000/openapi.json';

if (source.startsWith('http') === false && existsSync(source) === false) {
  console.error(`OpenAPI document not found: ${source}`);
  process.exit(1);
}

mkdirSync(dirname(output), { recursive: true });
execFileSync(
  process.execPath,
  [resolve(root, 'node_modules/openapi-typescript/bin/cli.js'), source, '--output', output],
  { stdio: 'inherit', cwd: root },
);
console.log(`Generated ${output} from ${source}`);
