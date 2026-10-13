import { API_BASE } from '../config/env';
import { useQuery } from '@tanstack/react-query';
import { apiFetch, apiJson, apiPage } from '../services/api';
import type { BranchSummary, DashboardSummary, PullRequestSummary } from '../types/api';
import { PREFERRED_BRANCHES, pullRequestIdOf } from '../utils/branch-selection';
import type { AnalysisRecord, AuthenticationMethods, GateLibraryItem, ProjectIntegrationRecord, SessionProfile, AzureDevOpsOrganizationConfig, AppTab, AdminSection } from '../types/api';

// Administration sections whose project picker is fed by the dashboard summary.
const PROJECT_PICKER_ADMIN_SECTIONS = new Set<AdminSection>(['devops-integrations', 'project-management', 'new-code-settings', 'sla-settings', 'work-item-settings']);

export function useWorkspaceData(
  authToken: string | undefined,
  activeTab: AppTab,
  activeAdminSection: AdminSection,
  project = '',
  branch = '',
  cursor = '',
  projectCursor = '',
  branchCursor = '',
  branchSearch = '',
) {
  const signedIn = !!authToken;
  const analysisWorkspaceActive = activeTab === 'projects' || activeTab === 'issues';
  const summaryActive =
    analysisWorkspaceActive ||
    activeTab === 'rules' ||
    activeTab === 'quality-gates' ||
    (activeTab === 'administration' && PROJECT_PICKER_ADMIN_SECTIONS.has(activeAdminSection));
  const integrationsActive =
    analysisWorkspaceActive || (activeTab === 'administration' && activeAdminSection === 'devops-integrations');
  const meQuery = useQuery({
    queryKey: ['me'],
    enabled: signedIn,
    queryFn: ({ signal }) => apiJson<SessionProfile>(`${API_BASE}/v1/auth/me`, authToken, { signal }),
  });
  const methodsQuery = useQuery({
    queryKey: ['auth-methods'],
    enabled: !signedIn || (activeTab === 'administration' && activeAdminSection === 'access-management'),
    queryFn: ({ signal }) => apiJson<AuthenticationMethods>(`${API_BASE}/v1/auth/methods`, undefined, { signal }),
  });
  const photoQuery = useQuery({
    queryKey: ['me', 'photo'],
    enabled: signedIn && meQuery.data?.auth_provider === 'entra',
    retry: false,
    queryFn: async ({ signal }) => (await apiFetch(`${API_BASE}/v1/auth/me/photo`, authToken, { signal })).blob(),
  });

  const canAccessAdministration = !meQuery.isError && meQuery.data?.permissions?.is_admin === true;
  const adminActive = canAccessAdministration && activeTab === 'administration';
  // `branch` is a selection key: a branch name, or `pr:<id>` for a pull request.
  const pullRequestId = pullRequestIdOf(branch);
  const analysesQuery = useQuery({
    queryKey: ['analyses', project, branch, cursor],
    enabled: signedIn && analysisWorkspaceActive && !!project,
    queryFn: ({ signal }) => {
      const scope: Record<string, string> = pullRequestId ? { kind: 'pull_request', pr_id: pullRequestId } : { kind: 'branch', branch };
      return apiPage<AnalysisRecord>(`${API_BASE}/v1/insights/analyses?${new URLSearchParams({ project_key: project, ...scope, cursor })}`, authToken, { signal });
    },
  });
  const summaryQuery = useQuery({
    queryKey: ['analyses', 'summary', projectCursor],
    enabled: signedIn && summaryActive,
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ cursor: projectCursor });
      return apiJson<DashboardSummary>(`${API_BASE}/v1/insights/summary?${params}`, authToken, { signal });
    },
  });

  const selectedSummaryQuery = useQuery({
    queryKey: ['analyses', 'summary-project', project],
    enabled: signedIn && analysisWorkspaceActive && !!project,
    queryFn: ({ signal }) => apiJson<DashboardSummary>(`${API_BASE}/v1/insights/summary?project_key=${encodeURIComponent(project)}`, authToken, { signal }),
  });

  const branchesQuery = useQuery({
    queryKey: ['branches', project, branchCursor],
    enabled: signedIn && analysisWorkspaceActive && !!project,
    queryFn: ({ signal }) =>
      apiPage<BranchSummary>(`${API_BASE}/v1/insights/branches?${new URLSearchParams({ project_key: project, cursor: branchCursor })}`, authToken, {
        signal,
      }),
  });
  const selectedBranchQuery = useQuery({
    queryKey: ['branches', project, 'selected', branch],
    enabled: signedIn && analysisWorkspaceActive && !!project && !!branch && !pullRequestId,
    queryFn: ({ signal }) => apiPage<BranchSummary>(`${API_BASE}/v1/insights/branches?${new URLSearchParams({ project_key: project, branch })}`, authToken, { signal }),
  });
  const branchListPaged = !!branchCursor || !!branchesQuery.data?.next;
  // The default choice must not depend on the loaded page: main and master are asked for by name.
  const preferredBranchesQuery = useQuery({
    queryKey: ['branches', project, 'preferred'],
    enabled: signedIn && analysisWorkspaceActive && !!project && branchListPaged,
    queryFn: async ({ signal }) => {
      const pages = await Promise.all(
        PREFERRED_BRANCHES.map((name) => apiPage<BranchSummary>(`${API_BASE}/v1/insights/branches?${new URLSearchParams({ project_key: project, branch: name })}`, authToken, { signal })),
      );
      return pages.flatMap((page) => page.items);
    },
  });
  // The picker filters the loaded page itself; the server is only asked when other pages exist.
  const branchSearchQuery = useQuery({
    queryKey: ['branches', project, 'search', branchSearch],
    enabled: signedIn && analysisWorkspaceActive && !!project && !!branchSearch && branchListPaged,
    queryFn: ({ signal }) =>
      apiPage<BranchSummary>(`${API_BASE}/v1/insights/branches?${new URLSearchParams({ project_key: project, search: branchSearch, limit: '100' })}`, authToken, { signal }),
  });
  // ponytail: the newest pull requests only; the selected one is always fetched on its own below.
  const pullRequestsQuery = useQuery({
    queryKey: ['pull-requests', project],
    enabled: signedIn && analysisWorkspaceActive && !!project,
    queryFn: ({ signal }) => apiPage<PullRequestSummary>(`${API_BASE}/v1/insights/pull-requests?${new URLSearchParams({ project_key: project })}`, authToken, { signal }),
  });
  const selectedPullRequestQuery = useQuery({
    queryKey: ['pull-requests', project, 'selected', pullRequestId],
    enabled: signedIn && analysisWorkspaceActive && !!project && !!pullRequestId,
    queryFn: ({ signal }) =>
      apiPage<PullRequestSummary>(`${API_BASE}/v1/insights/pull-requests?${new URLSearchParams({ project_key: project, pr_id: pullRequestId ?? '' })}`, authToken, { signal }),
  });

  const integrationsQuery = useQuery({
    queryKey: ['integrations'],
    enabled: signedIn && integrationsActive,
    queryFn: ({ signal }) => apiJson<ProjectIntegrationRecord[]>(`${API_BASE}/v1/integrations/projects`, authToken, { signal }),
  });

  const gatesQuery = useQuery({
    queryKey: ['gates', 'library'],
    enabled: signedIn && activeTab === 'quality-gates',
    queryFn: ({ signal }) => apiJson<GateLibraryItem[]>(`${API_BASE}/v1/gates`, authToken, { signal }),
  });

  const assignmentsQuery = useQuery({
    queryKey: ['gate-assignments'],
    enabled: signedIn && activeTab === 'quality-gates',
    queryFn: ({ signal }) => apiJson<Record<string, string>>(`${API_BASE}/v1/gates/assignments`, authToken, { signal }),
  });

  const devOpsQuery = useQuery({
    queryKey: ['devops'],
    enabled: adminActive && activeAdminSection === 'devops-integrations',
    queryFn: ({ signal }) => apiJson<AzureDevOpsOrganizationConfig[]>(`${API_BASE}/v1/integrations/azure-devops/configs`, authToken, { signal }),
  });

  return {
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
  };
}
