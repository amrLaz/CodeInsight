export function evidenceLink(value: string | null | undefined): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

type RepositoryProvider = 'github' | 'azure-devops' | 'other';

function repositoryUrl(provider: RepositoryProvider, value: string | null | undefined): URL | null {
  if (provider === 'other') return null;
  let url: URL;
  try {
    url = new URL(value ?? '');
  } catch {
    return null;
  }
  const azureHost = url.hostname === 'dev.azure.com' || url.hostname.endsWith('.visualstudio.com');
  if (provider === 'azure-devops' && url.protocol === 'https:' && azureHost && !url.password) url.username = '';
  else if (!evidenceLink(url.href)) return null;
  url.search = '';
  url.hash = '';
  url.pathname = url.pathname.replace(/\/$/, '').replace(/\.git$/i, '');
  return url;
}

export function repositoryCommitLink(provider: RepositoryProvider, repoUrl: string | null | undefined, commit: string | null | undefined): string | null {
  const repo = repositoryUrl(provider, repoUrl);
  const sha = commit?.trim() ?? '';
  if (!repo || !/^[a-f0-9]{6,64}$/i.test(sha)) return null;
  repo.pathname += `/commit/${sha}`;
  return repo.href;
}

export function repositoryBranchLink(provider: RepositoryProvider, repoUrl: string | null | undefined, branch: string | null | undefined): string | null {
  const repo = repositoryUrl(provider, repoUrl);
  const name = branch?.trim() ?? '';
  if (!repo || !name || name.length > 255 || Array.from(name).some((character) => character.charCodeAt(0) < 32)) return null;
  const ref = name.startsWith('refs/heads/') ? name.slice('refs/heads/'.length) : name.startsWith('refs/tags/') ? name.slice('refs/tags/'.length) : name;
  if (!ref) return null;
  if (provider === 'azure-devops') repo.searchParams.set('version', `${name.startsWith('refs/tags/') ? 'GT' : 'GB'}${ref}`);
  else repo.pathname += `/tree/${ref.split('/').map(encodeURIComponent).join('/')}`;
  return repo.href;
}

export function repositoryPullRequestLink(provider: RepositoryProvider, repoUrl: string | null | undefined, pullRequest: string | null | undefined): string | null {
  const repo = repositoryUrl(provider, repoUrl);
  const id = pullRequest?.trim() ?? '';
  if (!repo || !/^\d+$/.test(id)) return null;
  repo.pathname += provider === 'azure-devops' ? `/pullrequest/${id}` : `/pull/${id}`;
  return repo.href;
}

export function ruleDefinitionLink(tool: string, ruleId: string): string | null {
  const rule = ruleId.trim();
  const upper = rule.toUpperCase();
  if (/^CVE-\d{4}-\d{4,}$/.test(upper)) return `https://nvd.nist.gov/vuln/detail/${upper}`;
  const cwe = /^CWE-(\d+)$/.exec(upper);
  if (cwe) return `https://cwe.mitre.org/data/definitions/${cwe[1]}.html`;
  if (/^CA\d{4}$/.test(upper)) return `https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/${upper.toLowerCase()}`;
  if (tool.toLowerCase().includes('eslint') && /^[a-z0-9@/_-]+$/i.test(rule)) return `https://eslint.org/docs/latest/rules/${encodeURIComponent(rule)}`;
  if (/^SA\d{4}$/.test(upper)) return `https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/${upper}.md`;
  return null;
}
