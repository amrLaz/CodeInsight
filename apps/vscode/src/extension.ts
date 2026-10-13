import { chmod, readFile, stat, writeFile } from 'node:fs/promises';
import * as vscode from 'vscode';
import { ApiError, CodeInsightsApi } from '../../core/src/api/client';
import { commentFinding, durableFinding, issuePrompt, people, updateFinding, type DurableFinding } from '../../core/src/api/collaboration';
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
import { TokenStore } from './auth/token-store';
import { gitContext, type GitContext } from './context/git-context';
import { projectSettings, type ProjectFile } from '../../core/src/context/project-file';
import { resolveProject, resolutionLabel, type Resolution } from '../../core/src/context/project-resolution';
import { DiffSource } from '../../core/src/context/git-diff';
import { vscodeWorkspace } from './context/workspace';
import { relativeToRoot, repositoryFile } from '../../core/src/context/paths';
import { activeFolder } from './context/workspace-folder';
import { locate, reveal } from './context/workspace-file';
import { registerMcpServer } from './mcp/provider';
import { claudeSetup } from './mcp/claude-setup';
import type { IssueLocation } from '../../core/src/report/locations';
import { hubBranchKey, hubUrl } from '../../core/src/hub/deep-link';
import { branchReport, verdictReason } from '../../core/src/report/branch-report';
import { ageLabel, cacheKey, issuesCacheKey, readCache, writeCache } from '../../core/src/report/cache';
import {
  countLabel,
  defaultIssueFilters,
  filtersLabel,
  groupIssues,
  issuesQuery,
  toIssue,
  type CategoryFilter,
  type Issue,
  type IssueFilters,
  severityLabel,
  type SeverityFilter,
  type StateFilter,
} from '../../core/src/report/issues';
import { gateLabel } from '../../core/src/report/measure';
import { noticeFor, type Seen } from '../../core/src/report/notification';
import {
  localPrompt,
  promptUnavailable,
  requiresExpiry,
  requiresReason,
  stateLabel,
  triageError,
  updateBody,
  validExpiry,
  availableStates,
  type Change,
  type State,
} from '../../core/src/report/triage';
import { parseLcov, preferLocal, type CoverageOrigin } from '../../core/src/report/coverage';
import { defaultRuleFilters, filterRules, groupRules, rulesLabel, toRule, type Rule, type RuleFilters } from '../../core/src/report/rules';
import type { LocalSection, ViewState } from '../../core/src/report/report-nodes';
import { analyseChanges } from '../../core/src/local/session';
import { partialNotice, reportLabel, type Exception } from '../../core/src/local/analysis';
import { correctedIssues, estimateGate, type BranchIssue } from '../../core/src/local/gate';
import { alreadyInstalled, prePushHook } from '../../core/src/local/hook';
import { registerParticipant } from './chat/participant';
import { Annotations } from './views/annotations';
import { CoverageDecorations } from './views/coverage';
import { DependencyAnnotations } from './views/dependencies';
import { DetailsView } from './views/details-view';
import { DuplicationDecorations } from './views/duplication';
import { LocalAnnotations } from './views/local-annotations';
import { QualityLenses } from './views/quality-lenses';
import { BranchReportView } from './views/branch-report-view';
import { IssuesView } from './views/issues-view';
import { RulesView } from './views/rules-view';
import { StatusBar } from './views/status-bar';

const SEEN_KEY = 'codeinsights.seenAnalysis';
// Bound the read: a very large analysis must not freeze the tree or the extension host.
const ISSUE_LIMIT = 500;

// Files anchored with their diff at each refresh; the rest resolve when opened.
const ANCHOR_BUDGET = 50;

// Lines of context in a prompt built locally, matching Ingestion:PromptContextLines.
const PROMPT_CONTEXT_LINES = 20;

/**
 * Lots L7–L9: the local run estimates the gate without ever displaying a local « pass »,
 * the last analysis' coverage/duplication/SBOM/rules are shown in the editor, and the
 * workspace can be multi-root. The CI verdict in the status bar stays the hub's.
 */
export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel('CodeInsights', { log: true });
  const tokens = new TokenStore(context);
  const status = new StatusBar();
  const view = new BranchReportView();
  const issuesView = new IssuesView();
  const issuesTree = vscode.window.createTreeView('codeinsights.issues', { treeDataProvider: issuesView, showCollapseAll: true });
  issuesView.attach(issuesTree);
  const rulesView = new RulesView();
  const rulesTree = vscode.window.createTreeView('codeinsights.rules', { treeDataProvider: rulesView, showCollapseAll: true });
  rulesView.attach(rulesTree);
  let timer: NodeJS.Timeout | undefined;
  let running = false;
  const annotations = new Annotations();
  const detailsView = new DetailsView();
  const localAnnotations = new LocalAnnotations();
  const coverageDecorations = new CoverageDecorations();
  const duplicationDecorations = new DuplicationDecorations();
  const qualityLenses = new QualityLenses(coverageDecorations, duplicationDecorations);
  const dependencyAnnotations = new DependencyAnnotations();
  let localSection: LocalSection | undefined;
  let lastReportState: ViewState | undefined;
  let filters: IssueFilters = { ...defaultIssueFilters };
  let ruleFilters: RuleFilters = { ...defaultRuleFilters };
  let allRules: Rule[] = [];
  let lastIssue: Issue | undefined;
  let seen: Seen = context.workspaceState.get<Seen>(SEEN_KEY);
  let current: { analysis: RawAnalysis; rootPath: string; diffs: DiffSource } | undefined;
  let lastFolder: vscode.WorkspaceFolder | undefined;
  let sbom: RawComponent[] | undefined;
  const coverageByPath = new Map<string, { state: string; lines: CoverageLine[] }>();
  const duplicationByPath = new Map<string, { state: string; duplications: DuplicateBlock[] }>();

  const api = (): CodeInsightsApi =>
    new CodeInsightsApi({
      serverUrl: serverUrl(),
      token: () => tokens.get(),
      onUnauthorized: () => {
        output.warn('Jeton refusé par le hub : absent, expiré ou révoqué.');
        void vscode.window
          .showWarningMessage('CodeInsights : jeton invalide ou expiré.', 'Se connecter')
          .then((choice) => {
            if (choice !== undefined) void vscode.commands.executeCommand('codeinsights.signIn');
          });
      },
    });

  const show = (state: ViewState): void => {
    lastReportState = state;
    view.setState(state.kind === 'report' && localSection !== undefined ? { ...state, local: localSection } : state);
    if (state.kind === 'message') status.setIdle(state.detail ?? state.message);
    else {
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
      status.setVerdict(state.report.verdict, detail, state.staleness !== undefined);
    }
  };

  const refresh = async (): Promise<void> => {
    if (running) return;
    running = true;
    try {
      const folder = activeFolder();
      if (folder === undefined) return show({ kind: 'message', message: 'Aucun dossier ouvert' });
      lastFolder = folder;
      if (serverUrl().length === 0)
        return show({ kind: 'message', message: 'URL du hub non configurée', detail: 'codeinsights.serverUrl' });
      if ((await tokens.get()) === undefined)
        return show({ kind: 'message', message: 'Non connecté', detail: 'Exécuter « CodeInsights : se connecter »' });

      const git = await gitContext(folder);
      const branch = git?.branch;
      if (branch === undefined || branch.length === 0)
        return show({ kind: 'message', message: 'Branche Git inconnue', detail: 'Ouvrir un dépôt Git' });

      const client = api();
      const resolution = await resolveProjectFor(folder, git, client);
      if (resolution.state !== 'resolved')
        return show({ kind: 'message', message: 'Projet non résolu', detail: resolutionLabel(resolution) });

      status.setBusy();
      const key = cacheKey(resolution.projectKey, branch);
      let analysis: RawAnalysis | null;
      try {
        analysis = await latestAnalysis(client, resolution.projectKey, branch);
      } catch (error) {
        // Offline or refused: the cached report is shown with its age, never as current.
        const cached = readCache<RawAnalysis>(context.workspaceState, key);
        const message = error instanceof ApiError ? error.message : String(error);
        output.warn(`Lecture du rapport impossible : ${message}`);
        if (cached === undefined)
          return show({ kind: 'message', message: 'Rapport indisponible', detail: message });
        const rootPath = git?.rootPath ?? folder.uri.fsPath;
        current = { analysis: cached.value, rootPath, diffs: new DiffSource(rootPath, vscodeWorkspace.repositories) };
        qualityLenses.setRoot(rootPath);
        show({
          kind: 'report',
          report: branchReport(cached.value),
          context: contextLine(branch, git),
          staleness: ageLabel(cached.storedAt, Date.now()),
        });
        await loadIssuesFromCache(cached.value, true);
        void decorateEditor(vscode.window.activeTextEditor);
        return;
      }
      if (analysis === null) {
        current = undefined;
        sbom = undefined;
        coverageByPath.clear();
        duplicationByPath.clear();
        coverageDecorations.clear();
        duplicationDecorations.clear();
        dependencyAnnotations.set(folder.uri.fsPath, undefined);
        allRules = [];
        rulesView.setState({ kind: 'message', message: 'Aucune analyse pour cette branche' }, '');
        await annotations.publish([], { rootPath: folder.uri.fsPath, commitSha: '', diffs: new DiffSource(folder.uri.fsPath, vscodeWorkspace.repositories) }, 0);
        issuesView.setState({ kind: 'message', message: 'Aucune analyse pour cette branche' }, '');
        return show({
          kind: 'message',
          message: 'Aucune analyse pour cette branche',
          detail: `${resolution.projectKey} · ${branch}`,
        });
      }
      await writeCache(context.workspaceState, key, analysis, Date.now());
      show({ kind: 'report', report: branchReport(analysis), context: contextLine(branch, git) });
      const rootPath = git?.rootPath ?? folder.uri.fsPath;
      current = { analysis, rootPath, diffs: new DiffSource(rootPath, vscodeWorkspace.repositories) };
      qualityLenses.setRoot(rootPath);
      coverageByPath.clear();
      duplicationByPath.clear();
      await announce(analysis);
      await loadIssues(client, analysis, false);
      await loadRules(client, analysis.project_key);
      await loadSbom(client, analysis.analysis_id, rootPath);
      void decorateEditor(vscode.window.activeTextEditor);
    } finally {
      running = false;
    }
  };

  /** Warns once when the branch received a new analysis, with its verdict. */
  const announce = async (analysis: RawAnalysis): Promise<void> => {
    const decision = noticeFor(seen, { analysisId: analysis.analysis_id });
    seen = { analysisId: analysis.analysis_id };
    await context.workspaceState.update(SEEN_KEY, seen);
    if (!decision.notify) return;
    const report = branchReport(analysis);
    const reason = verdictReason(report);
    void vscode.window
      .showInformationMessage(
        `CodeInsights : nouvelle analyse de ${report.scope} — ${gateLabel(report.verdict)}${reason === undefined ? '' : ` (${reason})`}`,
        'Ouvrir dans le hub',
      )
      .then((choice) => {
        if (choice !== undefined) void vscode.commands.executeCommand('codeinsights.openInHub');
      });
  };

  const publishIssueGroups = async (analysis: RawAnalysis, raw: RawFinding[], stale: boolean): Promise<void> => {
    const groups = groupIssues(raw.map(toIssue));
    const description = `${filtersLabel(filters)} — ${countLabel(groups)}${stale ? ' (cache)' : ''}`;
    const publishDiagnostics = vscode.workspace.getConfiguration('codeinsights').get<boolean>('diagnostics.enabled', true);
    if (current !== undefined) {
      current.diffs.clear();
      await annotations.publish(
        publishDiagnostics ? groups : [],
        { rootPath: current.rootPath, commitSha: analysis.commit_sha, diffs: current.diffs },
        ANCHOR_BUDGET,
      );
    }
    issuesView.setState(
      groups.length === 0
        ? { kind: 'message', message: stale ? 'Aucune issue en cache pour ces filtres' : 'Aucune issue pour ces filtres' }
        : { kind: 'issues', groups, truncated: raw.length >= ISSUE_LIMIT },
      description,
    );
  };

  const loadIssues = async (client: CodeInsightsApi, analysis: RawAnalysis, stale: boolean): Promise<void> => {
    issuesView.setState({ kind: 'message', message: 'Lecture des issues…' }, filtersLabel(filters));
    try {
      const raw = await findings(client, analysis.analysis_id, issuesQuery(filters), ISSUE_LIMIT);
      await writeCache(context.workspaceState, issuesCacheKey(analysis.project_key, analysis.branch), raw, Date.now());
      await publishIssueGroups(analysis, raw, stale);
    } catch (error) {
      const message = error instanceof ApiError ? error.message : String(error);
      output.warn(`Lecture des issues impossible : ${message}`);
      const cached = readCache<RawFinding[]>(context.workspaceState, issuesCacheKey(analysis.project_key, analysis.branch));
      if (cached === undefined) {
        issuesView.setState({ kind: 'message', message: `Issues indisponibles : ${message}` }, filtersLabel(filters));
        return;
      }
      await publishIssueGroups(analysis, cached.value, true);
    }
  };

  const loadIssuesFromCache = async (analysis: RawAnalysis, stale: boolean): Promise<void> => {
    const cached = readCache<RawFinding[]>(context.workspaceState, issuesCacheKey(analysis.project_key, analysis.branch));
    if (cached === undefined) {
      issuesView.setState({ kind: 'message', message: 'Issues indisponibles hors ligne' }, filtersLabel(filters));
      return;
    }
    await publishIssueGroups(analysis, cached.value, stale);
  };

  const applyRuleFilters = (): void => {
    const filtered = filterRules(allRules, ruleFilters);
    rulesView.setState(
      filtered.length === 0 ? { kind: 'message', message: 'Aucune règle pour ces filtres' } : { kind: 'rules', groups: groupRules(filtered) },
      rulesLabel(ruleFilters, filtered.length),
    );
  };

  const loadRules = async (client: CodeInsightsApi, projectKey: string): Promise<void> => {
    rulesView.setState({ kind: 'message', message: 'Lecture des règles…' }, rulesLabel(ruleFilters, 0));
    try {
      allRules = (await projectRules(client, projectKey)).map(toRule);
      applyRuleFilters();
    } catch (error) {
      const message = error instanceof ApiError ? error.message : String(error);
      output.warn(`Lecture des règles impossible : ${message}`);
      allRules = [];
      rulesView.setState({ kind: 'message', message: `Règles indisponibles : ${message}` }, rulesLabel(ruleFilters, 0));
    }
  };

  const loadSbom = async (client: CodeInsightsApi, analysisId: string, rootPath: string): Promise<void> => {
    if (!vscode.workspace.getConfiguration('codeinsights').get<boolean>('dependencies.enabled', true)) {
      sbom = undefined;
      dependencyAnnotations.set(rootPath, undefined);
      return;
    }
    try {
      sbom = await components(client, analysisId);
      dependencyAnnotations.set(rootPath, sbom);
    } catch (error) {
      output.warn(`SBOM indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
      sbom = undefined;
      dependencyAnnotations.set(rootPath, undefined);
    }
  };

  const decorateEditor = async (editor: vscode.TextEditor | undefined): Promise<void> => {
    if (editor === undefined || current === undefined) return;
    const path = relativeToRoot(current.rootPath, editor.document.uri.fsPath);
    if (path === undefined) return;
    const configuration = vscode.workspace.getConfiguration('codeinsights');
    const client = api();
    let origin: CoverageOrigin = 'analysis';
    let coverageState = 'not_measured';
    let lines: CoverageLine[] = [];
    if (configuration.get<boolean>('coverage.enabled', true)) {
      const localPath = configuration.get<string>('coverage.localLcov', '').trim();
      if (localPath.length > 0) {
        const resolved = repositoryFile(current.rootPath, localPath);
        if (resolved !== undefined) {
          try {
            const info = await stat(resolved);
            if (preferLocal(info.mtimeMs, current.analysis.created_at, Date.now())) {
              const parsed = parseLcov(await readFile(resolved, 'utf8'), current.rootPath);
              const local = parsed.get(path);
              if (local !== undefined) {
                origin = 'local';
                coverageState = 'measured';
                lines = local;
              }
            }
          } catch {
            output.warn(`LCOV local illisible : ${localPath}`);
          }
        }
      }
      if (origin === 'analysis') {
        const cached = coverageByPath.get(path);
        if (cached !== undefined) {
          coverageState = cached.state;
          lines = cached.lines;
        } else {
          try {
            const payload = await coverageLines(client, current.analysis.analysis_id, path);
            coverageState = payload.state ?? 'not_measured';
            lines = payload.lines;
            coverageByPath.set(path, { state: coverageState, lines });
          } catch (error) {
            output.warn(`Couverture indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
          }
        }
      }
      const hunks = origin === 'local' ? undefined : await current.diffs.hunks(current.analysis.commit_sha, path);
      coverageDecorations.apply(editor, current.rootPath, lines, hunks, origin, coverageState);
    } else coverageDecorations.clear();

    if (configuration.get<boolean>('duplication.enabled', true)) {
      const name = path.split('/').slice(-1)[0] ?? path;
      const cached = duplicationByPath.get(path);
      let state = 'not_measured';
      let blocks: DuplicateBlock[] = [];
      if (cached !== undefined) {
        state = cached.state;
        blocks = cached.duplications;
      } else {
        try {
          const payload = await duplications(client, current.analysis.analysis_id, name);
          state = payload.state ?? 'not_measured';
          blocks = payload.duplications;
          duplicationByPath.set(path, { state, duplications: blocks });
        } catch (error) {
          output.warn(`Duplications indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
        }
      }
      const hunks = await current.diffs.hunks(current.analysis.commit_sha, path);
      duplicationDecorations.apply(editor, current.rootPath, blocks, hunks, state);
    } else duplicationDecorations.clear();
  };

  const schedule = (): void => {
    if (timer !== undefined) clearInterval(timer);
    const seconds = vscode.workspace.getConfiguration('codeinsights').get<number>('refreshIntervalSeconds', 300);
    if (seconds > 0) timer = setInterval(() => void refresh(), Math.max(60, seconds) * 1000);
  };

  const failTriage = (error: unknown): void => {
    const message = error instanceof ApiError ? triageError(error.status, error.message) : String(error);
    output.warn(`Traitement refusé : ${message}`);
    void vscode.window.showWarningMessage(`CodeInsights : ${message}`);
  };

  /** Collects what the hub requires for this change: assignee, state, reason, expiry. */
  const buildChange = async (client: CodeInsightsApi, finding: DurableFinding, action: string): Promise<Change | undefined> => {
    const change: Change = { reason: '' };
    if (action === 'assign') change.assigneeUserId = await me(client);
    else if (action === 'unassign') change.assigneeUserId = null;
    else if (action === 'assign-other') {
      const members = await people(client, finding.project_key);
      const chosen = await vscode.window.showQuickPick(
        members.map((person) => {
          const item: vscode.QuickPickItem & { id: string } = { label: person.full_name ?? person.email ?? person.user_id, id: person.user_id };
          if (person.email !== undefined) item.description = person.email;
          return item;
        }),
        { title: 'Assigner à', ignoreFocusOut: true },
      );
      if (chosen === undefined) return undefined;
      change.assigneeUserId = chosen.id;
    } else if (action === 'state') {
      const states = availableStates(await isAdmin(client), finding.state);
      if (states.length === 0) {
        void vscode.window.showInformationMessage(
          "CodeInsights : cette issue est sous exception ; seule une revue d'administrateur peut la changer.",
        );
        return undefined;
      }
      const chosen = await vscode.window.showQuickPick(
        states.map((state) => ({ label: stateLabel(state), id: state })),
        { title: 'Nouvel état', ignoreFocusOut: true },
      );
      if (chosen === undefined) return undefined;
      change.state = chosen.id as State;
    }
    if (requiresReason(finding, change)) {
      const reason = await vscode.window.showInputBox({ title: 'Motif de la décision', prompt: 'Requis par le hub', ignoreFocusOut: true });
      if (reason === undefined || reason.trim().length === 0) return undefined;
      change.reason = reason.trim();
    }
    const state = change.state ?? finding.state;
    if (requiresExpiry(state)) {
      const value = await vscode.window.showInputBox({
        title: 'Expiration de l’exception',
        prompt: 'Date ISO 8601 dans l’année à venir, par exemple 2026-12-31',
        ignoreFocusOut: true,
        validateInput: (input) => (validExpiry(input.trim(), Date.now()) ? undefined : 'Date invalide : elle doit être future et à moins d’un an.'),
      });
      if (value === undefined) return undefined;
      change.expiresAt = new Date(value.trim()).toISOString();
    }
    return change;
  };

  let identity: { userId: string; role: string } | undefined;
  const whoami = async (client: CodeInsightsApi): Promise<{ userId: string; role: string }> => {
    if (identity !== undefined) return identity;
    const body = await client.json<{ user_id?: string; id?: string; role?: string; access_role?: string }>('/api/v1/auth/me');
    identity = { userId: body.user_id ?? body.id ?? '', role: body.role ?? body.access_role ?? 'default' };
    return identity;
  };
  const me = async (client: CodeInsightsApi): Promise<string> => (await whoami(client)).userId;
  const isAdmin = async (client: CodeInsightsApi): Promise<boolean> => (await whoami(client)).role === 'admin';

  /**
   * Prompt of an issue: the hub's own, built from the analysed source, or a local one
   * built from the working tree when the hub no longer holds it — labelled as such.
   */
  const promptFor = async (issue: Issue): Promise<{ prompt: string; label: string } | undefined> => {
    if (issue.promptAvailable) {
      try {
        return { prompt: await issuePrompt(api(), issue.id), label: 'source analysée' };
      } catch (error) {
        output.warn(`Prompt du hub indisponible : ${error instanceof ApiError ? error.message : String(error)}`);
      }
    }
    if (issue.path === undefined || current === undefined) {
      void vscode.window.showWarningMessage(`CodeInsights : prompt indisponible, ${promptUnavailable(issue.promptUnavailableReason)}.`);
      return undefined;
    }
    const uri = await locate(current.rootPath, issue.path);
    if (uri === undefined) {
      void vscode.window.showWarningMessage(
        `CodeInsights : prompt indisponible (${promptUnavailable(issue.promptUnavailableReason)}) et ${issue.path} est absent du dépôt local.`,
      );
      return undefined;
    }
    const document = await vscode.workspace.openTextDocument(uri);
    const lines = document.getText().split('\n');
    return { prompt: localPrompt(issue, issue.line, lines, PROMPT_CONTEXT_LINES), label: 'fichier local' };
  };

  /**
   * Runs the local producers on the pending change. The result is an estimate from two
   * workstation tools, not the pipeline verdict, and it says so.
   */
  const analyseLocally = async (interactive: boolean): Promise<void> => {
    const folder = activeFolder();
    if (folder === undefined || current === undefined) {
      if (interactive) void vscode.window.showWarningMessage('CodeInsights : ouvrir un dépôt analysé avant de lancer l’analyse locale.');
      return;
    }
    const configuration = vscode.workspace.getConfiguration('codeinsights', folder.uri);
    // Ce que le dépôt déclare passe devant le réglage du poste : la branche de référence et
    // le jeu de règles sont ceux de la CI, et un poste qui en choisit d'autres estime un gate
    // que la pipeline démentira sans que rien à l'écran ne l'annonce.
    const declared = projectSettings(await readMapping(folder));
    const settings = {
      semgrepConfigs: declared.semgrepConfigs ?? configuration.get<string[]>('liveAnalysis.semgrepConfig', []).filter((entry) => entry.trim().length > 0),
      useDocker: configuration.get<boolean>('liveAnalysis.useDocker', false),
      images: configuration.get<{ semgrep: string; gitleaks: string }>('liveAnalysis.images', {
        semgrep: 'semgrep/semgrep:1.145.0',
        gitleaks: 'zricethezav/gitleaks:v8.30.0',
      }),
      timeoutMs: configuration.get<number>('liveAnalysis.timeoutSeconds', 120) * 1000,
      maxFiles: configuration.get<number>('liveAnalysis.maxFiles', 100),
    };
    const hub = await hubPolicy(current.analysis.project_key);
    const run = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Window, title: 'CodeInsights : analyse locale' },
      (progress) =>
        analyseChanges(
          vscodeWorkspace,
          current!.rootPath,
          declared.targetBranch ?? configuration.get<string>('liveAnalysis.targetBranch', 'origin/master'),
          settings,
          hub,
          (message) => output.warn(message),
          (tool) => progress.report({ message: tool }),
        ),
    );
    if (run === undefined) {
      if (interactive) void vscode.window.showWarningMessage('CodeInsights : diff local indisponible (extension Git absente ou dépôt sans révision de base).');
      return;
    }
    localAnnotations.publish(current.rootPath, run.report.introduced);
    let branchIssues: BranchIssue[] = [];
    try {
      const raw = await findings(api(), current.analysis.analysis_id, '', ISSUE_LIMIT);
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
      output.warn(`Issues de branche indisponibles pour l’estimation : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    const corrected = correctedIssues(
      branchIssues,
      run.report,
      run.changed.map((file) => file.path),
    );
    const estimate = estimateGate(current.analysis.gate_snapshot?.conditions ?? [], run.report, corrected);
    localSection = {
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
    if (lastReportState !== undefined) show(lastReportState);
    const notice = partialNotice(run.report);
    if (notice !== undefined) output.warn(notice);
    output.info(
      `Analyse locale (${run.baseRef}) : ${reportLabel(run.report)} · estimation : ${estimate.label}${run.report.suppressed > 0 ? ` · ${run.report.suppressed} masquée(s) par exception ou règle désactivée` : ''}`,
    );
    if (!interactive) return;
    const message = `CodeInsights : ${reportLabel(run.report)}. Estimation du gate : ${estimate.label}.`;
    if (notice === undefined && estimate.effect !== 'worsens') void vscode.window.showInformationMessage(message);
    else void vscode.window.showWarningMessage(notice === undefined ? message : `${message} ${notice}`);
  };

  /** Project exceptions and disabled rules, so the local run stays as quiet as the CI. */
  const hubPolicy = async (projectKey: string): Promise<{ exceptions: Exception[]; disabledRules: string[] }> => {
    const client = api();
    const result: { exceptions: Exception[]; disabledRules: string[] } = { exceptions: [], disabledRules: [] };
    try {
      result.exceptions = await projectExceptions(client, projectKey, Date.now());
    } catch (error) {
      output.warn(`Exceptions du projet indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    try {
      const rules = await projectRules(client, projectKey, 'status=disabled');
      result.disabledRules = rules
        .filter((rule) => rule.status === 'disabled')
        .map((rule) => `${rule.tool ?? ''}/${rule.rule_id ?? ''}`);
    } catch (error) {
      output.warn(`Règles désactivées indisponibles : ${error instanceof ApiError ? error.message : String(error)}`);
    }
    return result;
  };

  registerParticipant(context, async () => (lastIssue === undefined ? undefined : promptFor(lastIssue)));

  // Le serveur MCP embarqué : déclaré une fois, relancé par l'hôte quand l'URL, les
  // réglages d'analyse locale ou le jeton changent.
  const mcp = registerMcpServer(context, () => tokens.get(), (message) => output.info(message));
  if (mcp !== undefined) context.subscriptions.push(mcp.disposable);

  // Claude Code ne lit pas le fournisseur ci-dessus : `registerMcpServerDefinitionProvider`
  // alimente le registre MCP de l'éditeur, pas celui d'un client en ligne de commande. Le
  // même bundle le sert, à condition de le lui déclarer — c'est ce que fait cette commande.
  const claude = claudeSetup(context, () => tokens.get(), (message) => output.info(message));

  context.subscriptions.push(
    output,
    status,
    new vscode.Disposable(() => {
      if (timer !== undefined) clearInterval(timer);
    }),
    vscode.window.registerTreeDataProvider('codeinsights.branchReport', view),
    vscode.commands.registerCommand('codeinsights.signIn', async () => {
      if (serverUrl().length === 0) {
        await vscode.commands.executeCommand('workbench.action.openSettings', 'codeinsights.serverUrl');
        return;
      }
      const token = await vscode.window.showInputBox({
        title: 'CodeInsights',
        prompt: 'Jeton d’accès personnel CodeInsights, créé depuis « Mon compte » dans le hub',
        password: true,
        ignoreFocusOut: true,
      });
      if (token === undefined || token.trim().length === 0) return;
      await tokens.set(token);
      output.info('Jeton enregistré dans SecretStorage.');
      mcp?.refresh();
      void claude.refresh();
      await refresh();
    }),
    vscode.commands.registerCommand('codeinsights.signOut', async () => {
      await tokens.clear();
      output.info('Jeton supprimé.');
      mcp?.refresh();
      // Un jeton révoqué qui resterait déclaré ferait échouer chaque outil de Claude sans
      // que l'agent puisse dire pourquoi : la déconnexion retire aussi l'entrée.
      void claude.refresh();
      await refresh();
    }),
    vscode.commands.registerCommand('codeinsights.refresh', refresh),
    vscode.commands.registerCommand('codeinsights.openInHub', async () => {
      if (serverUrl().length === 0) {
        await vscode.commands.executeCommand('workbench.action.openSettings', 'codeinsights.serverUrl');
        return;
      }
      const folder = activeFolder();
      const git = folder === undefined ? undefined : await gitContext(folder);
      const target: Parameters<typeof hubUrl>[0] = { serverUrl: serverUrl() };
      const resolution =
        folder === undefined ? undefined : await resolveProjectFor(folder, git, api());
      if (resolution?.state === 'resolved') target.project = resolution.projectKey;
      // The hub indexes branches by the ref the CI reported; the loaded analysis carries
      // that exact spelling, where Git only gives the short name.
      const resolved = resolution?.state === 'resolved' ? resolution.projectKey : undefined;
      const analysed =
        current !== undefined && resolved !== undefined && current.analysis.project_key === resolved
          ? hubBranchKey(current.analysis)
          : undefined;
      const branch = analysed ?? git?.branch;
      if (branch !== undefined) target.branch = branch;
      await vscode.env.openExternal(vscode.Uri.parse(hubUrl(target)));
    }),
    issuesTree,
    rulesTree,
    annotations,
    coverageDecorations,
    duplicationDecorations,
    qualityLenses,
    dependencyAnnotations,
    vscode.window.registerWebviewViewProvider('codeinsights.details', detailsView),
    issuesTree.onDidChangeSelection((event) => {
      const node = event.selection[0];
      const issue = node !== undefined && 'issue' in node ? node.issue : undefined;
      if (issue !== undefined) lastIssue = issue;
      detailsView.show(issue, issue === undefined ? undefined : annotations.anchorState(issue), current?.analysis.commit_sha);
    }),
    vscode.commands.registerCommand('codeinsights.openLocalFinding', async (target: { path: string; line: number }) => {
      if (current === undefined) return;
      const uri = await locate(current.rootPath, target.path);
      if (uri === undefined) return;
      await reveal(uri, target.line);
    }),
    vscode.commands.registerCommand('codeinsights.openLocation', async (location: IssueLocation) => {
      if (current === undefined || location.path === undefined) return;
      const uri = await locate(current.rootPath, location.path);
      if (uri === undefined) {
        void vscode.window.showWarningMessage(`CodeInsights : ${location.path} est introuvable dans le dépôt local.`);
        return;
      }
      await reveal(uri, location.line);
    }),
    vscode.languages.registerCodeLensProvider({ scheme: 'file' }, annotations),
    vscode.languages.registerCodeLensProvider({ scheme: 'file' }, qualityLenses),
    vscode.languages.registerCodeLensProvider({ scheme: 'file' }, dependencyAnnotations),
    vscode.languages.registerHoverProvider({ scheme: 'file' }, annotations),
    vscode.languages.registerHoverProvider({ scheme: 'file' }, dependencyAnnotations),
    vscode.workspace.onDidOpenTextDocument((document) => {
      void annotations.resolve(document.uri);
      const editor = vscode.window.activeTextEditor;
      if (editor?.document.uri.toString() === document.uri.toString()) void decorateEditor(editor);
    }),
    vscode.commands.registerCommand('codeinsights.focusIssues', async () => {
      await vscode.commands.executeCommand('codeinsights.issues.focus');
    }),
    vscode.commands.registerCommand('codeinsights.filterIssues', async () => {
      const severity = await pick<SeverityFilter>('Criticité', ['all', 'blocker', 'high', 'medium', 'low', 'info'], filters.severity);
      if (severity === undefined) return;
      const category = await pick<CategoryFilter>(
        'Catégorie',
        ['all', 'bug', 'code_smell', 'vulnerability', 'security_hotspot', 'secret', 'dependency', 'code', 'ioc'],
        filters.category,
      );
      if (category === undefined) return;
      const state = await pick<StateFilter>('État', ['all', 'open', 'confirmed', 'resolved', 'false_positive', 'accepted_risk'], filters.state);
      if (state === undefined) return;
      const scope = await pick<'all' | 'mine'>('Périmètre', ['all', 'mine'], filters.mine ? 'mine' : 'all');
      if (scope === undefined) return;
      filters = { severity, category, state, mine: scope === 'mine' };
      if (current !== undefined) await loadIssues(api(), current.analysis, false);
    }),
    vscode.commands.registerCommand('codeinsights.clearIssueFilters', async () => {
      filters = { ...defaultIssueFilters };
      if (current !== undefined) await loadIssues(api(), current.analysis, false);
    }),
    vscode.commands.registerCommand('codeinsights.openIssue', async (issue: Issue) => {
      if (issue.path === undefined) {
        void vscode.window.showWarningMessage("CodeInsights : cette issue n'a pas de localisation dans le dépôt.");
        return;
      }
      if (current === undefined) return;
      const uri = await locate(current.rootPath, issue.path);
      if (uri === undefined) {
        // The analysed revision is not the working tree: the file may be renamed or removed.
        void vscode.window.showWarningMessage(
          `CodeInsights : ${issue.path} est introuvable dans le dépôt local (analyse du commit ${current.analysis.commit_sha.slice(0, 8)}).`,
        );
        return;
      }
      await reveal(uri, issue.line);
    }),
    vscode.commands.registerCommand('codeinsights.openIssueInHub', async (node: { issue?: Issue } | Issue | undefined) => {
      const issue = selected(node);
      if (issue === undefined || current === undefined || serverUrl().length === 0) return;
      const target: Parameters<typeof hubUrl>[0] = {
        serverUrl: serverUrl(),
        tab: 'issues',
        project: current.analysis.project_key,
        branch: hubBranchKey(current.analysis),
        analysisId: current.analysis.analysis_id,
        severity: issue.severity,
      };
      if (issue.path !== undefined) target.file = issue.path;
      await vscode.env.openExternal(vscode.Uri.parse(hubUrl(target)));
    }),
    vscode.commands.registerCommand('codeinsights.openRuleDefinition', async (node: { issue?: Issue } | Issue | undefined) => {
      // Only the definition the producer declared is opened; the extension never invents one.
      const issue = selected(node);
      if (issue?.documentationUrl === undefined) {
        void vscode.window.showInformationMessage("CodeInsights : le producteur n'a pas déclaré de définition pour cette règle.");
        return;
      }
      await vscode.env.openExternal(vscode.Uri.parse(issue.documentationUrl));
    }),
    vscode.commands.registerCommand('codeinsights.triageIssue', async (node: { issue?: Issue } | Issue | undefined) => {
      const issue = selected(node) ?? lastIssue;
      if (issue === undefined || current === undefined) return;
      lastIssue = issue;
      const client = api();
      let finding: DurableFinding;
      try {
        finding = await durableFinding(client, issue.id);
      } catch (error) {
        return failTriage(error);
      }
      const action = await vscode.window.showQuickPick(
        [
          { label: "M'assigner cette issue", id: 'assign' },
          { label: 'Retirer l’assignation', id: 'unassign' },
          { label: 'Assigner à…', id: 'assign-other' },
          { label: `Changer l’état (actuel : ${stateLabel(finding.effective_state ?? finding.state)})`, id: 'state' },
          { label: 'Commenter', id: 'comment' },
        ],
        { title: `CodeInsights — ${issue.rule}`, ignoreFocusOut: true },
      );
      if (action === undefined) return;
      try {
        if (action.id === 'comment') {
          const reason = await vscode.window.showInputBox({ title: 'Commentaire', prompt: 'Commentaire visible dans le hub', ignoreFocusOut: true });
          if (reason === undefined || reason.trim().length === 0) return;
          await commentFinding(client, finding.id, finding.version, reason.trim());
          void vscode.window.showInformationMessage('CodeInsights : commentaire enregistré.');
          return;
        }
        const change = await buildChange(client, finding, action.id);
        if (change === undefined) return;
        const updated = await updateFinding(client, finding.id, updateBody(finding, change));
        void vscode.window.showInformationMessage(
          `CodeInsights : issue mise à jour (${stateLabel(updated.effective_state ?? updated.state)}).`,
        );
        await refresh();
      } catch (error) {
        failTriage(error);
      }
    }),
    vscode.commands.registerCommand('codeinsights.copyPrompt', async (node: { issue?: Issue } | Issue | undefined) => {
      const issue = selected(node) ?? lastIssue;
      if (issue === undefined) return;
      lastIssue = issue;
      const built = await promptFor(issue);
      if (built === undefined) return;
      await vscode.env.clipboard.writeText(built.prompt);
      void vscode.window.showInformationMessage(`CodeInsights : prompt copié (${built.label}).`);
    }),
    vscode.commands.registerCommand('codeinsights.explainIssue', async (node: { issue?: Issue } | Issue | undefined) => {
      const issue = selected(node) ?? lastIssue;
      if (issue === undefined) return;
      lastIssue = issue;
      const built = await promptFor(issue);
      if (built === undefined) return;
      try {
        await vscode.commands.executeCommand('workbench.action.chat.open', { query: '@codeinsights ' });
      } catch {
        // No chat surface in this host: the prompt is still usable from the clipboard.
        await vscode.env.clipboard.writeText(built.prompt);
        void vscode.window.showInformationMessage('CodeInsights : aucun chat disponible, prompt copié dans le presse-papier.');
      }
    }),
    vscode.commands.registerCommand('codeinsights.analyzeChanges', () => analyseLocally(true)),
    vscode.commands.registerCommand('codeinsights.qualityInfo', () => undefined),
    vscode.commands.registerCommand('codeinsights.filterRules', async () => {
      const search = await vscode.window.showInputBox({
        title: 'CodeInsights — Recherche de règle',
        value: ruleFilters.search,
        prompt: 'Identifiant, outil ou titre. Vide : pas de filtre texte.',
        ignoreFocusOut: true,
      });
      if (search === undefined) return;
      const status = await pick('État', ['all', 'enabled', 'disabled', 'unmanaged'], ruleFilters.status);
      if (status === undefined) return;
      const severity = await pick('Criticité', ['all', 'blocker', 'high', 'medium', 'low', 'info'], ruleFilters.severity);
      if (severity === undefined) return;
      ruleFilters = { ...ruleFilters, search, status, severity };
      applyRuleFilters();
    }),
    vscode.commands.registerCommand('codeinsights.clearRuleFilters', () => {
      ruleFilters = { ...defaultRuleFilters };
      applyRuleFilters();
    }),
    vscode.commands.registerCommand('codeinsights.showRule', async (node: { issue?: Issue } | Issue | undefined) => {
      const issue = selected(node) ?? lastIssue;
      if (issue === undefined) return;
      ruleFilters = { ...defaultRuleFilters, search: issue.rule };
      applyRuleFilters();
      await vscode.commands.executeCommand('codeinsights.rules.focus');
    }),
    vscode.commands.registerCommand('codeinsights.openRuleInHub', async (node: { rule?: Rule } | Rule | undefined) => {
      const rule = selectedRule(node);
      if (rule === undefined || current === undefined || serverUrl().length === 0) return;
      await vscode.env.openExternal(
        vscode.Uri.parse(
          hubUrl({
            serverUrl: serverUrl(),
            tab: 'rules',
            project: current.analysis.project_key,
            selectedRule: `${rule.tool}/${rule.ruleId}`,
            ruleSearch: rule.ruleId,
            ruleStatus: rule.status,
          }),
        ),
      );
    }),
    vscode.commands.registerCommand('codeinsights.configureMcpClient', () => claude.configure()),
    vscode.commands.registerCommand('codeinsights.installPrePushHook', async () => {
      const folder = activeFolder();
      if (folder === undefined) return;
      const git = await gitContext(folder);
      if (git === undefined) {
        void vscode.window.showWarningMessage('CodeInsights : ouvrir un dépôt Git pour proposer le crochet pre-push.');
        return;
      }
      const confirmed = await vscode.window.showWarningMessage(
        "CodeInsights peut écrire un crochet Git de rappel. Il n'empêche pas le push et n'est jamais installé tout seul.",
        { modal: true },
        'Écrire le rappel',
      );
      if (confirmed === undefined) return;
      const hookPath = `${git.rootPath.replace(/\\/g, '/')}/.git/hooks/pre-push`;
      try {
        const existing = await readFile(hookPath, 'utf8');
        if (alreadyInstalled(existing)) {
          void vscode.window.showInformationMessage('CodeInsights : le rappel pre-push est déjà en place.');
          return;
        }
        void vscode.window.showWarningMessage('CodeInsights : un crochet pre-push existe déjà. Il n’a pas été modifié.');
        return;
      } catch {
        // No hook yet: writing one is the whole point of the command.
      }
      await writeFile(hookPath, prePushHook('CodeInsights : analyser mes changements'), { encoding: 'utf8', mode: 0o755 });
      await chmod(hookPath, 0o755);
      void vscode.window.showInformationMessage('CodeInsights : rappel pre-push écrit. Il n’empêche pas le push.');
    }),
    localAnnotations,
    vscode.workspace.onDidSaveTextDocument((document) => {
      void annotations.resolve(document.uri);
      if (vscode.workspace.getConfiguration('codeinsights').get<boolean>('liveAnalysis.enabled', false)) void analyseLocally(false);
    }),
    vscode.window.onDidChangeActiveTextEditor((editor) => {
      const folder = activeFolder(editor);
      if (folder !== undefined && lastFolder !== undefined && folder.uri.fsPath !== lastFolder.uri.fsPath) {
        lastFolder = folder;
        // Les réglages passés au serveur MCP sont ceux de ce dossier : changer de dépôt
        // dans un multi-root change la branche cible et les règles, pas seulement le rapport.
        mcp?.refresh();
        void refresh();
        return;
      }
      void decorateEditor(editor);
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (!event.affectsConfiguration('codeinsights')) return;
      if (event.affectsConfiguration('codeinsights.serverUrl') || event.affectsConfiguration('codeinsights.liveAnalysis')) {
        mcp?.refresh();
        void claude.refresh();
      }
      schedule();
      void refresh();
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      mcp?.refresh();
      void refresh();
    }),
  );

  schedule();
  // Une mise à jour de l'extension déplace le bundle : la configuration écrite pour Claude
  // désignerait le répertoire de la version précédente. Sans opt-in préalable, ne fait rien.
  // La proposition vient après, pour ne pas proposer ce que la remise à niveau vient d'écrire.
  void claude
    .upgrade()
    .then(() => claude.prompt())
    .catch((error: unknown) => output.warn(`Claude Code : ${error instanceof Error ? error.message : String(error)}`));
  await refresh();
}

export function deactivate(): void {
  // The disposables registered on the extension context cover every resource.
}

/** Sequential quick picks keep the filter dialogue native, without a custom webview. */
async function pick<T extends string>(title: string, values: readonly T[], active: T): Promise<T | undefined> {
  const items = values.map((value) => (value === active ? { label: value, description: 'actuel' } : { label: value }));
  const chosen = await vscode.window.showQuickPick(items, { title: `CodeInsights — ${title}`, ignoreFocusOut: true });
  return chosen === undefined ? undefined : (chosen.label as T);
}

/** A command is invoked with the tree node from a menu, or the issue itself from code. */
function selected(node: { issue?: Issue } | Issue | undefined): Issue | undefined {
  if (node === undefined) return undefined;
  return 'issue' in node ? node.issue : (node as Issue);
}

function selectedRule(node: { rule?: Rule } | Rule | undefined): Rule | undefined {
  if (node === undefined) return undefined;
  return 'ruleId' in node ? node : node.rule;
}

function serverUrl(): string {
  return vscode.workspace.getConfiguration('codeinsights').get<string>('serverUrl', '').trim();
}

function contextLine(branch: string, git: GitContext | undefined): { branch: string; headSha?: string } {
  return git?.headSha === undefined ? { branch } : { branch, headSha: git.headSha };
}

async function resolveProjectFor(
  folder: vscode.WorkspaceFolder,
  git: GitContext | undefined,
  client: CodeInsightsApi,
): Promise<Resolution> {
  const sources: Parameters<typeof resolveProject>[0] = {
    setting: vscode.workspace.getConfiguration('codeinsights', folder.uri).get<string>('projectKey', ''),
    folderName: folder.name,
  };
  const mapping = await readMapping(folder);
  if (mapping !== undefined) sources.mapping = mapping;
  if (git?.repositoryUrl !== undefined) sources.repositoryUrl = git.repositoryUrl;
  return resolveProject(sources, (repositoryUrl) => projectsForRepository(client, repositoryUrl));
}

/** Optional per-repository mapping, required by monorepos hosting several projects. */
async function readMapping(folder: vscode.WorkspaceFolder): Promise<ProjectFile | undefined> {
  const uri = vscode.Uri.joinPath(folder.uri, '.codeinsights', 'vscode.json');
  try {
    const bytes = await vscode.workspace.fs.readFile(uri);
    const parsed: unknown = JSON.parse(new TextDecoder().decode(bytes));
    return parsed !== null && typeof parsed === 'object' ? (parsed as ProjectFile) : undefined;
  } catch {
    return undefined;
  }
}

export type { BranchReport } from '../../core/src/report/branch-report';
