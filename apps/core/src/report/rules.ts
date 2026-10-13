import type { RawRule } from '../api/codeinsights';

/**
 * Catalogue of rules observed on the project's latest analyses. Status is the one the
 * hub published (enabled / disabled / unmanaged); the IDE never invents a description.
 */

export type RuleFilters = { search: string; status: string; category: string; severity: string };

export const defaultRuleFilters: RuleFilters = { search: '', status: 'all', category: 'all', severity: 'all' };

export type Rule = {
  key: string;
  tool: string;
  ruleId: string;
  title: string;
  category: string;
  severity: string;
  language: string;
  occurrences: number;
  status: string;
};

export type RuleGroup = { tool: string; rules: Rule[] };

export function toRule(raw: RawRule): Rule {
  const tool = raw.tool?.trim() || 'inconnu';
  const ruleId = raw.rule_id?.trim() || raw.key?.trim() || 'inconnu';
  return {
    key: raw.key?.trim() || `${tool}/${ruleId}`,
    tool,
    ruleId,
    title: raw.title?.trim() || `${tool} · ${ruleId}`,
    category: raw.category?.trim() || 'unknown',
    severity: raw.severity?.trim() || 'info',
    language: raw.language?.trim() || 'Non disponible',
    occurrences: typeof raw.occurrences === 'number' && Number.isFinite(raw.occurrences) ? raw.occurrences : 0,
    status: raw.status?.trim() || 'unmanaged',
  };
}

export function filterRules(rules: Rule[], filters: RuleFilters): Rule[] {
  const search = filters.search.trim().toLowerCase();
  return rules.filter((rule) => {
    if (filters.status !== 'all' && rule.status !== filters.status) return false;
    if (filters.category !== 'all' && rule.category !== filters.category) return false;
    if (filters.severity !== 'all' && rule.severity.toLowerCase() !== filters.severity.toLowerCase()) return false;
    if (search.length === 0) return true;
    return `${rule.tool} ${rule.ruleId} ${rule.title} ${rule.language}`.toLowerCase().includes(search);
  });
}

export function groupRules(rules: Rule[]): RuleGroup[] {
  const groups = new Map<string, Rule[]>();
  for (const rule of rules) {
    const bucket = groups.get(rule.tool);
    if (bucket === undefined) groups.set(rule.tool, [rule]);
    else bucket.push(rule);
  }
  for (const bucket of groups.values())
    bucket.sort((a, b) => b.occurrences - a.occurrences || a.ruleId.localeCompare(b.ruleId));
  return [...groups.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([tool, grouped]) => ({ tool, rules: grouped }));
}

export function rulesLabel(filters: RuleFilters, count: number): string {
  const parts: string[] = [];
  if (filters.search.trim().length > 0) parts.push(`« ${filters.search.trim()} »`);
  if (filters.status !== 'all') parts.push(filters.status);
  if (filters.category !== 'all') parts.push(filters.category);
  if (filters.severity !== 'all') parts.push(filters.severity);
  const scope = parts.length === 0 ? 'toutes les règles' : parts.join(' · ');
  const total = count === 0 ? 'aucune règle' : count === 1 ? '1 règle' : `${count} règles`;
  return `${scope} — ${total}`;
}

export function statusLabel(status: string): string {
  switch (status) {
    case 'enabled':
      return 'activée';
    case 'disabled':
      return 'désactivée';
    case 'unmanaged':
      return 'non gérée';
    default:
      return status;
  }
}
