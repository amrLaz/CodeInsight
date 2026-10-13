/**
 * JSON-RPC surface between the Visual Studio adapter and this host. The host is the
 * only process that calls apps/core: the C# side never reimplements a verdict,
 * a drift mapping or a local estimate.
 */

import type { Details } from '../../core/src/report/details';
import type { CategoryFilter, Issue, IssueFilters, SeverityFilter, StateFilter } from '../../core/src/report/issues';
import type { ReportNode, ViewState } from '../../core/src/report/report-nodes';
import type { Rule, RuleFilters } from '../../core/src/report/rules';

export const VERSION = '0.1.0';

export const METHODS = [
  'initialize',
  'setSettings',
  'setToken',
  'setCwd',
  'refresh',
  'getSnapshot',
  'filterIssues',
  'clearIssueFilters',
  'filterRules',
  'clearRuleFilters',
  'selectIssue',
  'analyzeChanges',
  'triage',
  'prompt',
  'fileContext',
  'hubUrl',
  'installHook',
  'shutdown',
] as const;

export type Method = (typeof METHODS)[number];

export type HostSettings = {
  serverUrl: string;
  projectKey: string;
  refreshIntervalSeconds: number;
  diagnosticsEnabled: boolean;
  liveAnalysisEnabled: boolean;
  liveAnalysisTargetBranch: string;
  liveAnalysisSemgrepConfig: string[];
  liveAnalysisUseDocker: boolean;
  liveAnalysisImages: { semgrep: string; gitleaks: string };
  liveAnalysisTimeoutSeconds: number;
  liveAnalysisMaxFiles: number;
  coverageEnabled: boolean;
  coverageLocalLcov: string;
  duplicationEnabled: boolean;
  dependenciesEnabled: boolean;
};

export const defaultSettings = (): HostSettings => ({
  serverUrl: '',
  projectKey: '',
  refreshIntervalSeconds: 300,
  diagnosticsEnabled: true,
  liveAnalysisEnabled: false,
  liveAnalysisTargetBranch: 'origin/master',
  liveAnalysisSemgrepConfig: ['.codeinsights/semgrep-mqr-v1.json'],
  liveAnalysisUseDocker: false,
  liveAnalysisImages: { semgrep: 'semgrep/semgrep:1.145.0', gitleaks: 'zricethezav/gitleaks:v8.30.0' },
  liveAnalysisTimeoutSeconds: 120,
  liveAnalysisMaxFiles: 100,
  coverageEnabled: true,
  coverageLocalLcov: '',
  duplicationEnabled: true,
  dependenciesEnabled: true,
});

export type InitializeParams = {
  cwd: string;
  cacheDir: string;
  token?: string;
  settings: HostSettings;
};

export type IssueRow = {
  id: number;
  severity: string;
  category: string;
  rule: string;
  tool: string;
  message: string;
  path?: string;
  line?: number;
  excepted: boolean;
  documentationUrl?: string;
  locationsSuffix: string;
  locations: {
    kind: string;
    path?: string;
    line?: number;
    column?: number;
    endColumn?: number;
    label: string;
  }[];
};

export type IssueGroupRow = { path?: string; label: string; worst: string; issues: IssueRow[] };

export type RuleRow = {
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

export type Snapshot = {
  cwd: string;
  signedIn: boolean;
  report: ViewState;
  reportNodes: ReportNode[];
  issues: { kind: 'message'; message: string } | { kind: 'issues'; groups: IssueGroupRow[]; truncated: boolean };
  issuesDescription: string;
  rules: { kind: 'message'; message: string } | { kind: 'rules'; groups: { tool: string; rules: RuleRow[] }[] };
  rulesDescription: string;
  details?: Details;
  status: { text: string; detail?: string; stale: boolean };
  notice?: string;
};

export type FileDiagnostic = {
  path: string;
  line: number;
  column?: number;
  endColumn?: number;
  severity: 'error' | 'warning' | 'information' | 'hint';
  message: string;
  code: string;
  source: 'CodeInsights' | 'CodeInsights (local)';
  documentationUrl?: string;
  unnecessary?: boolean;
};

export type FileLens = {
  line: number;
  text: string;
  kind: 'issues' | 'coverage' | 'duplication' | 'dependency';
};

export type FileContext = {
  path: string;
  diagnostics: FileDiagnostic[];
  lenses: FileLens[];
  coverage: { line: number; covered: boolean }[];
  coverageLabel?: string;
  duplications: { start: number; end: number; label: string }[];
  dependencies: { line: number; label: string; detail?: string }[];
};

export type TriageAction = 'assign' | 'unassign' | 'assign-other' | 'state' | 'comment';

export type TriageParams = {
  issueId: number;
  action: TriageAction;
  assigneeUserId?: string;
  state?: string;
  reason?: string;
  expiresAt?: string;
};

export type TriagePreview = {
  needsReason: boolean;
  needsExpiry: boolean;
  states: { id: string; label: string }[];
  people: { id: string; label: string; description?: string }[];
  currentState: string;
};

export type PromptResult = { prompt: string; label: string };

export type FilterIssuesParams = {
  severity: SeverityFilter;
  category: CategoryFilter;
  state: StateFilter;
  mine: boolean;
};

export type { Details, Issue, IssueFilters, ReportNode, Rule, RuleFilters, ViewState };
