/**
 * Builds hub URLs from the SPA workspace state. The parameter names mirror
 * apps/web/src/navigation-state.ts; a rename there breaks these links silently,
 * so deep-link.test.ts asserts the exact contract.
 */

export type HubTarget = {
  serverUrl: string;
  tab?: 'projects' | 'issues' | 'rules' | 'quality-gates';
  scope?: 'overall' | 'new-code';
  project?: string;
  branch?: string;
  analysisId?: string;
  file?: string;
  severity?: string;
  rule?: string;
  selectedRule?: string;
  ruleSearch?: string;
  ruleStatus?: string;
  mine?: boolean;
};

/**
 * Hub BRANCH selection for an analysis: a pull request build is selected through its pull
 * request (`pr:<id>`, mirrored from apps/web/src/utils/branch-selection.ts), never its branch.
 */
export function hubBranchKey(analysis: { branch: string; pr_id?: string | null }): string {
  return analysis.pr_id ? `pr:${analysis.pr_id}` : analysis.branch;
}

export function hubUrl(target: HubTarget): string {
  const base = target.serverUrl.trim().replace(/\/+$/, '');
  if (base.length === 0) throw new Error('CodeInsights server URL is not configured');
  const params = new URLSearchParams();
  const set = (key: string, value: string | undefined, fallback = '') => {
    if (value !== undefined && value !== fallback) params.set(key, value);
  };
  set('tab', target.tab, 'projects');
  set('scope', target.scope, 'overall');
  set('project', target.project);
  set('branch', target.branch);
  set('analysis_id', target.analysisId);
  set('issue_file', target.file);
  if (target.severity !== undefined && target.severity !== 'all') params.set('severity', target.severity);
  set('issue_rule', target.rule);
  set('selected_rule', target.selectedRule);
  set('rule_search', target.ruleSearch);
  set('rule_status', target.ruleStatus, 'all');
  if (target.mine === true) params.set('mine', 'true');
  const query = params.toString();
  return query.length === 0 ? `${base}/` : `${base}/?${query}`;
}
