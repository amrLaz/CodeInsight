import type { RawComponent, RawComponentVulnerability } from '../api/codeinsights';

/**
 * Matches a manifest line onto the SBOM of the last analysis. An empty CVE list is
 * "aucune CVE reçue", never "safe": the hub only repeats what the producer sent.
 */

export type ManifestKind = 'npm' | 'nuget' | 'pub';

export type ManifestHit = { name: string; line: number; kind: ManifestKind };

export type ComponentMatch = {
  name: string;
  version?: string;
  purl?: string;
  vulnerabilities: { id: string; severity?: string; source?: string }[];
};

export function manifestKind(path: string): ManifestKind | undefined {
  const normalised = path.replace(/\\/g, '/').toLowerCase();
  if (normalised.endsWith('package.json')) return 'npm';
  if (normalised.endsWith('.csproj') || normalised.endsWith('directory.packages.props') || normalised.endsWith('packages.props'))
    return 'nuget';
  if (normalised.endsWith('pubspec.yaml') || normalised.endsWith('pubspec.yml')) return 'pub';
  return undefined;
}

export function parseManifest(kind: ManifestKind, text: string): ManifestHit[] {
  if (kind === 'npm') return parsePackageJson(text);
  if (kind === 'nuget') return parseCsproj(text);
  return parsePubspec(text);
}

export function parseVulnerabilities(raw: RawComponent['vulnerabilities']): ComponentMatch['vulnerabilities'] {
  if (!Array.isArray(raw)) return [];
  const result: ComponentMatch['vulnerabilities'] = [];
  for (const entry of raw) {
    const row = entry as RawComponentVulnerability;
    const id = typeof row.id === 'string' ? row.id.trim() : '';
    if (id.length === 0) continue;
    const item: ComponentMatch['vulnerabilities'][number] = { id };
    if (typeof row.severity === 'string' && row.severity.length > 0) item.severity = row.severity;
    if (typeof row.source === 'string' && row.source.length > 0) item.source = row.source;
    result.push(item);
  }
  return result;
}

export function matchComponent(name: string, kind: ManifestKind, components: RawComponent[]): ComponentMatch | undefined {
  const needle = name.toLowerCase();
  const found = components.find((component) => {
    if (component.name.toLowerCase() === needle) return true;
    const purl = component.purl?.toLowerCase() ?? '';
    if (kind === 'npm' && (purl.includes(`/npm/${needle}@`) || purl.includes(`/npm/${needle}?`) || purl.endsWith(`/npm/${needle}`)))
      return true;
    if (kind === 'nuget' && (purl.includes(`/nuget/${needle}@`) || purl.endsWith(`/nuget/${needle}`))) return true;
    if (kind === 'pub' && (purl.includes(`/pub/${needle}@`) || purl.endsWith(`/pub/${needle}`))) return true;
    return false;
  });
  if (found === undefined) return undefined;
  const match: ComponentMatch = { name: found.name, vulnerabilities: parseVulnerabilities(found.vulnerabilities) };
  if (typeof found.version === 'string' && found.version.length > 0) match.version = found.version;
  if (typeof found.purl === 'string' && found.purl.length > 0) match.purl = found.purl;
  return match;
}

export function dependencyLabel(match: ComponentMatch | undefined, loaded: boolean): string {
  if (!loaded) return 'SBOM de l’analyse indisponible';
  if (match === undefined) return 'composant absent du SBOM de l’analyse';
  const version = match.version === undefined ? '' : `@${match.version}`;
  if (match.vulnerabilities.length === 0) return `${match.name}${version} : aucune CVE reçue`;
  const worst = match.vulnerabilities.map((row) => row.severity).find((value) => value !== undefined);
  const count = match.vulnerabilities.length === 1 ? '1 CVE' : `${match.vulnerabilities.length} CVE`;
  return worst === undefined ? `${match.name}${version} : ${count}` : `${match.name}${version} : ${count} · ${worst}`;
}

function parsePackageJson(text: string): ManifestHit[] {
  let payload: { dependencies?: unknown; devDependencies?: unknown; optionalDependencies?: unknown };
  try {
    payload = JSON.parse(text) as typeof payload;
  } catch {
    return [];
  }
  const names = new Set<string>();
  for (const bucket of [payload.dependencies, payload.devDependencies, payload.optionalDependencies]) {
    if (bucket === null || typeof bucket !== 'object') continue;
    for (const name of Object.keys(bucket as Record<string, unknown>)) if (name.length > 0) names.add(name);
  }
  const hits: ManifestHit[] = [];
  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index += 1) {
    const match = /^\s*"([^"]+)"\s*:/.exec(lines[index] ?? '');
    if (match === null || !names.has(match[1]!)) continue;
    hits.push({ name: match[1]!, line: index + 1, kind: 'npm' });
  }
  return hits;
}

function parseCsproj(text: string): ManifestHit[] {
  const hits: ManifestHit[] = [];
  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index += 1) {
    const match = /<(?:PackageReference|PackageVersion)\b[^>]*\bInclude="([^"]+)"/i.exec(lines[index] ?? '');
    if (match === null) continue;
    hits.push({ name: match[1]!, line: index + 1, kind: 'nuget' });
  }
  return hits;
}

function parsePubspec(text: string): ManifestHit[] {
  const hits: ManifestHit[] = [];
  const lines = text.split(/\r?\n/);
  let inDependencies = false;
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index] ?? '';
    if (/^(dependencies|dev_dependencies|dependency_overrides):/.test(line)) {
      inDependencies = true;
      continue;
    }
    if (inDependencies && /^[^\s]/.test(line)) {
      inDependencies = false;
    }
    if (!inDependencies) continue;
    const match = /^\s{2}([A-Za-z0-9_.-]+):/.exec(line);
    if (match === null) continue;
    hits.push({ name: match[1]!, line: index + 1, kind: 'pub' });
  }
  return hits;
}
