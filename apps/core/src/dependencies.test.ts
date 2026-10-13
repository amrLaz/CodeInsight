import { test } from 'vitest';
import assert from 'node:assert/strict';

test('dependencies', async () => {

const { dependencyLabel, manifestKind, matchComponent, parseManifest, parseVulnerabilities } = await import('./report/dependencies');

assert.equal(manifestKind('apps/web/package.json'), 'npm');
assert.equal(manifestKind('apps/api/CodeInsights.Api.csproj'), 'nuget');
assert.equal(manifestKind('pubspec.yaml'), 'pub');
assert.equal(manifestKind('src/app.ts'), undefined);

const npm = parseManifest(
  'npm',
  JSON.stringify({ dependencies: { leftpad: '1.0.0' }, devDependencies: { vitest: '2.0.0' } }, null, 2),
);
assert.deepEqual(
  npm.map((hit) => hit.name).sort(),
  ['leftpad', 'vitest'],
);

const csproj = parseManifest('nuget', '<Project>\n  <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />\n</Project>\n');
assert.equal(csproj[0].name, 'Newtonsoft.Json');
assert.equal(csproj[0].line, 2);

const pub = parseManifest('pub', 'name: app\ndependencies:\n  http: 1.0.0\ndev_dependencies:\n  test: 1.0.0\nflutter:\n  assets: []\n');
assert.deepEqual(
  pub.map((hit) => hit.name),
  ['http', 'test'],
);

const components = [
  {
    id: 1,
    name: 'leftpad',
    version: '1.0.0',
    purl: 'pkg:npm/leftpad@1.0.0',
    vulnerabilities: [{ id: 'CVE-2025-1', severity: 'high', source: 'osv' }],
  },
];
const match = matchComponent('leftpad', 'npm', components);
assert.equal(match.version, '1.0.0');
assert.deepEqual(parseVulnerabilities(match.vulnerabilities), match.vulnerabilities);
assert.equal(dependencyLabel(match, true), 'leftpad@1.0.0 : 1 CVE · high');
assert.equal(dependencyLabel(matchComponent('vitest', 'npm', components), true), 'composant absent du SBOM de l’analyse');
assert.equal(dependencyLabel({ name: 'vitest', vulnerabilities: [] }, true), 'vitest : aucune CVE reçue');
assert.equal(dependencyLabel(undefined, false), 'SBOM de l’analyse indisponible');
assert.equal(dependencyLabel({ name: 'x', vulnerabilities: [] }, true).includes('safe'), false);

console.log('dependencies checks passed');

});
