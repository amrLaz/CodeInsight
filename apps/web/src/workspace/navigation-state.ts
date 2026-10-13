import type { AppTab, IssueSeverityFilter, IssueTypeFilter, ScopeMode } from '../types/api';
import type { RuleFilters } from '../features/rules/Rules';

export type IssueFilters = { severity: IssueSeverityFilter; category: IssueTypeFilter; rule: string; file: string; mine: boolean; state: string; team: string; sla: string };
export type ProjectMetric = '' | 'coverage' | 'duplication';
export type WorkspaceUrlState = {
  tab: AppTab;
  scope: ScopeMode;
  project: string;
  branch: string;
  analysis: string;
  projectMetric: ProjectMetric;
  gate: string;
  selectedRule: string;
  issues: IssueFilters;
  rules: RuleFilters;
};

export const defaultIssueFilters: IssueFilters = { severity: 'all', category: 'all', rule: '', file: '', mine: false, state: '', team: '', sla: '' };
export const defaultRuleFilters: RuleFilters = { search: '', category: 'all', severity: 'all', status: 'all', language: 'all' };
const oneOf = <T extends string>(value: string | null, values: readonly T[], fallback: T): T => (value && values.includes(value as T) ? (value as T) : fallback);

export function parseWorkspaceUrl(search: string): WorkspaceUrlState {
  const params = new URLSearchParams(search);
  return {
    tab: oneOf(params.get('tab'), ['projects', 'issues', 'rules', 'quality-gates', 'administration'], 'projects'),
    scope: oneOf(params.get('scope'), ['overall', 'new-code'], 'overall'),
    project: params.get('project') ?? '',
    branch: params.get('branch') ?? '',
    analysis: params.get('analysis_id') ?? '',
    projectMetric: oneOf(params.get('project_metric'), ['coverage', 'duplication'], ''),
    gate: params.get('gate') ?? '',
    selectedRule: params.get('selected_rule') ?? '',
    issues: {
      severity: oneOf(params.get('severity'), ['all', 'blocker', 'high', 'medium', 'low', 'info'], 'all'),
      category: oneOf(params.get('issue_category'), ['all', 'bug', 'code_smell', 'vulnerability', 'security_hotspot', 'secret', 'dependency', 'code', 'ioc', 'unknown'], 'all'),
      rule: params.get('issue_rule') ?? '',
      file: params.get('issue_file') ?? '',
      mine: params.get('mine') === 'true',
      state: params.get('issue_state') ?? '',
      team: params.get('team') ?? '',
      sla: params.get('issue_sla') ?? '',
    },
    rules: {
      search: params.get('rule_search') ?? '',
      category: params.get('rule_category') ?? 'all',
      severity: params.get('rule_severity') ?? 'all',
      status: oneOf(params.get('rule_status'), ['all', 'enabled', 'disabled', 'unmanaged'], 'all'),
      language: params.get('rule_language') ?? 'all',
    },
  };
}

export function workspaceSearch(state: WorkspaceUrlState): string {
  const params = new URLSearchParams();
  const set = (key: string, value: string, fallback = '') => {
    if (value !== fallback) params.set(key, value);
  };
  set('tab', state.tab, 'projects');
  set('scope', state.scope, 'overall');
  set('project', state.project);
  set('branch', state.branch);
  set('analysis_id', state.analysis);
  set('project_metric', state.projectMetric);
  set('gate', state.gate);
  set('selected_rule', state.selectedRule);
  set('severity', state.issues.severity, 'all');
  set('issue_category', state.issues.category, 'all');
  set('issue_rule', state.issues.rule);
  set('issue_file', state.issues.file);
  if (state.issues.mine) params.set('mine', 'true');
  set('issue_state', state.issues.state);
  set('team', state.issues.team);
  set('issue_sla', state.issues.sla);
  set('rule_search', state.rules.search);
  set('rule_category', state.rules.category, 'all');
  set('rule_severity', state.rules.severity, 'all');
  set('rule_status', state.rules.status, 'all');
  set('rule_language', state.rules.language, 'all');
  return params.size ? `?${params}` : '';
}
