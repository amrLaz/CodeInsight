import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const pkg = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
const csproj = await readFile(join(root, 'vsix', 'CodeInsights.VisualStudio.csproj'), 'utf8');
const match = /<Version>([^<]+)<\/Version>/.exec(csproj);
assert.ok(match, 'le csproj doit déclarer Version');
assert.equal(match[1], pkg.version, 'package.json et le csproj doivent porter la même version livrée');
console.log(`version checks passed (${pkg.version})`);
