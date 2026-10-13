import { chmod, readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { ApiError, CodeInsightsApi } from '../../core/src/api/client';
import {
  commentFinding,
  durableFinding,
  issuePrompt,
  people,
  updateFinding,
  type DurableFinding,
} from '../../core/src/api/collaboration';
import {
  components,
  coverageLines,
  duplications,
  findings,
  latestAnalysis,
  projectExceptions,
  projectRules,
  projectsForRepository,
  type CoverageLine,
  type DuplicateBlock,
  type RawAnalysis,
  type RawComponent,
  type RawFinding,
} from '../../core/src/api/codeinsights';
import { DiffSource } from '../../core/src/context/git-diff';
import { relativeToRoot, repositoryFile } from '../../core/src/context/paths';
import { projectSettings, type ProjectFile } from '../../core/src/context/project-file';
import { resolutionLabel, resolveProject, type Resolution } from '../../core/src/context/project-resolution';
import { hubBranchKey, hubUrl } from '../../core/src/hub/deep-link';
import { analyseChanges } from '../../core/src/local/session';
import { partialNotice, reportLabel, type Exception } from '../../core/src/local/analysis';
import { correctedIssues, estimateGate, type BranchIssue } from '../../core/src/local/gate';
import { alreadyInstalled, HOOK_MARKER } from '../../core/src/local/hook';
import { branchReport, verdictReason } from '../../core/src/report/branch-report';
import { ageLabel, cacheKey, issuesCacheKey, readCache, writeCache } from '../../core/src/report/cache';
import { coverageSummary, parseLcov, placeCoverage, preferLocal, type CoverageOrigin } from '../../core/src/report/coverage';
import { details, type Details } from '../../core/src/report/details';
import { diagnosticSpec } from '../../core/src/report/diagnostics';
import { dependencyLabel, manifestKind, matchComponent, parseManifest } from '../../core/src/report/dependencies';
import { anchor as anchorFor, type Anchor, type AnchorState } from '../../core/src/report/drift';
import { duplicateLabel, placeDuplicates } from '../../core/src/report/duplication';
import {
  countLabel,
  defaultIssueFilters,
  filtersLabel,
  groupIssues,
  issuesQuery,
  severityLabel,
  toIssue,
  type Issue,
  type IssueFilters,
  type IssueGroup,
} from '../../core/src/report/issues';
import { locationLabel, primaryRange, secondary } from '../../core/src/report/locations';
import { gateLabel } from '../../core/src/report/measure';
import { noticeFor, type Seen } from '../../core/src/report/notification';
import { reportNodes, type LocalSection, type ViewState } from '../../core/src/report/report-nodes';
import { defaultRuleFilters, filterRules, groupRules, rulesLabel, toRule, type Rule, type RuleFilters } from '../../core/src/report/rules';
import {
  availableStates,
  localPrompt,
  promptUnavailable,
  requiresExpiry,
  requiresReason,
  stateLabel,
  triageError,
  updateBody,
  validExpiry,
  type Change,
  type State,
} from '../../core/src/report/triage';
import { currentBranch, headSha, nodeWorkspace, remoteUrl, repositoryRoot } from './git';
import {
  defaultSettings,
  type FileContext,
  type FileDiagnostic,
  type FileLens,
  type FilterIssuesParams,
  type HostSettings,
  type InitializeParams,
  type IssueGroupRow,
  type IssueRow,
  type PromptResult,
  type Snapshot,
  type TriageParams,
  type TriagePreview,
} from './protocol';
import { FileStore } from './store';

const ISSUE_LIMIT = 500;
const ANCHOR_BUDGET = 50;
const PROMPT_CONTEXT_LINES = 20;
const SEEN_KEY = 'codeinsights.seenAnalysis';

export type LogFn = (level: 'info' | 'warn', message: string) => void;

type GitContext = { rootPath: string; branch?: string; headSha?: string; repositoryUrl?: string };

type Anchored = { issue: Issue; anchor: Anchor };

/**
 * Orchestration of the Visual Studio host. Transformations stay in apps/core; this
 * class only sequences them the way apps/vscode/src/extension.ts does.
 */
export class Session {
  private settings: HostSettings = defaultSettings();
  private token: string | undefined;
  private cwd = '';
  private store = new FileStore('/tmp/codeinsights-visualstudio-cache.json');
  private running = false;
  private lastReportState: ViewState | undefined;
  private localSection: LocalSection | undefined;
  private filters: IssueFilters = { ...defaultIssueFilters };
  private ruleFilters: RuleFilters = { ...defaultRuleFilters };
  private allRules: Rule[] = [];
  private lastIssue: Issue | undefined;
  private seen: Seen;
  private current: { analysis: RawAnalysis; rootPath: string; diffs: DiffSource } | undefined;
  private sbom: RawComponent[] | undefined;
  private coverageByPath = new Map<string, { state: string; lines: CoverageLine[] }>();
  private duplicationByPath = new Map<string, { state: string; duplications: DuplicateBlock[] }>();
  private anchored = new Map<string, Anchored[]>();
  private issuesByPath = new Map<string, Issue[]>();
  private identity: { userId: string; role: string } | undefined;
  private lastNotice: string | undefined;
  private issuesState: Snapshot['issues'] = { kind: 'message', message: 'Aucune analyse chargée' };
  private issuesDescription = '';
  private rulesState: Snapshot['rules'] = { kind: 'message', message: 'Aucune analyse chargée' };
  private rulesDescription = '';
  private detailsView: Details | undefined;
  private unauthorized = false;

  constructor(private readonly log: LogFn) {}

  async initialize(params: InitializeParams): Promise<Snapshot> {
    this.cwd = params.cwd;
    this.token = params.token?.trim() || undefined;
    this.settings = { ...defaultSettings(), ...params.settings };
    this.store = new FileStore(join(params.cacheDir, 'cache.json'));
    await this.store.load();
    this.seen = this.store.get(SEEN_KEY) as Seen;
    this.identity = undefined;
    return this.refresh();
  }

  async setSettings(settings: HostSettings): Promise<Snapshot> {
    this.settings = { ...defaultSettings(), ...settings };
    return this.refresh();
  }

  async setToken(token: string | undefined): Promise<Snapshot> {
    this.token = token?.trim() || undefined;
    this.identity = undefined;
    return this.refresh();
  }

  async setCwd(cwd: string): Promise<Snapshot> {
    this.cwd = cwd;
    return this.refresh();
  }

  snapshot(): Snapshot {
    const state: ViewState =
      this.lastReportState === undefined
        ? { kind: 'message', message: 'Aucune analyse chargée' }
        : this.lastReportState.kind === 'report' && this.localSection !== undefined
          ? { ...this.lastReportState, local: this.localSection }
          : this.lastReportState;
    const snapshot: Snapshot = {
      cwd: this.cwd,
      signedIn: this.token !== undefined,
      report: state,
      reportNodes: reportNodes(state),
      issues: this.issuesState,
      issuesDescription: this.issuesDescription,
      rules: this.rulesState,
      rulesDescription: this.rulesDescription,
      status: this.statusOf(state),
    };
    if (this.detailsView !== undefined) snapshot.details = this.detailsView;
    if (this.lastNotice !== undefined) snapshot.notice = this.lastNotice;
    return snapshot;
  }

  async refresh(): Promise<Snapshot> {
    this.lastNotice = undefined;
    if (this.running) return this.snapshot();
    this.running = true;
    try {
      await this.refreshUnlocked();
      return this.snapshot();
    } finally {
      this.running = false;
    }
  }

  async filterIssues(params: FilterIssuesParams): Promise<Snapshot> {
    this.filters = { severity: params.severity, category: params.category, state: params.state, mine: params.mine };
    if (this.current !== undefined) await this.loadIssues(this.api(), this.current.analysis, false);
    return this.snapshot();
  }

  async clearIssueFilters(): Promise<Snapshot> {
    this.filters = { ...defaultIssueFilters };
    if (this.current !== undefined) await this.loadIssues(this.api(), this.current.analysis, false);
    return this.snapshot();
  }

  async filterRules(params: RuleFilters): Promise<Snapshot> {
    this.ruleFilters = { ...params };
    this.applyRuleFilters();
    return this.snapshot();
  }

  async clearRuleFilters(): Promise<Snapshot> {
    this.ruleFilters = { ...defaultRuleFilters };
    this.applyRuleFilters();
    return this.snapshot();
  }

  async selectIssue(issueId: number): Promise<Snapshot> {
    const issue = this.findIssue(issueId);
    if (issue === undefined) {
      this.detailsView = undefined;
      return this.snapshot();
    }
    this.lastIssue = issue;
    this.detailsView = details(issue, this.anchorState(issue), this.current?.analysis.commit_sha);
    return this.snapshot();
  }

  async analyzeChanges(): Promise<Snapshot> {
    await this.analyseLocally();
    return this.snapshot();
  }

  async triagePreview(issueId: number): Promise<TriagePreview> {
    const issue = this.findIssue(issueId) ?? this.lastIssue;
    if (issue === undefined || this.current === undefined) throw new Error("Aucune issue sélectionnée.");
    this.lastIssue = issue;
    const client = this.api();
    const finding = await durableFinding(client, issue.id);
    const admin = await this.isAdmin(client);
    const states = availableStates(admin, finding.state).map((state) => ({ id: state, label: stateLabel(state) }));
    const members = await people(client, finding.project_key);
    return {
      needsReason: true,
      needsExpiry: false,
      states,
      people: members.map((person) => {
        const row: { id: string; label: string; description?: string } = {
          id: person.user_id,
          label: person.full_name ?? person.email ?? person.user_id,
        };
        if (person.email !== undefined) row.description = person.email;
        return row;
      }),
      currentState: finding.effective_state ?? finding.state,
    };
  }

  async triage(params: TriageParams): Promise<Snapshot> {
    const issue = this.findIssue(params.issueId) ?? this.lastIssue;
    if (issue === undefined || this.current === undefined) throw new Error("Aucune issue sélectionnée.");
    this.lastIssue = issue;
    const client = this.api();
    const finding = await durableFinding(client, issue.id);
    try {
      if (params.action === 'comment') {
        const reason = params.reason?.trim() ?? '';
        if (reason.length === 0) throw new Error('Un commentaire est requis.');
        await commentFinding(client, finding.id, finding.version, reason);
        this.lastNotice = 'Commentaire enregistré.';
        return this.snapshot();
      }
      const change = await this.buildChange(client, finding, params);
      if (requiresReason(finding, change) && change.reason.trim().length === 0)
        throw new Error('Un motif est requis par le hub.');
      const state = change.state ?? finding.state;
      if (requiresExpiry(state) && (change.expiresAt === undefined || change.expiresAt === null || !validExpiry(change.expiresAt, Date.now())))
        throw new Error("Date d'expiration invalide : elle doit être future et à moins d'un an.");
      const updated = await updateFinding(client, finding.id, updateBody(finding, change));
      this.lastNotice = `Issue mise à jour (${stateLabel(updated.effective_state ?? updated.state)}).`;
      return this.refresh();
    } catch (error) {
      throw new Error(error instanceof ApiError ? triageError(error.status, error.message) : String(error));
    }
  }

  async prompt(issueId: number): Promise<PromptResult> {
    const issue = this.findIssue(issueId) ?? this.lastIssue;
    if (issue === undefined) throw new Error("Aucune issue sélectionnée.");
    this.lastIssue = issue;
    const built = await this.promptFor(issue);
    if (built === undefined) throw new Error(`Prompt indisponible, ${promptUnavailable(issue.promptUnavailableReason)}.`);
    return built;
  }

  async fileContext(fsPath: string): Promise<FileContext> {
    const empty: FileContext = { path: fsPath, diagnostics: [], lenses: [], coverage: [], duplications: [], dependencies: [] };
    if (this.current === undefined) return empty;
    const path = relativeToRoot(this.current.rootPath, fsPath);
    if (path === undefined) return empty;
    await this.refreshAnchors(path, true);
    const diagnostics: FileDiagnostic[] = [];
    const lenses: FileLens[] = [];
    if (this.settings.diagnosticsEnabled) {
      for (const entry of this.anchored.get(path) ?? []) {
        const spec = diagnosticSpec(entry.issue, entry.anchor);
        if (spec === undefined) continue;
        const exact = entry.anchor.state === 'exact' || entry.anchor.state === 'shifted';
        const range = exact ? primaryRange(entry.issue.locations, entry.issue.line) : undefined;
        const row: FileDiagnostic = {
          path: fsPath,
          line: spec.line,
          severity: spec.severity,
          message: spec.message,
          code: spec.code,
          source: 'CodeInsights',
        };
        if (range !== undefined) {
          row.column = range.column;
          if (range.endColumn !== undefined) row.endColumn = range.endColumn;
        }
        if (spec.documentationUrl !== undefined) row.documentationUrl = spec.documentationUrl;
        if (spec.unnecessary) row.unnecessary = true;
        diagnostics.push(row);
      }
      const anchored = this.anchored.get(path) ?? [];
      if (anchored.length > 0) {
        const blockers = anchored.filter((entry) => entry.issue.severity === 'blocker').length;
        const approximate = anchored.filter((entry) => entry.anchor.state !== 'exact' && entry.anchor.state !== 'shifted').length;
        const parts = [anchored.length === 1 ? '1 issue' : `${anchored.length} issues`];
        if (blockers > 0) parts.push(blockers === 1 ? '1 bloquante' : `${blockers} bloquantes`);
        if (approximate > 0) parts.push(`${approximate} position(s) incertaine(s)`);
        lenses.push({ line: 1, text: `CodeInsights : ${parts.join(' · ')}`, kind: 'issues' });
      }
    }
    for (const finding of this.localSection?.introduced ?? []) {
      if (finding.path !== path) continue;
      diagnostics.push({
        path: fsPath,
        line: finding.line,
        severity: 'warning',
        message: finding.label,
        code: finding.description,
        source: 'CodeInsights (local)',
      });
    }
    const coverage = await this.coverageFor(path);
    if (coverage !== undefined) {
      if (coverage.label !== undefined) empty.coverageLabel = coverage.label;
      empty.coverage = coverage.marks;
      if (coverage.label !== undefined) lenses.push({ line: 1, text: `CodeInsights : ${coverage.label}`, kind: 'coverage' });
    }
    const dups = await this.duplicationsFor(path);
    empty.duplications = dups;
    if (dups.length > 0)
      lenses.push({
        line: 1,
        text: dups.length === 1 ? 'CodeInsights : 1 bloc dupliqué' : `CodeInsights : ${dups.length} blocs dupliqués`,
        kind: 'duplication',
      });
    empty.dependencies = await this.dependenciesFor(fsPath, path, lenses);
    empty.diagnostics = diagnostics;
    empty.lenses = lenses;
    empty.path = fsPath;
    return empty;
  }

  async hubLink(kind: 'report' | 'issue' | 'rule', id?: number | string): Promise<string> {
    if (this.settings.serverUrl.length === 0) throw new Error("URL du hub non configurée.");
    const git = await this.gitContext();
    const target: Parameters<typeof hubUrl>[0] = { serverUrl: this.settings.serverUrl };
    const resolution = await this.resolveProjectFor(git, this.api());
    if (resolution.state === 'resolved') target.project = resolution.projectKey;
    const analysed =
      this.current !== undefined && resolution.state === 'resolved' && this.current.analysis.project_key === resolution.projectKey
        ? hubBranchKey(this.current.analysis)
        : undefined;
    const branch = analysed ?? git?.branch;
    if (branch !== undefined) target.branch = branch;
    if (kind === 'issue' && typeof id === 'number') {
      const issue = this.findIssue(id);
      if (issue !== undefined && this.current !== undefined) {
        target.tab = 'issues';
        target.project = this.current.analysis.project_key;
        target.branch = hubBranchKey(this.current.analysis);
        target.analysisId = this.current.analysis.analysis_id;
        target.severity = issue.severity;
        if (issue.path !== undefined) target.file = issue.path;
      }
    }
    if (kind === 'rule' && typeof id === 'string' && this.current !== undefined) {
      const rule = this.allRules.find((entry) => entry.key === id);
      if (rule !== undefined) {
        target.tab = 'rules';
        target.project = this.current.analysis.project_key;
        target.selectedRule = `${rule.tool}/${rule.ruleId}`;
        target.ruleSearch = rule.ruleId;
        target.ruleStatus = rule.status;
      }
    }
    return hubUrl(target);
  }

  async installHook(): Promise<string> {
    const git = await this.gitContext();
    if (git === undefined) throw new Error('Ouvrir un dépôt Git pour proposer le crochet pre-push.');
    const hookPath = `${git.rootPath.replace(/\\/g, '/')}/.git/hooks/pre-push`;
    try {
      const existing = await readFile(hookPath, 'utf8');
      if (alreadyInstalled(existing)) return 'Le rappel pre-push est déjà en place.';
      throw new Error('Un crochet pre-push existe déjà. Il n’a pas été modifié.');
    } catch (error) {
      if (error instanceof Error && error.message.startsWith('Un crochet')) throw error;
    }
    const script = `#!/bin/sh
${HOOK_MARKER}
# Rappel installé depuis Visual Studio. Il n'empêche pas le push : l'estimation locale
# n'est pas le gate CI, et un crochet Git ne peut pas exécuter l'extension.
echo "CodeInsights : lancer « Analyser mes changements » dans Visual Studio avant de pousser si les changements n'ont pas été relus."
exit 0
`;
    await writeFile(hookPath, script, { encoding: 'utf8', mode: 0o755 });
    await chmod(hookPath, 0o755);
    return 'Rappel pre-push écrit. Il n’empêche pas le push.';
  }

  tookUnauthorized(): boolean {
    const value = this.unauthorized;
    this.unauthorized = false;
    return value;
  }

  private async refreshUnlocked(): Promise<void> {
    const git = await this.gitContext();
    if (git === undefined) return this.show({ kind: 'message', message: 'Aucun dossier ouvert' });
    if (this.settings.serverUrl.trim().length === 0)
      return this.show({ kind: 'message', message: 'URL du hub non configurée', detail: 'codeinsights.serverUrl' });
    if (this.token === undefined)
      return this.show({ kind: 'message', message: 'Non connecté', detail: 'Exécuter « CodeInsights : se connecter »' });
    const branch = git.branch;
    if (branch === undefined || branch.length === 0)
      return this.show({ kind: 'message', message: 'Branche Git inconnue', detail: 'Ouvrir un dépôt Git' });
    const client = this.api();
    const resolution = await this.resolveProjectFor(git, client);
    if (resolution.state !== 'resolved')
      return this.show({ kind: 'message', message: 'Projet non résolu', detail: resolutionLabel(resolution) });
    const key = cacheKey(resolution.projectKey, branch);
    let analysis: RawAnalysis | null;
    try {
      analysis = await latestAnalysis(client, resolution.projectKey, branch);
    } catch (error) {
      const cached = readCache<RawAnalysis>(this.store, key);
      const message = error instanceof ApiError ? error.message : String(error);
      this.log('warn', `Lecture du rapport impossible : ${message}`);
      if (cached === undefined) return this.show({ kind: 'message', message: 'Rapport indisponible', detail: message });
      const rootPath = git.rootPath;
      this.current = { analysis: cached.value, rootPath, diffs: new DiffSource(rootPath, nodeWorkspace.repositories) };
      this.show({
        kind: 'report',
        report: branchReport(cached.value),
        context: contextLine(branch, git),
        staleness: ageLabel(cached.storedAt, Date.now()),
      });
      await this.loadIssuesFromCache(cached.value);
      return;
    }
    if (analysis === null) {
      this.current = undefined;
      this.sbom = undefined;
      this.coverageByPath.clear();
      this.duplicationByPath.clear();
      this.anchored.clear();
      this.issuesByPath.clear();
      this.allRules = [];
      this.rulesState = { kind: 'message', message: 'Aucune analyse pour cette branche' };
      this.rulesDescription = '';
      this.issuesState = { kind: 'message', message: 'Aucune analyse pour cette branche' };
      this.issuesDescription = '';
      this.detailsView = undefined;
      return this.show({
        kind: 'message',
        message: 'Aucune analyse pour cette branche',
        detail: `${resolution.projectKey} · ${branch}`,
      });
    }
    await writeCache(this.store, key, analysis, Date.now());
    this.show({ kind: 'report', report: branchReport(analysis), context: contextLine(branch, git) });
    const rootPath = git.rootPath;
    this.current = { analysis, rootPath, diffs: new DiffSource(rootPath, nodeWorkspace.repositories) };
    this.coverageByPath.clear();
    this.duplicationByPath.clear();
    await this.announce(analysis);
    await this.loadIssues(client, analysis, false);
    await this.loadRules(client, analysis.project_key);
    await this.loadSbom(client, analysis.analysis_id);
  }

  private show(state: ViewState): void {
    this.lastReportState = state;
  }

  private statusOf(state: ViewState): Snapshot['status'] {
    if (state.kind === 'message') return { text: state.detail ?? state.message, stale: false };
    const reason = verdictReason(state.report);
    const estimate = state.local?.estimate;
    const detail = [
      `${state.report.projectKey} · ${state.report.scope}`,
      `Quality Gate : ${gateLabel(state.report.verdict)}${reason === undefined ? '' : ` (${reason})`}`,
      state.staleness === undefined ? undefined : `Rapport en cache, ${state.staleness}`,
      estimate === undefined ? undefined : `Estimation locale : ${estimate.label}`,
    ]
      .filter((line) => line !== undefined)
      .join('\n');
    return { text: `CodeInsights: ${gateLabel(state.report.verdict)}`, detail, stale: state.staleness !== undefined };
  }

  private async announce(analysis: RawAnalysis): Promise<void> {
    const decision = noticeFor(this.seen, { analysisId: analysis.analysis_id });
    this.seen = { analysisId: analysis.analysis_id };
    await this.store.update(SEEN_KEY, this.seen);
    if (!decision.notify) return;
    const report = branchReport(analysis);
    const reason = verdictReason(report);
    this.lastNotice = `Nouvelle analyse de ${report.scope} — ${gateLabel(report.verdict)}${reason === undefined ? '' : ` (${reason})`}`;
  }

  private async publishIssueGroups(analysis: RawAnalysis, raw: RawFinding[], stale: boolean): Promise<void> {
    const issues = raw.map(toIssue);
    const groups = groupIssues(issues);
    this.issuesDescription = `${filtersLabel(this.filters)} — ${countLabel(groups)}${stale ? ' (cache)' : ''}`;
    this.issuesByPath = new Map();
    this.anchored.clear();
    for (const group of groups) if (group.path !== undefined) this.issuesByPath.set(group.path, group.issues);
    if (this.current !== undefined) {
      this.current.diffs.clear();
      if (this.settings.diagnosticsEnabled) {
        let resolved = 0;
        for (const path of this.issuesByPath.keys()) {
          await this.refreshAnchors(path, resolved < ANCHOR_BUDGET);
          resolved += 1;
        }
      }
    }
    this.issuesState =
      groups.length === 0
        ? { kind: 'message', message: stale ? 'Aucune issue en cache pour ces filtres' : 'Aucune issue pour ces filtres' }
        : { kind: 'issues', groups: groups.map(toIssueGroupRow), truncated: raw.length >= ISSUE_LIMIT };
  }

  private async loadIssues(client: CodeInsightsApi, analysis: RawAnalysis, stale: boolean): Promise<void> {
    this.issuesState = { kind: 'message', message: 'Lecture des issues…' };
    this.issuesDescription = filtersLabel(this.filters);
    try {
      const raw = await findings(client, analysis.analysis_id, issuesQuery(this.filters), ISSUE_LIMIT);
      await writeCache(this.store, issuesCacheKey(analysis.project_key, analysis.branch), raw, Date.now());
      await this.publishIssueGroups(analysis, raw, stale);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : String(error);
      this.log('warn', `Lecture des issues impossible : ${message}`);
      const cached = readCache<RawFinding[]>(this.store, issuesCacheKey(analysis.project_key, analysis.branch));
      if (cached === undefined) {
        this.issuesState = { kind: 'message', message: `Issues indisponibles : ${message}` };
        return;
      }
      await this.publishIssueGroups(analysis, cached.value, true);
    }
  }

  private async loadIssuesFromCache(analysis: RawAnalysis): Promise<void> {
    const cached = readCache<RawFinding[]>(this.store, issuesCacheKey(analysis.project_key, analysis.branch));
    if (cached === undefined) {
      this.issuesState = { kind: 'message', message: 'Issues indisponibles hors ligne' };
      this.issuesDescription = filtersLabel(this.filters);
      return;
    }
    await this.publishIssueGroups(analysis, cached.value, true);
  }

  private applyRuleFilters(): void {
    const filtered = filterRules(this.allRules, this.ruleFilters);
    this.rulesState =
      filtered.length === 0
        ? { kind: 'message', message: 'Aucune règle pour ces filtres' }
        : { kind: 'rules', groups: groupRules(filtered).map((group) => ({ tool: group.tool, rules: group.rules })) };
    this.rulesDescription = rulesLabel(this.ruleFilters, filtered.length);
  }

  private async loadRules(client: CodeInsightsApi, projectKey: string): Promise<void> {
    this.rulesState = { kind: 'message', message: 'Lecture des règles…' };
    this.rulesDescription = rulesLabel(this.ruleFilters, 0);
    try {
      this.allRules = (await projectRules(client, projectKey)).map(toRule);
      this.applyRuleFilters();
    } catch (error) {
      const message = error instanceof ApiError ? error.message : String(error);
      this.log('warn', `Lecture des règles impossible : ${message}`);
      this.allRules = [];
      this.rulesState = { kind: 'message', message: `Règles indisponibles : ${message}` };
    }
  }

  private async loadSbom(client: CodeInsightsApi, analysisId: string): Promise<void> {
    if (!this.settings.dependenciesEnabled) {
      this.sbom = undefined;
      return;
    }
    try {
      this.sbom = await components(client, analysisId);
    } catch (error) {
      this.log('warn', `SBOM indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
      this.sbom = undefined;
    }
  }

  private async refreshAnchors(path: string, withDiff: boolean): Promise<void> {
    const context = this.current;
    const issues = this.issuesByPath.get(path);
    if (context === undefined || issues === undefined) return;
    const hunks = withDiff ? await context.diffs.hunks(context.analysis.commit_sha, path) : undefined;
    const anchored: Anchored[] = [];
    for (const issue of issues) anchored.push({ issue, anchor: anchorFor(issue.line, hunks) });
    this.anchored.set(path, anchored);
  }

  private async coverageFor(path: string): Promise<{ marks: { line: number; covered: boolean }[]; label?: string } | undefined> {
    if (!this.settings.coverageEnabled || this.current === undefined) return undefined;
    let origin: CoverageOrigin = 'analysis';
    let coverageState = 'not_measured';
    let lines: CoverageLine[] = [];
    const localPath = this.settings.coverageLocalLcov.trim();
    if (localPath.length > 0) {
      const resolved = repositoryFile(this.current.rootPath, localPath);
      if (resolved !== undefined) {
        try {
          const info = await stat(resolved);
          if (preferLocal(info.mtimeMs, this.current.analysis.created_at, Date.now())) {
            const parsed = parseLcov(await readFile(resolved, 'utf8'), this.current.rootPath);
            const local = parsed.get(path);
            if (local !== undefined) {
              origin = 'local';
              coverageState = 'measured';
              lines = local;
            }
          }
        } catch {
          this.log('warn', `LCOV local illisible : ${localPath}`);
        }
      }
    }
    if (origin === 'analysis') {
      const cached = this.coverageByPath.get(path);
      if (cached !== undefined) {
        coverageState = cached.state;
        lines = cached.lines;
      } else {
        try {
          const payload = await coverageLines(this.api(), this.current.analysis.analysis_id, path);
          coverageState = payload.state ?? 'not_measured';
          lines = payload.lines;
          this.coverageByPath.set(path, { state: coverageState, lines });
        } catch (error) {
          this.log('warn', `Couverture indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
        }
      }
    }
    const summary = coverageSummary(coverageState, lines, origin);
    const label = `Couverture : ${summary.display}`;
    if (summary.state !== 'measured') return { marks: [], label };
    const hunks = origin === 'local' ? undefined : await this.current.diffs.hunks(this.current.analysis.commit_sha, path);
    return { marks: placeCoverage(lines, hunks, origin).map((row) => ({ line: row.line, covered: row.covered })), label };
  }

  private async duplicationsFor(path: string): Promise<{ start: number; end: number; label: string }[]> {
    if (!this.settings.duplicationEnabled || this.current === undefined) return [];
    const name = path.split('/').slice(-1)[0] ?? path;
    const cached = this.duplicationByPath.get(path);
    let state = 'not_measured';
    let blocks: DuplicateBlock[] = [];
    if (cached !== undefined) {
      state = cached.state;
      blocks = cached.duplications;
    } else {
      try {
        const payload = await duplications(this.api(), this.current.analysis.analysis_id, name);
        state = payload.state ?? 'not_measured';
        blocks = payload.duplications;
        this.duplicationByPath.set(path, { state, duplications: blocks });
      } catch (error) {
        this.log('warn', `Duplications indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
      }
    }
    if (state !== 'measured') return [];
    const hunks = await this.current.diffs.hunks(this.current.analysis.commit_sha, path);
    return placeDuplicates(path, blocks, hunks).map((block) => ({ start: block.start, end: block.end, label: duplicateLabel(block) }));
  }

  private async dependenciesFor(fsPath: string, path: string, lenses: FileLens[]): Promise<{ line: number; label: string; detail?: string }[]> {
    if (!this.settings.dependenciesEnabled) return [];
    const kind = manifestKind(path);
    if (kind === undefined) return [];
    let text: string;
    try {
      text = await readFile(fsPath, 'utf8');
    } catch {
      return [];
    }
    const loaded = this.sbom !== undefined;
    const marks: { line: number; label: string; detail?: string }[] = [];
    for (const hit of parseManifest(kind, text)) {
      const match = loaded ? matchComponent(hit.name, hit.kind, this.sbom ?? []) : undefined;
      const label = `CodeInsights : ${dependencyLabel(match, loaded)}`;
      const mark: { line: number; label: string; detail?: string } = { line: hit.line, label };
      if (match?.purl !== undefined) mark.detail = match.purl;
      marks.push(mark);
      lenses.push({ line: hit.line, text: label, kind: 'dependency' });
    }
    return marks;
  }

  private async analyseLocally(): Promise<void> {
    if (this.current === undefined) {
      this.lastNotice = 'Ouvrir un dépôt analysé avant de lancer l’analyse locale.';
      return;
    }
    const declared = projectSettings(await this.readMapping());
    const settings = {
      semgrepConfigs: declared.semgrepConfigs ?? this.settings.liveAnalysisSemgrepConfig.filter((entry) => entry.trim().length > 0),
      useDocker: this.settings.liveAnalysisUseDocker,
      images: this.settings.liveAnalysisImages,
      timeoutMs: this.settings.liveAnalysisTimeoutSeconds * 1000,
      maxFiles: this.settings.liveAnalysisMaxFiles,
    };
    const hub = await this.hubPolicy(this.current.analysis.project_key);
    const run = await analyseChanges(
      nodeWorkspace,
      this.current.rootPath,
      declared.targetBranch ?? this.settings.liveAnalysisTargetBranch,
      settings,
      hub,
      (message) => this.log('warn', message),
      (tool) => this.log('info', tool),
    );
    if (run === undefined) {
      this.lastNotice = 'Diff local indisponible (git absent ou dépôt sans révision de base).';
      return;
    }
    let branchIssues: BranchIssue[] = [];
    try {
      const raw = await findings(this.api(), this.current.analysis.analysis_id, '', ISSUE_LIMIT);
      branchIssues = raw.flatMap((row) => {
        const path = row.path === null || row.path === undefined || row.path.length === 0 ? undefined : row.path.replace(/\\/g, '/');
        const line = typeof row.line === 'number' && row.line > 0 ? row.line : undefined;
        if (path === undefined || line === undefined || row.excepted === true) return [];
        return [
          {
            tool: row.tool ?? 'unknown',
            rule: row.rule_id ?? 'unknown',
            path,
            line,
            category: row.category ?? 'unknown',
            severity: row.severity ?? 'info',
            message: row.message ?? '',
          },
        ];
      });
    } catch (error) {
      this.log('warn', `Issues de branche indisponibles pour l’estimation : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    const corrected = correctedIssues(
      branchIssues,
      run.report,
      run.changed.map((file) => file.path),
    );
    const estimate = estimateGate(this.current.analysis.gate_snapshot?.conditions ?? [], run.report, corrected);
    this.localSection = {
      label: reportLabel(run.report),
      notice: partialNotice(run.report),
      baseRef: run.baseRef,
      producers: run.report.producers.map((producer) => ({ tool: producer.tool, state: producer.state, reason: producer.reason })),
      suppressed: run.report.suppressed,
      introduced: run.report.introduced.map((finding, index) => ({
        id: `local.finding.${index}`,
        label: finding.message.length === 0 ? finding.rule : finding.message,
        description: `${severityLabel(finding.severity)} · ${finding.rule} · ${finding.path}:${finding.line}`,
        path: finding.path,
        line: finding.line,
      })),
      corrected: corrected.map((finding, index) => ({
        id: `local.corrected.${index}`,
        label: finding.message.length === 0 ? finding.rule : finding.message,
        description: `corrigée (estimation) · ${finding.rule} · ${finding.path}:${finding.line}`,
        path: finding.path,
        line: finding.line,
      })),
      estimate: {
        label: estimate.label,
        notice: estimate.notice,
        effect: estimate.effect,
        conditions: estimate.conditions.map((condition) => ({
          metric: condition.metric,
          label: condition.label,
          display: condition.display,
          result: condition.result,
        })),
      },
    };
    const notice = partialNotice(run.report);
    if (notice !== undefined) this.log('warn', notice);
    this.log(
      'info',
      `Analyse locale (${run.baseRef}) : ${reportLabel(run.report)} · estimation : ${estimate.label}${run.report.suppressed > 0 ? ` · ${run.report.suppressed} masquée(s) par exception ou règle désactivée` : ''}`,
    );
    this.lastNotice = `CodeInsights : ${reportLabel(run.report)}. Estimation du gate : ${estimate.label}.${notice === undefined ? '' : ` ${notice}`}`;
  }

  private async hubPolicy(projectKey: string): Promise<{ exceptions: Exception[]; disabledRules: string[] }> {
    const client = this.api();
    const result: { exceptions: Exception[]; disabledRules: string[] } = { exceptions: [], disabledRules: [] };
    try {
      result.exceptions = await projectExceptions(client, projectKey, Date.now());
    } catch (error) {
      this.log('warn', `Exceptions du projet indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    try {
      const rules = await projectRules(client, projectKey, 'status=disabled');
      result.disabledRules = rules.filter((rule) => rule.status === 'disabled').map((rule) => `${rule.tool ?? ''}/${rule.rule_id ?? ''}`);
    } catch (error) {
      this.log('warn', `Règles désactivées indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    return result;
  }

  private async promptFor(issue: Issue): Promise<PromptResult | undefined> {
    if (issue.promptAvailable) {
      try {
        return { prompt: await issuePrompt(this.api(), issue.id), label: 'source analysée' };
      } catch (error) {
        this.log('warn', `Prompt du hub indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
      }
    }
    if (issue.path === undefined || this.current === undefined) return undefined;
    const file = repositoryFile(this.current.rootPath, issue.path);
    if (file === undefined) return undefined;
    try {
      const lines = (await readFile(file, 'utf8')).split('\n');
      return { prompt: localPrompt(issue, issue.line, lines, PROMPT_CONTEXT_LINES), label: 'fichier local' };
    } catch {
      return undefined;
    }
  }

  private async buildChange(client: CodeInsightsApi, finding: DurableFinding, params: TriageParams): Promise<Change> {
    const change: Change = { reason: params.reason?.trim() ?? '' };
    if (params.action === 'assign') change.assigneeUserId = await this.me(client);
    else if (params.action === 'unassign') change.assigneeUserId = null;
    else if (params.action === 'assign-other') change.assigneeUserId = params.assigneeUserId ?? null;
    else if (params.action === 'state' && params.state !== undefined) change.state = params.state as State;
    if (params.expiresAt !== undefined) change.expiresAt = params.expiresAt;
    return change;
  }

  private async me(client: CodeInsightsApi): Promise<string> {
    return (await this.whoami(client)).userId;
  }

  private async isAdmin(client: CodeInsightsApi): Promise<boolean> {
    return (await this.whoami(client)).role === 'admin';
  }

  private async whoami(client: CodeInsightsApi): Promise<{ userId: string; role: string }> {
    if (this.identity !== undefined) return this.identity;
    const body = await client.json<{ user_id?: string; id?: string; role?: string; access_role?: string }>('/api/v1/auth/me');
    this.identity = { userId: body.user_id ?? body.id ?? '', role: body.role ?? body.access_role ?? 'default' };
    return this.identity;
  }

  private api(): CodeInsightsApi {
    return new CodeInsightsApi({
      serverUrl: this.settings.serverUrl,
      token: () => Promise.resolve(this.token),
      onUnauthorized: () => {
        this.unauthorized = true;
        this.log('warn', 'Jeton refusé par le hub : absent, expiré ou révoqué.');
      },
    });
  }

  private async gitContext(): Promise<GitContext | undefined> {
    if (this.cwd.length === 0) return undefined;
    const rootPath = await repositoryRoot(this.cwd);
    if (rootPath === undefined) return undefined;
    const context: GitContext = { rootPath };
    const branch = await currentBranch(rootPath);
    if (branch !== undefined) context.branch = branch;
    const sha = await headSha(rootPath);
    if (sha !== undefined) context.headSha = sha;
    const url = await remoteUrl(rootPath);
    if (url !== undefined) context.repositoryUrl = url;
    return context;
  }

  private async resolveProjectFor(git: GitContext | undefined, client: CodeInsightsApi): Promise<Resolution> {
    const folderName = (git?.rootPath ?? this.cwd).split(/[\\/]/).filter((part) => part.length > 0).slice(-1)[0] ?? '';
    const sources: Parameters<typeof resolveProject>[0] = { setting: this.settings.projectKey, folderName };
    const mapping = await this.readMapping();
    if (mapping !== undefined) sources.mapping = mapping;
    if (git?.repositoryUrl !== undefined) sources.repositoryUrl = git.repositoryUrl;
    return resolveProject(sources, (repositoryUrl) => projectsForRepository(client, repositoryUrl));
  }

  private async readMapping(): Promise<ProjectFile | undefined> {
    const git = await this.gitContext();
    const root = git?.rootPath ?? this.cwd;
    if (root.length === 0) return undefined;
    try {
      const parsed: unknown = JSON.parse(await readFile(join(root, '.codeinsights', 'vscode.json'), 'utf8'));
      return parsed !== null && typeof parsed === 'object' ? (parsed as ProjectFile) : undefined;
    } catch {
      return undefined;
    }
  }

  private findIssue(id: number): Issue | undefined {
    if (this.issuesState.kind !== 'issues') return undefined;
    for (const group of this.issuesState.groups) {
      const issue = group.issues.find((entry) => entry.id === id);
      if (issue !== undefined) return this.issuesByPath.get(group.path ?? '')?.find((entry) => entry.id === id) ?? toIssueFromRow(issue);
    }
    return undefined;
  }

  private anchorState(issue: Issue): AnchorState | undefined {
    for (const anchored of this.anchored.values()) {
      const match = anchored.find((entry) => entry.issue.id === issue.id);
      if (match !== undefined) return match.anchor.state;
    }
    return undefined;
  }
}

function contextLine(branch: string, git: GitContext | undefined): { branch: string; headSha?: string } {
  return git?.headSha === undefined ? { branch } : { branch, headSha: git.headSha };
}

function toIssueGroupRow(group: IssueGroup): IssueGroupRow {
  const row: IssueGroupRow = { label: group.label, worst: group.worst, issues: group.issues.map(toIssueRow) };
  if (group.path !== undefined) row.path = group.path;
  return row;
}

function toIssueRow(issue: Issue): IssueRow {
  const row: IssueRow = {
    id: issue.id,
    severity: issue.severity,
    category: issue.category,
    rule: issue.rule,
    tool: issue.tool,
    message: issue.message,
    excepted: issue.excepted,
    locationsSuffix: issue.locationsSuffix,
    locations: secondary(issue.locations).map((location) => {
      const item: IssueRow['locations'][number] = { kind: location.kind, label: locationLabel(location) };
      if (location.path !== undefined) item.path = location.path;
      if (location.line !== undefined) item.line = location.line;
      if (location.column !== undefined) item.column = location.column;
      if (location.endColumn !== undefined) item.endColumn = location.endColumn;
      return item;
    }),
  };
  if (issue.path !== undefined) row.path = issue.path;
  if (issue.line !== undefined) row.line = issue.line;
  if (issue.documentationUrl !== undefined) row.documentationUrl = issue.documentationUrl;
  return row;
}

function toIssueFromRow(row: IssueRow): Issue {
  return {
    id: row.id,
    severity: row.severity as Issue['severity'],
    category: row.category,
    rule: row.rule,
    tool: row.tool,
    message: row.message,
    path: row.path,
    line: row.line,
    excepted: row.excepted,
    documentationUrl: row.documentationUrl,
    promptAvailable: false,
    promptUnavailableReason: undefined,
    locations: [],
    locationsTruncated: false,
    locationsSuffix: row.locationsSuffix,
  };
}
