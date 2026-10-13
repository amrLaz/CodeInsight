import { API_BASE, APP_VERSION } from '../config/env';
import { TABS } from '../config/navigation';
import { SignInScreen, SignOutScreen } from './AuthScreens';
import { useWorkspaceData } from '../workspace/useWorkspaceData';
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch as request, apiJson } from '../services/api';
import AppShell from '../layouts/AppShell';
import DialogCloseButton from '../components/common/DialogCloseButton';
import { Button } from '../components/ui';
import { useToast } from '../context/ToastProvider';
import { defaultIssueFilters, parseWorkspaceUrl, workspaceSearch, type IssueFilters, type ProjectMetric, type WorkspaceUrlState } from '../workspace/navigation-state';
import { compareFavoriteProjects } from '../utils/project-favorites';
import { analysisSelectionKey, pullRequestIdOf, pullRequestKey, selectAvailableBranch } from '../utils/branch-selection';
import { ENTRA_AUTO_ATTEMPT_KEY, ENTRA_AUTO_SUPPRESSED_KEY, shouldAttemptSilentEntra } from '../entra-auth';

const Projects = lazy(() => import('../features/projects/Projects'));
const Issues = lazy(() => import('../features/issues/Issues'));
const Rules = lazy(() => import('../features/rules/Rules'));
const QualityGates = lazy(() => import('../features/quality-gates/QualityGates'));
const CodeQualityDetails = lazy(() => import('../features/projects/components/CodeQualityDetails'));
const NewCode = lazy(() => import('../features/projects/components/NewCodePanel').then((module) => ({ default: module.NewCode })));
const AnalysisProfiles = lazy(() => import('../features/projects/components/AnalysisProfiles'));
const AccountTokens = lazy(() => import('../features/account/AccountTokens'));
const Administration = lazy(() => import('../features/administration/Administration'));

const LOCAL_STORAGE_GATE_LIBRARY_KEY = 'sast-quality-gates-v1';
const LOCAL_STORAGE_SESSION_KEY = 'sast-platform-session-v1';

import type { ScopeMode, AppTab, AdminSection, ProjectMeta, LocalUserRole, SessionUser, ProjectSummary, IssueSeverityFilter, IssueTypeFilter, BranchSummary, PullRequestSummary } from '../types/api';

type SessionProps = { initialUser: SessionUser | null; onSessionChange: (user: SessionUser | null) => void };

export default function Workspace({ initialUser, onSessionChange }: SessionProps) {
  const hasEntraCallback = useRef(
    ['entra_session', 'entra_error', 'entra_silent'].some((key) => {
      const query = new URLSearchParams(window.location.search);
      const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ''));
      return query.has(key) || fragment.has(key);
    }),
  ).current;
  const initialUrlState = useRef(parseWorkspaceUrl(window.location.search)).current;
  const initialUrlHasTab = useRef(new URLSearchParams(window.location.search).has('tab')).current;
  const [activeTab, setActiveTab] = useState<AppTab>(initialUrlState.tab);
  const [activeAdminSection, setActiveAdminSection] = useState<AdminSection>('general-settings');
  const [scopeMode, setScopeMode] = useState<ScopeMode>(initialUrlState.scope);
  const independentScope = activeTab === 'administration';
  const [selectedProject, setSelectedProject] = useState(initialUrlState.project);
  const [selectedBranch, setSelectedBranch] = useState(initialUrlState.branch);
  const [selectedAnalysisId, setSelectedAnalysisId] = useState(initialUrlState.analysis);
  const [projectMetric, setProjectMetric] = useState<ProjectMetric>(initialUrlState.projectMetric);
  const [issueFilters, setIssueFilters] = useState<IssueFilters>(initialUrlState.issues);
  const [ruleFilters, setRuleFilters] = useState(initialUrlState.rules);
  const [selectedGateId, setSelectedGateId] = useState(initialUrlState.gate);
  const [selectedRuleId, setSelectedRuleId] = useState(initialUrlState.selectedRule);
  const [actionError, setError] = useState<string | null>(null);
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [showAccount, setShowAccount] = useState(false);
  const [profilesActivated, setProfilesActivated] = useState(false);
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);
  const notify = useToast();
  const deleteDialogRef = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = showDeleteModal ? deleteDialogRef.current : null;
    if (!dialog) return;
    const previousFocus = document.activeElement;
    // ponytail: native modal dialogs keep the background inert without a focus-trap dependency.
    dialog.showModal();
    return () => {
      dialog.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [showDeleteModal]);

  const [adminForm, setAdminForm] = useState({
    project_key: '',
    repoProvider: 'github' as 'github' | 'azure-devops' | 'other',
    repoUrl: '',
    pipelineProvider: 'azure-devops' as 'azure-devops' | 'github-actions' | 'other',
    pipelineUrl: '',
  });

  const sessionUser = initialUser;
  const [authScreen, setAuthScreen] = useState<'sign-in' | 'app' | 'sign-out'>(initialUser ? 'app' : 'sign-in');
  useEffect(() => {
    const pageName =
      authScreen === 'app' ? (activeTab === 'projects' && projectMetric ? 'Code quality details' : TABS.find((tab) => tab.id === activeTab)?.label) : authScreen === 'sign-in' ? 'Sign In' : 'Sign Out';
    document.title = pageName ? `CodeInsights - ${pageName}` : 'CodeInsights';
  }, [activeTab, authScreen, projectMetric]);

  const [signInForm, setSignInForm] = useState({
    email: '',
    password: '',
    remember_me: true,
  });
  const [signInError, setSignInError] = useState<string | null>(null);
  const signInEmailRef = useRef<HTMLInputElement | null>(null);
  const signInPasswordRef = useRef<HTMLInputElement | null>(null);

  const releaseName = APP_VERSION;

  const queryClient = useQueryClient();
  const signedIn = !!sessionUser?.token;
  const [linkedAnalysisId, setLinkedAnalysisId] = useState(initialUrlState.analysis);
  const linkedAnalysis = useQuery({
    queryKey: ['analysis-link', linkedAnalysisId],
    enabled: signedIn && !!linkedAnalysisId,
    queryFn: ({ signal }) => apiJson<import('../types/api').AnalysisRecord>(`${API_BASE}/v1/analyses/${encodeURIComponent(linkedAnalysisId)}`, sessionUser?.token, { signal }),
  });
  const linkedSelectionApplied = useRef(false);
  useEffect(() => {
    if (!linkedAnalysis.data || linkedSelectionApplied.current) return;
    linkedSelectionApplied.current = true;
    setSelectedProject(linkedAnalysis.data.project_key);
    setSelectedBranch(analysisSelectionKey(linkedAnalysis.data));
    setSelectedAnalysisId(linkedAnalysis.data.analysis_id);
    if (!initialUrlHasTab && !initialUrlState.projectMetric) setActiveTab('issues');
  }, [linkedAnalysis.data]);
  const [branchPage, setBranchPage] = useState({ project: '', cursor: '' });
  const branchCursor = branchPage.project === selectedProject ? branchPage.cursor : '';
  const [branchSearch, setBranchSearch] = useState('');
  const [branchSearchTerm, setBranchSearchTerm] = useState('');
  // As for the Entra search, the server is asked once typing pauses for 300 ms.
  useEffect(() => {
    const term = branchSearch.trim();
    if (!term) {
      setBranchSearchTerm('');
      return;
    }
    const timeout = window.setTimeout(() => setBranchSearchTerm(term), 300);
    return () => window.clearTimeout(timeout);
  }, [branchSearch]);
  const [projectCursor, setProjectCursor] = useState('');
  const [historyPage, setHistoryPage] = useState({ project: '', branch: '', cursor: '' });
  const historyCursor = historyPage.project === selectedProject && historyPage.branch === selectedBranch ? historyPage.cursor : '';
  const {
    meQuery,
    methodsQuery,
    photoQuery,
    analysesQuery,
    summaryQuery,
    selectedSummaryQuery,
    branchesQuery,
    selectedBranchQuery,
    preferredBranchesQuery,
    branchSearchQuery,
    pullRequestsQuery,
    selectedPullRequestQuery,
    integrationsQuery,
    gatesQuery,
    assignmentsQuery,
    devOpsQuery,
  } =
    useWorkspaceData(sessionUser?.token, activeTab, activeAdminSection, selectedProject, selectedBranch, historyCursor, projectCursor, branchCursor, branchSearchTerm);
  const [photoUrl, setPhotoUrl] = useState<string | null>(null);
  const [photoFailed, setPhotoFailed] = useState(false);
  useEffect(() => {
    if (!photoQuery.data) {
      setPhotoUrl(null);
      return;
    }
    const url = URL.createObjectURL(photoQuery.data);
    setPhotoFailed(false);
    setPhotoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [photoQuery.data]);
  const canAccessAdministration = !meQuery.isError && meQuery.data?.permissions?.is_admin === true;
  const apiFetch = (url: string, init?: RequestInit) => request(url, sessionUser?.token, init);

  const analyses = useMemo(() => {
    const page = analysesQuery.data?.items ?? [];
    const linked = linkedAnalysis.data;
    return linked && linked.project_key === selectedProject && analysisSelectionKey(linked) === selectedBranch && !historyCursor && !page.some((item) => item.analysis_id === linked.analysis_id)
      ? [linked, ...page]
      : page;
  }, [analysesQuery.data, linkedAnalysis.data, selectedProject, selectedBranch, historyCursor]);
  const projectMeta = useMemo<Record<string, ProjectMeta>>(
    () =>
      Object.fromEntries(
        (integrationsQuery.data ?? []).map((item) => [
          item.project_key,
          {
            repoProvider: item.repo_provider,
            repoUrl: item.repo_url,
            pipelineProvider: item.pipeline_provider,
            pipelineUrl: item.pipeline_url,
            updatedAt: item.updated_at,
          },
        ]),
      ),
    [integrationsQuery.data],
  );
  const gateLibrary = useMemo(
    () =>
      (gatesQuery.data ?? []).map((g) => ({
        ...g,
        projectCount: g.project_count ?? 0,
        builtIn: g.is_default ?? false,
      })),
    [gatesQuery.data],
  );
  const gateAssignments = assignmentsQuery.data ?? {};
  const azureDevOpsConfigs = devOpsQuery.data ?? [];
  const loading = summaryQuery.isFetching || analysesQuery.isFetching || integrationsQuery.isFetching;
  const dashboardError = linkedAnalysis.error ?? summaryQuery.error ?? analysesQuery.error ?? integrationsQuery.error;
  const error = actionError ?? meQuery.error?.message ?? dashboardError?.message;
  const adminReadError = devOpsQuery.error?.message;
  const loadGates = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['gates'] }), queryClient.invalidateQueries({ queryKey: ['gate-assignments'] })]);
  const loadDashboard = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['analyses'] }),
      queryClient.invalidateQueries({ queryKey: ['branches'] }),
      queryClient.invalidateQueries({ queryKey: ['pull-requests'] }),
      queryClient.invalidateQueries({ queryKey: ['gates'] }),
      queryClient.invalidateQueries({ queryKey: ['integrations'] }),
      queryClient.invalidateQueries({ queryKey: ['projects'] }),
    ]);
  const favoriteMutation = useMutation({
    mutationFn: ({ projectKey, isFavorite }: { projectKey: string; isFavorite: boolean }) =>
      apiFetch(`${API_BASE}/v1/auth/me/favorite-projects/${encodeURIComponent(projectKey)}`, {
        method: isFavorite ? 'DELETE' : 'PUT',
      }),
    onSuccess: async () => {
      setProjectCursor('');
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['analyses', 'summary'] }), queryClient.invalidateQueries({ queryKey: ['analyses', 'summary-project'] })]);
    },
  });
  const visibleTabs = useMemo(() => {
    return canAccessAdministration ? TABS : TABS.filter((tab) => tab.id !== 'administration');
  }, [canAccessAdministration]);

  // Branches first, then pull requests under their `pr:<id>` selection key.
  const selectedProjectBranches = useMemo<BranchSummary[]>(() => {
    const pullRequests = new Map<string, PullRequestSummary>([...(pullRequestsQuery.data?.items ?? []), ...(selectedPullRequestQuery.data?.items ?? [])].map((item) => [item.pr_id, item]));
    return [
      ...new Map(
        [...(branchesQuery.data?.items ?? []), ...(selectedBranchQuery.data?.items ?? []), ...(preferredBranchesQuery.data ?? []), ...(branchSearchQuery.data?.items ?? [])].map((branch) => [branch.name, branch]),
      ).values(),
      ...[...pullRequests.values()].map((item) => ({
        name: pullRequestKey(item.pr_id),
        count: item.count,
        latest: item.latest,
        pull_request: { id: item.pr_id, source_branch: item.source_branch, target_branch: item.target_branch },
      })),
    ];
  }, [branchesQuery.data, selectedBranchQuery.data, preferredBranchesQuery.data, branchSearchQuery.data, pullRequestsQuery.data, selectedPullRequestQuery.data]);
  const summaries = useMemo<ProjectSummary[]>(
    () =>
      [...new Map([...(summaryQuery.data?.projects ?? []), ...(selectedSummaryQuery.data?.projects ?? [])].map((item) => [item.project_key, item])).values()]
        .filter((item) => item.latest)
        .map((item) => ({
          projectKey: item.project_key,
          isFavorite: item.is_favorite,
          latest: item.latest,
          history: item.project_key === selectedProject ? analyses : [item.latest],
          passCount: item.pass_count,
          failCount: item.fail_count,
          unknownCount: item.unknown_count,
          issueCounts: item.issue_counts ?? {},
          totalCount: item.total_count,
          branches: item.project_key === selectedProject ? selectedProjectBranches : item.branches,
        }))
        .sort(compareFavoriteProjects),
    [summaryQuery.data, selectedSummaryQuery.data, selectedProjectBranches, analyses, selectedProject],
  );
  const projectPickerOptions = useMemo(() => summaries.map((summary) => ({ key: summary.projectKey })), [summaries]);

  const selectedSummary = useMemo(() => {
    return summaries.find((summary) => summary.projectKey === selectedProject) ?? null;
  }, [selectedProject, summaries]);

  useEffect(() => {
    if (selectedProject && summaryQuery.isSuccess && (selectedSummaryQuery.isError || (selectedSummaryQuery.isSuccess && !selectedSummary))) {
      setSelectedProject('');
      setSelectedBranch('');
      setSelectedAnalysisId('');
      setProjectMetric('');
    }
  }, [selectedProject, selectedSummary, summaryQuery.isSuccess, selectedSummaryQuery.isSuccess, selectedSummaryQuery.isError]);

  useEffect(() => {
    const selectedQuery = pullRequestIdOf(selectedBranch) ? selectedPullRequestQuery : selectedBranchQuery;
    // A paged catalogue first learns whether main or master exist beyond the loaded page.
    if (!selectedSummary || !branchesQuery.isSuccess || !pullRequestsQuery.isSuccess || preferredBranchesQuery.isLoading || (selectedBranch && !selectedQuery.isSuccess)) return;
    const nextBranch = selectAvailableBranch(
      (selectedSummary.branches ?? []).map((branch) => branch.name),
      selectedBranch,
      analysisSelectionKey(selectedSummary.latest),
    );
    if (nextBranch === selectedBranch) return;
    setSelectedBranch(nextBranch);
    setSelectedAnalysisId('');
    setProjectMetric('');
  }, [selectedBranch, selectedSummary, branchesQuery.isSuccess, selectedBranchQuery.isSuccess, preferredBranchesQuery.isLoading, pullRequestsQuery.isSuccess, selectedPullRequestQuery.isSuccess]);

  // Handle the Entra OIDC callback before the automatic silent attempt can run.
  useEffect(() => {
    window.localStorage.removeItem(LOCAL_STORAGE_GATE_LIBRARY_KEY);
    const queryParams = new URLSearchParams(window.location.search);
    const fragmentParams = new URLSearchParams(window.location.hash.replace(/^#/, ''));
    // The server hands the session off through a short-lived httpOnly cookie
    // instead of a URL fragment: a page that self-authenticates from its own
    // URL reads to automated scanners as a credential-exposure page, and
    // Safe Browsing flagged this origin for exactly that shape.
    const entraSession = queryParams.get('entra_session');
    const entraError = fragmentParams.get('entra_error') ?? queryParams.get('entra_error');
    const entraSilent = fragmentParams.get('entra_silent') ?? queryParams.get('entra_silent');
    queryParams.delete('entra_session');
    queryParams.delete('entra_error');
    queryParams.delete('entra_silent');
    window.history.replaceState({}, '', window.location.pathname + (queryParams.size ? `?${queryParams}` : ''));

    if (entraSilent === 'unavailable') return;
    if (entraError) {
      setSignInError(`Azure AD sign-in failed: ${entraError}`);
      return;
    }

    if (entraSession === 'ready') {
      void (async () => {
        try {
          const response = await fetch('/api/v1/auth/entra/session', { credentials: 'include' });
          if (!response.ok) throw new Error('Entra session handoff failed');
          const decoded = (await response.json()) as {
            full_name: string;
            email: string;
            access_role: 'admin' | 'regular' | 'default';
            token?: string;
            permissions?: SessionUser['permissions'];
          };
          const nextSession: SessionUser = {
            full_name: decoded.full_name ?? 'Entra User',
            email: decoded.email ?? '',
            access_role: decoded.access_role ?? 'default',
            token: decoded.token,
            permissions: decoded.permissions,
          };
          onSessionChange(nextSession);
          window.localStorage.setItem(LOCAL_STORAGE_SESSION_KEY, JSON.stringify(nextSession));
          setAuthScreen('app');
        } catch {
          setSignInError('Failed to read Azure AD session. Please try again.');
        }
      })();
      return;
    }

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const workspaceUrlState = useMemo<WorkspaceUrlState>(
    () => ({
      tab: activeTab,
      scope: scopeMode,
      project: selectedProject,
      branch: selectedBranch,
      analysis: selectedAnalysisId,
      projectMetric,
      gate: selectedGateId,
      selectedRule: selectedRuleId,
      issues: issueFilters,
      rules: ruleFilters,
    }),
    [activeTab, scopeMode, selectedProject, selectedBranch, selectedAnalysisId, projectMetric, selectedGateId, selectedRuleId, issueFilters, ruleFilters],
  );
  const skipNextUrlReplace = useRef(false);
  const pushWorkspaceState = (next: WorkspaceUrlState) => {
    skipNextUrlReplace.current = true;
    window.history.pushState({}, '', `${window.location.pathname}${workspaceSearch(next)}`);
  };

  useEffect(() => {
    if (skipNextUrlReplace.current) {
      skipNextUrlReplace.current = false;
      return;
    }
    window.history.replaceState({}, '', `${window.location.pathname}${workspaceSearch(workspaceUrlState)}`);
  }, [workspaceUrlState]);

  useEffect(() => {
    const restoreWorkspace = () => {
      const restored = parseWorkspaceUrl(window.location.search);
      setActiveTab(restored.tab);
      setScopeMode(restored.scope);
      setSelectedProject(restored.project);
      setSelectedBranch(restored.branch);
      setSelectedAnalysisId(restored.analysis);
      setProjectMetric(restored.projectMetric);
      setLinkedAnalysisId(restored.analysis);
      setSelectedGateId(restored.gate);
      setSelectedRuleId(restored.selectedRule);
      setIssueFilters(restored.issues);
      setRuleFilters(restored.rules);
    };
    window.addEventListener('popstate', restoreWorkspace);
    return () => window.removeEventListener('popstate', restoreWorkspace);
  }, []);

  // Filter analyses by selected project and branch
  const selectedProjectAnalyses = useMemo(() => {
    if (!selectedSummary || !selectedBranch) return [];
    return selectedSummary.history.filter((analysis) => analysisSelectionKey(analysis) === selectedBranch);
  }, [selectedSummary, selectedBranch]);

  useEffect(() => {
    if (linkedAnalysisId && !linkedAnalysis.isError && !linkedSelectionApplied.current) return;
    if (!selectedProjectAnalyses.some((analysis) => analysis.analysis_id === selectedAnalysisId)) {
      setSelectedAnalysisId(selectedProjectAnalyses[0]?.analysis_id ?? '');
    }
  }, [selectedAnalysisId, selectedProjectAnalyses]);

  const normalizeErrorMessage = (err: unknown, fallback = 'Unknown error') => {
    if (err instanceof Error && err.message) {
      return err.message;
    }
    if (typeof err === 'string' && err.trim()) {
      return err;
    }
    return fallback;
  };

  const downloadExport = async (url: string, filename: string) => {
    try {
      const response = await apiFetch(url);
      const objectUrl = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = objectUrl;
      link.download = filename;
      link.click();
      URL.revokeObjectURL(objectUrl);
    } catch (err) {
      setError(normalizeErrorMessage(err));
    }
  };

  useEffect(() => {
    if (!meQuery.isPending && !canAccessAdministration && activeTab === 'administration') {
      setActiveTab('projects');
    }
  }, [activeTab, canAccessAdministration, meQuery.isPending]);

  const hydratedProject = useRef('');
  useEffect(() => {
    if (!adminForm.project_key) {
      return;
    }
    if (!integrationsQuery.data || hydratedProject.current === adminForm.project_key) return;
    hydratedProject.current = adminForm.project_key;
    const meta = projectMeta[adminForm.project_key] ?? {
      repoProvider: 'github',
      repoUrl: '',
      pipelineProvider: 'azure-devops',
      pipelineUrl: '',
    };
    setAdminForm((current) => ({
      ...current,
      repoProvider: meta.repoProvider,
      repoUrl: meta.repoUrl,
      pipelineProvider: meta.pipelineProvider,
      pipelineUrl: meta.pipelineUrl,
    }));
  }, [adminForm.project_key, projectMeta]);

  const deleteMutation = useMutation({
    mutationFn: (projectKey: string) => apiFetch(`${API_BASE}/v1/analyses/projects/${encodeURIComponent(projectKey)}`, { method: 'DELETE' }),
    onSuccess: async (_, projectKey) => {
      setShowDeleteModal(false);
      setDeleteTarget(null);
      notify({ message: `Analyses deleted for ${projectKey}.` });
      await loadDashboard();
      await queryClient.invalidateQueries({ queryKey: ['audit'] });
    },
    onError: (err: Error) => setError(err.message),
  });
  const deletingAnalyses = deleteMutation.isPending;
  const deleteAnalyses = () => {
    if (deleteTarget && !deletingAnalyses) deleteMutation.mutate(deleteTarget);
  };

  const projectMutation = useMutation({
    mutationFn: (form: typeof adminForm) =>
      apiFetch(`${API_BASE}/v1/integrations/projects/${encodeURIComponent(form.project_key)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          repo_provider: form.repoProvider,
          repo_url: form.repoUrl.trim(),
          pipeline_provider: form.pipelineProvider,
          pipeline_url: form.pipelineUrl.trim(),
        }),
      }),
    onMutate: () => {
      setError(null);
    },
    onSuccess: async (_, form) => {
      await queryClient.invalidateQueries({ queryKey: ['integrations'] });
      notify({ message: `Links saved for ${form.project_key}.` });
    },
    onError: (err: Error) => setError(err.message),
  });
  const savingProjectMeta = projectMutation.isPending;
  const saveProjectMeta = () => {
    if (adminForm.project_key && !savingProjectMeta) projectMutation.mutate(adminForm);
  };

  const handleSignIn = async () => {
    setSignInError(null);
    const email = (signInEmailRef.current?.value ?? signInForm.email).trim().toLowerCase();
    const password = (signInPasswordRef.current?.value ?? signInForm.password).trim();

    if (!email || !password) {
      setSignInError('Email and password are required.');
      return;
    }

    try {
      const resp = await fetch(`${API_BASE}/v1/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
      });
      if (!resp.ok) {
        const err = await resp.json().catch(() => ({}));
        setSignInError((err as { detail?: string }).detail ?? 'Invalid credentials.');
        return;
      }
      const data = (await resp.json()) as { token: string; user: { email: string; full_name: string; role: string; permissions?: SessionUser['permissions'] } };
      const nextSession: SessionUser = {
        full_name: data.user.full_name,
        email: data.user.email,
        access_role: data.user.role as LocalUserRole,
        token: data.token,
        permissions: data.user.permissions,
      };
      onSessionChange(nextSession);
      if (signInForm.remember_me) {
        window.localStorage.setItem(LOCAL_STORAGE_SESSION_KEY, JSON.stringify(nextSession));
      } else {
        window.localStorage.removeItem(LOCAL_STORAGE_SESSION_KEY);
      }
      setSignInForm((current) => ({ ...current, password: '' }));
      setAuthScreen('app');
    } catch {
      setSignInError('Connection error — please try again.');
    }
  };

  const handleEntraSignIn = async (mode: 'interactive' | 'silent' = 'interactive') => {
    setSignInError(null);
    if (mode === 'interactive') window.sessionStorage.removeItem(ENTRA_AUTO_SUPPRESSED_KEY);
    try {
      const response = await fetch(`${API_BASE}/v1/auth/entra/login?mode=${mode}`, { credentials: 'include' });
      if (!response.ok) throw new Error('Failed to get authorization URL');
      const data = (await response.json()) as { auth_url: string };
      window.location.href = data.auth_url;
    } catch {
      setSignInError('Failed to initiate Azure AD sign-in. Check the Entra configuration in Administration.');
    }
  };

  const silentSignInClaimed = useRef(false);
  useEffect(() => {
    if (
      !shouldAttemptSilentEntra({
        signedIn,
        hasCallback: hasEntraCallback,
        entraEnabled: methodsQuery.data?.entra_enabled ?? false,
        autoSignInEnabled: methodsQuery.data?.auto_entra_sign_in ?? false,
        claimed: silentSignInClaimed.current,
        attemptedInTab: !!window.sessionStorage.getItem(ENTRA_AUTO_ATTEMPT_KEY),
        suppressedInTab: !!window.sessionStorage.getItem(ENTRA_AUTO_SUPPRESSED_KEY),
      })
    )
      return;
    // ponytail: sessionStorage is the smallest tab-scoped loop guard and survives the redirect round trip.
    silentSignInClaimed.current = true;
    window.sessionStorage.setItem(ENTRA_AUTO_ATTEMPT_KEY, '1');
    void handleEntraSignIn('silent');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hasEntraCallback, methodsQuery.data, signedIn]);

  const openSignOutPage = () => {
    setAuthScreen('sign-out');
  };

  const cancelSignOut = () => {
    setAuthScreen('app');
  };

  const confirmSignOut = () => {
    window.sessionStorage.setItem(ENTRA_AUTO_SUPPRESSED_KEY, '1');
    window.localStorage.removeItem(LOCAL_STORAGE_SESSION_KEY);
    setActiveTab('projects');
    onSessionChange(null);
    setSignInForm({ email: '', password: '', remember_me: true });
    setSignInError(null);
    setAuthScreen('sign-in');
  };

  const changeScope = (scope: ScopeMode) => {
    pushWorkspaceState({ ...workspaceUrlState, scope, projectMetric: '' });
    setProjectMetric('');
    setScopeMode(scope);
  };
  const refreshDashboard = () => void loadDashboard();
  const branchLoading = branchesQuery.isFetching || pullRequestsQuery.isFetching || selectedBranchQuery.isFetching || preferredBranchesQuery.isFetching || selectedPullRequestQuery.isFetching;
  const branchError = (branchesQuery.error ?? pullRequestsQuery.error ?? selectedBranchQuery.error ?? preferredBranchesQuery.error ?? selectedPullRequestQuery.error)?.message;
  // Until the server answers, a search that matches nothing on the loaded page proves nothing.
  const branchSearchStatus = (() => {
    if (!branchCursor && !branchesQuery.data?.next) return '';
    const term = branchSearch.trim();
    if (!term) return '';
    if (term !== branchSearchTerm || branchSearchQuery.isFetching) return 'Searching all branches…';
    if (branchSearchQuery.error) return `Other branch pages could not be searched: ${branchSearchQuery.error.message}`;
    return branchSearchQuery.data?.next ? 'More branches match than are listed; refine the search.' : '';
  })();
  const selectedAnalysis = selectedProjectAnalyses.find((analysis) => analysis.analysis_id === selectedAnalysisId);
  const newCodePanel = (
    <NewCode
      analysisId={selectedAnalysisId}
      token={sessionUser?.token}
      repoProvider={projectMeta[selectedProject]?.repoProvider}
      repoUrl={projectMeta[selectedProject]?.repoUrl}
      headBranch={selectedAnalysis?.branch ?? (pullRequestIdOf(selectedBranch) ? undefined : selectedBranch)}
      pullRequestId={selectedAnalysis?.pr_id}
    />
  );

  if (authScreen === 'sign-in') return <SignInScreen {...{ methodsQuery, signInForm, setSignInForm, signInError, signInEmailRef, signInPasswordRef, handleSignIn, handleEntraSignIn }} />;
  if (authScreen === 'sign-out') return <SignOutScreen {...{ sessionUser, confirmSignOut, cancelSignOut }} />;

  return (
    <AppShell
      visibleTabs={visibleTabs}
      activeTab={activeTab}
      onSelectTab={(tab) => {
        pushWorkspaceState({ ...workspaceUrlState, tab, projectMetric: '' });
        setProjectMetric('');
        setActiveTab(tab);
      }}
      sessionUser={sessionUser}
      accountName={meQuery.data?.full_name ?? sessionUser?.full_name ?? ''}
      photoUrl={photoUrl}
      photoFailed={photoFailed}
      onPhotoError={() => setPhotoFailed(true)}
      onAccount={() => setShowAccount(true)}
      onSignOut={openSignOutPage}
      releaseName={releaseName}
    >
      <div key={activeTab} className="workspace-view" id="workspace-content" tabIndex={-1}>
        <Suspense
          fallback={
            <p className="feedback" role="status">
              Loading workspace…
            </p>
          }
        >
          {meQuery.data?.directory_access_status === 'unavailable' && (
            <p className="feedback warning" role="status">
              Microsoft Entra group access is temporarily unavailable. Only your direct role and explicit grants are applied.
            </p>
          )}
          {!independentScope && loading && (
            <p className="feedback" role="status">
              Loading analysis data…
            </p>
          )}
          {!independentScope && error && (
            <p className="feedback error" role="alert">
              Failed to load data: {error}. Previously loaded results may be out of date.
            </p>
          )}
          {activeTab === 'projects' && !projectMetric && (
            <Projects
              summaries={summaries}
              selectedSummary={selectedSummary}
              selectedProject={selectedProject}
              selectedBranch={selectedBranch}
              selectedAnalysisId={selectedAnalysisId}
              favoritePending={favoriteMutation.isPending}
              favoriteError={favoriteMutation.error?.message}
              branchLoading={branchLoading}
              branchError={branchError}
              onBranchSearch={setBranchSearch}
              branchSearchStatus={branchSearchStatus}
              setSelectedProject={(project) => {
                pushWorkspaceState({ ...workspaceUrlState, project, branch: selectedBranch, analysis: '', projectMetric: '' });
                setProjectMetric('');
                setSelectedAnalysisId('');
                setSelectedProject(project);
              }}
              setSelectedBranch={(branch) => {
                if (branch) pushWorkspaceState({ ...workspaceUrlState, branch, analysis: '', projectMetric: '' });
                setProjectMetric('');
                setSelectedBranch(branch);
              }}
              onToggleFavorite={(summary) => favoriteMutation.mutate({ projectKey: summary.projectKey, isFavorite: summary.isFavorite })}
              loading={loading}
              error={error}
              projectMeta={projectMeta}
              onRefresh={refreshDashboard}
              scopeMode={scopeMode}
              onScopeChange={changeScope}
              newCodePanel={newCodePanel}
              onOpenNewCode={() => {
                pushWorkspaceState({ ...workspaceUrlState, scope: 'new-code', projectMetric: '' });
                setProjectMetric('');
                setScopeMode('new-code');
              }}
              onOpenIssues={(summary, analysis, severity: IssueSeverityFilter = 'all', category: IssueTypeFilter = 'all') => {
                const nextIssues = { ...defaultIssueFilters, severity, category };
                pushWorkspaceState({
                  ...workspaceUrlState,
                  tab: 'issues',
                  project: summary.projectKey,
                  branch: analysisSelectionKey(analysis),
                  analysis: analysis.analysis_id,
                  projectMetric: '',
                  issues: nextIssues,
                });
                setSelectedProject(summary.projectKey);
                setSelectedBranch(analysisSelectionKey(analysis));
                setSelectedAnalysisId(analysis.analysis_id);
                setProjectMetric('');
                setIssueFilters(nextIssues);
                setActiveTab('issues');
              }}
              authToken={sessionUser?.token}
              onOpenCodeQuality={(summary, analysis, metric) => {
                pushWorkspaceState({
                  ...workspaceUrlState,
                  tab: 'projects',
                  scope: 'overall',
                  project: summary.projectKey,
                  branch: analysisSelectionKey(analysis),
                  analysis: analysis.analysis_id,
                  projectMetric: metric,
                });
                setSelectedProject(summary.projectKey);
                setSelectedBranch(analysisSelectionKey(analysis));
                setSelectedAnalysisId(analysis.analysis_id);
                setProjectMetric(metric);
              }}
            />
          )}
          {activeTab === 'projects' && projectMetric && (
            <CodeQualityDetails
              key={`${selectedAnalysisId}:${projectMetric}`}
              analysisId={selectedAnalysisId}
              metric={projectMetric}
              authToken={sessionUser?.token}
              repoProvider={projectMeta[selectedProject]?.repoProvider}
              repoUrl={projectMeta[selectedProject]?.repoUrl}
              projectHref={workspaceSearch({ ...workspaceUrlState, projectMetric: '' })}
              onMetricChange={(metric) => {
                pushWorkspaceState({ ...workspaceUrlState, projectMetric: metric });
                setProjectMetric(metric);
              }}
              onBack={() => {
                pushWorkspaceState({ ...workspaceUrlState, projectMetric: '' });
                setProjectMetric('');
              }}
            />
          )}
          {activeTab === 'issues' && (
            <Issues
              authToken={sessionUser?.token}
              isAdmin={canAccessAdministration}
              selectedProject={selectedProject}
              selectedBranch={selectedBranch}
              selectedAnalysisId={selectedAnalysisId}
              branchLoading={branchLoading}
              branchError={branchError}
              onBranchSearch={setBranchSearch}
              branchSearchStatus={branchSearchStatus}
              selectedSummary={selectedSummary}
              summaries={summaries}
              setSelectedProject={(project) => {
                pushWorkspaceState({ ...workspaceUrlState, project, branch: selectedBranch, analysis: '' });
                setSelectedAnalysisId('');
                setSelectedProject(project);
              }}
              setSelectedBranch={(branch) => {
                if (branch) pushWorkspaceState({ ...workspaceUrlState, branch, analysis: '' });
                setSelectedBranch(branch);
              }}
              setSelectedAnalysisId={(analysis) => {
                pushWorkspaceState({ ...workspaceUrlState, analysis });
                setSelectedAnalysisId(analysis);
              }}
              projectMeta={projectMeta}
              filters={issueFilters}
              onFiltersChange={setIssueFilters}
              downloadExport={downloadExport}
              scopeMode={scopeMode}
              onScopeChange={changeScope}
              refreshing={loading}
              onRefresh={refreshDashboard}
              newCodePanel={newCodePanel}
            />
          )}

          {activeTab === 'rules' && (
            <>
              <Rules
                apiBase={API_BASE}
                authToken={sessionUser?.token}
                selectedProject={selectedProject}
                onProjectChange={(project) => {
                  pushWorkspaceState({ ...workspaceUrlState, project, branch: '', analysis: '' });
                  setSelectedProject(project);
                  setSelectedBranch('');
                }}
                summaries={summaries}
                canManageRules={canAccessAdministration}
                filters={ruleFilters}
                onFiltersChange={setRuleFilters}
                selectedRuleId={selectedRuleId}
                onSelectedRuleChange={setSelectedRuleId}
              />
              <details
                className="advanced-rules-panel profile-administration"
                onToggle={(event) => {
                  if (event.currentTarget.open) setProfilesActivated(true);
                }}
              >
                <summary>Advanced profile administration</summary>
                {profilesActivated && <AnalysisProfiles key={selectedProject} api={API_BASE} token={sessionUser?.token} project={selectedProject} admin={canAccessAdministration} />}
              </details>
            </>
          )}

          {activeTab === 'quality-gates' && (
            <>
              {(gatesQuery.error ?? assignmentsQuery.error) && (
                <p className="feedback error" role="alert">
                  {(gatesQuery.error ?? assignmentsQuery.error)?.message}
                  <Button type="button" onClick={() => void loadGates()}>
                    Retry quality gates
                  </Button>
                </p>
              )}
              {(gatesQuery.isLoading || assignmentsQuery.isLoading) && <p role="status">Loading quality gates and assignments…</p>}
              {gatesQuery.data !== undefined && assignmentsQuery.data !== undefined && (
                <QualityGates
                  gateLibrary={gateLibrary}
                  gateAssignments={gateAssignments}
                  summaries={summaries}
                  canAccessAdministration={canAccessAdministration && !gatesQuery.isError && !assignmentsQuery.isError}
                  apiBase={API_BASE}
                  apiFetch={apiFetch}
                  selectedGateId={selectedGateId}
                  onSelectedGateChange={setSelectedGateId}
                />
              )}
            </>
          )}

          {activeTab === 'administration' && canAccessAdministration && (
            <Administration
              {...{
                activeAdminSection,
                setActiveAdminSection,
                adminReadError,
                sessionUser,
                projectPickerOptions,
                adminForm,
                setAdminForm,
                savingProjectMeta,
                saveProjectMeta,
                azureDevOpsConfigs,
                deleteTarget,
                setDeleteTarget,
                setShowDeleteModal,
              }}
              localLoginVisible={methodsQuery.data?.local_login_visible ?? true}
            />
          )}

          {!independentScope && (projectCursor || summaryQuery.data?.next_cursor) && (
            <div className="action-row pagination-row" aria-label="Project pages">
              {projectCursor && (
                <Button
                  type="button"
                  onClick={() => {
                    setSelectedProject('');
                    setProjectCursor('');
                  }}
                >
                  First page
                </Button>
              )}
              {summaryQuery.data?.next_cursor && (
                <Button
                  type="button"
                  onClick={() => {
                    setSelectedProject('');
                    setProjectCursor(summaryQuery.data!.next_cursor!);
                  }}
                >
                  Next
                </Button>
              )}
            </div>
          )}
          {!independentScope && selectedProject && (branchCursor || branchesQuery.data?.next || branchesQuery.error) && (
            <div className="action-row pagination-row" aria-label="Branch pages">
              {branchCursor && (
                <Button type="button" onClick={() => setBranchPage({ project: selectedProject, cursor: '' })}>
                  First page
                </Button>
              )}
              {branchesQuery.data?.next && (
                <Button type="button" onClick={() => setBranchPage({ project: selectedProject, cursor: branchesQuery.data!.next! })}>
                  Next
                </Button>
              )}
              {branchesQuery.error && <p role="alert">{branchesQuery.error.message}</p>}
            </div>
          )}
          {selectedProject && !projectMetric && ['projects', 'issues'].includes(activeTab) && (historyCursor || analysesQuery.data?.next) && (
            <div className="action-row pagination-row" aria-label="Analysis history pages">
              {historyCursor && (
                <Button type="button" onClick={() => setHistoryPage({ project: selectedProject, branch: selectedBranch, cursor: '' })}>
                  First page
                </Button>
              )}
              {analysesQuery.data?.next && (
                <Button type="button" disabled={analysesQuery.isFetching} onClick={() => setHistoryPage({ project: selectedProject, branch: selectedBranch, cursor: analysesQuery.data!.next! })}>
                  Next
                </Button>
              )}
            </div>
          )}
        </Suspense>
      </div>

      {showAccount && (
        <Suspense
          fallback={
            <p className="feedback" role="status">
              Loading account…
            </p>
          }
        >
          <AccountTokens authToken={sessionUser?.token} onClose={() => setShowAccount(false)} />
        </Suspense>
      )}

      {showDeleteModal && deleteTarget && (
        <dialog
          className="config-modal"
          ref={deleteDialogRef}
          aria-label="Confirm deletion"
          onCancel={(event) => {
            event.preventDefault();
            if (!deletingAnalyses) setShowDeleteModal(false);
          }}
        >
          <header className="config-modal-header">
            <h3>Confirm deletion</h3>
            <DialogCloseButton onClick={() => setShowDeleteModal(false)} disabled={deletingAnalyses} />
          </header>
          <div style={{ padding: '16px' }}>
            {deleteMutation.error && (
              <p className="feedback error" role="alert">
                {deleteMutation.error.message}
              </p>
            )}
            <p style={{ marginTop: 0, marginBottom: '12px' }}>{`Are you sure you want to delete all analyses for project "${deleteTarget}"? This cannot be undone.`}</p>
            <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
              <Button type="button" onClick={() => setShowDeleteModal(false)} disabled={deletingAnalyses}>
                Cancel
              </Button>
              <Button
                type="button"
                onClick={() => void deleteAnalyses()}
                disabled={deletingAnalyses}
                style={{
                  backgroundColor: 'var(--danger)',
                  color: 'white',
                  border: 'none',
                  padding: '8px 16px',
                  borderRadius: '4px',
                  cursor: deletingAnalyses ? 'not-allowed' : 'pointer',
                  fontWeight: 'bold',
                }}
              >
                {deletingAnalyses ? 'Deleting...' : 'Yes, delete permanently'}
              </Button>
            </div>
          </div>
        </dialog>
      )}
    </AppShell>
  );
}
