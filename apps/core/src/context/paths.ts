/**
 * Path arithmetic between a finding and the working tree. Report paths are supposed to
 * be repository-relative, but they come from the network: a path that escapes the
 * repository root is refused here too rather than trusted from the server.
 */
export function repositoryFile(rootPath: string, relativePath: string): string | undefined {
  const root = rootPath.replace(/\\/g, '/').replace(/\/+$/, '');
  if (root.length === 0) return undefined;
  const parts: string[] = [];
  for (const segment of relativePath.replace(/\\/g, '/').split('/')) {
    if (segment.length === 0 || segment === '.') continue;
    if (segment === '..') return undefined;
    parts.push(segment);
  }
  if (parts.length === 0) return undefined;
  // An absolute report path was already rejected by the hub; reject it here as well.
  if (relativePath.startsWith('/') || /^[a-zA-Z]:/.test(relativePath)) return undefined;
  return `${root}/${parts.join('/')}`;
}

/** Repository-relative path of a local file, or undefined when it is outside the clone. */
export function relativeToRoot(rootPath: string, fsPath: string): string | undefined {
  const root = rootPath.replace(/\\/g, '/').replace(/\/+$/, '');
  const file = fsPath.replace(/\\/g, '/');
  if (root.length === 0 || file === root) return undefined;
  if (!file.startsWith(`${root}/`)) return undefined;
  return file.slice(root.length + 1);
}
