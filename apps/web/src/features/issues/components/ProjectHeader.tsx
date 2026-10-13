import { Button, ListBox } from '@/components/ui';
import { useMemo } from "react";
import type { ProjectSummary, ScopeMode } from "@/types/api";
import { analysisGateClass, analysisGateLabel } from "@/utils/metric-display";
import { repositoryBranchLink, repositoryCommitLink, repositoryPullRequestLink } from "@/utils/evidence-links";
import { analysisSelectionKey, branchPickerOptions, displayBranchName, pullRequestIdOf } from '@/utils/branch-selection';
import ProjectPicker from '@/components/common/ProjectPicker';
import ScopeToggle from '@/components/common/ScopeToggle';
import "./ProjectHeader.css";

interface ProjectHeaderProps {
  selectedSummary: ProjectSummary | null;
  allSummaries: ProjectSummary[];
  onProjectChange: (projectKey: string) => void;
  onBranchChange: (branch: string) => void;
  selectedBranch: string;
  branchLoading?: boolean;
  branchError?: string | null;
  onBranchSearch?: (search: string) => void;
  branchSearchStatus?: string;
  selectedAnalysisId?: string;
  onAnalysisChange?: (analysisId: string) => void;
  hideProjectSelector?: boolean;
  scopeUnavailable?: boolean;
  repoProvider?: 'github' | 'azure-devops' | 'other';
  repoUrl?: string;
  scopeMode?: ScopeMode;
  onScopeChange?: (scope: ScopeMode) => void;
  refreshing?: boolean;
  onRefresh?: () => void;
}

export default function ProjectHeader({
  selectedSummary,
  allSummaries,
  onProjectChange,
  onBranchChange,
  selectedBranch,
  branchLoading = false,
  branchError,
  onBranchSearch,
  branchSearchStatus,
  selectedAnalysisId,
  onAnalysisChange,
  hideProjectSelector = false,
  scopeUnavailable = false,
  repoProvider = 'other',
  repoUrl,
  scopeMode,
  onScopeChange,
  refreshing = false,
  onRefresh,
}: ProjectHeaderProps) {
  const projectOptions = useMemo(() => allSummaries.map((summary) => ({ key: summary.projectKey })), [allSummaries]);

  const branchOptions = useMemo(
    () =>
      branchPickerOptions(
        selectedSummary?.branches ??
          (selectedSummary?.history ?? []).filter((analysis) => !analysis.pr_id).map((analysis) => ({ name: analysis.branch, count: 0, latest: analysis })),
      ),
    [selectedSummary],
  );
  const selectedBranchValue = branchOptions.find((branch) => displayBranchName(branch.key) === displayBranchName(selectedBranch))?.key ?? '';

  const branchAnalyses = useMemo(() => {
    if (!selectedSummary || !selectedBranch) return [];
    return selectedSummary.history.filter((analysis) => analysisSelectionKey(analysis) === selectedBranch);
  }, [selectedSummary, selectedBranch]);

  // Keep the visible metadata tied to the explicitly selected analysis.
  const selectedBranchData = useMemo(() => {
    if (!selectedSummary || !selectedBranch) return null;
    return branchAnalyses.find((analysis) => analysis.analysis_id === selectedAnalysisId) ?? branchAnalyses[0] ?? null;
  }, [branchAnalyses, selectedAnalysisId, selectedBranch, selectedSummary]);
  // A pull request is reached through the PR link of the metadata row, not through a branch link.
  const branchUrl = pullRequestIdOf(selectedBranchValue) ? null : repositoryBranchLink(repoProvider, repoUrl, selectedBranchValue);
  const commitUrl = repositoryCommitLink(repoProvider, repoUrl, selectedBranchData?.commit_sha);
  const pullRequestUrl = repositoryPullRequestLink(repoProvider, repoUrl, selectedBranchData?.pr_id);

  return (
    <div className="project-header">
      <div className="project-header-top">
        <div className="project-selector-group">
          {!hideProjectSelector && (
            <div className="project-name-section">
              <ProjectPicker
                label="Project"
                options={projectOptions}
                value={selectedSummary?.projectKey ?? ""}
                onChange={onProjectChange}
                className="project-selector"
              />
            </div>
          )}

          {!hideProjectSelector && (
            <div className="branch-selector-group reference-picker">
              <ProjectPicker
                label="Branch"
                ariaLabel="Branches"
                className="branch-picker"
                options={branchOptions}
                value={selectedBranchValue}
                onChange={onBranchChange}
                disabled={!selectedSummary}
                loading={branchLoading}
                loadingText="Loading branches…"
                error={branchError}
                placeholder={selectedSummary ? 'No branches available' : 'Select a project first'}
                searchLabel="Search branches"
                searchPlaceholder="Branch name"
                emptyResultsMessage={branchOptions.length === 0 ? 'No branches are available for this project.' : 'No branch matches this search.'}
                onSearchChange={onBranchSearch}
                searchStatus={branchSearchStatus}
                hideOptionKey
              />
              {branchUrl && <a href={branchUrl} target="_blank" rel="noreferrer" aria-label={`Open branch ${displayBranchName(selectedBranchValue)} in the repository`} title="Open branch in the repository">↗</a>}
            </div>
          )}

          {onAnalysisChange && branchAnalyses.length > 0 && (
            <div className="analysis-selector-group">
      <label htmlFor="analysis-selector">Analysis</label>
              <ListBox
                id="analysis-selector"
                value={selectedBranchData?.analysis_id ?? ""}
                onChange={(event) => onAnalysisChange(event.target.value)}
              >
                {branchAnalyses.map((analysis) => (
                  <option key={analysis.analysis_id} value={analysis.analysis_id}>
                    {new Date(analysis.created_at).toLocaleString()} · {analysis.commit_sha?.substring(0, 7) || 'unknown commit'}
                  </option>
                ))}
              </ListBox>
            </div>
          )}

          {onRefresh && (
            <Button type="button" className="context-refresh" onClick={onRefresh} disabled={refreshing}>
              {refreshing ? 'Refreshing...' : 'Refresh'}
            </Button>
          )}
        </div>

        {selectedBranchData && (
          <div className="branch-info">
            {!scopeUnavailable && (
              <span className={`quality-gate-badge ${analysisGateClass(selectedBranchData.quality_gate, selectedBranchData.completeness)}`}>
                Quality Gate: {analysisGateLabel(selectedBranchData.quality_gate, selectedBranchData.completeness)}
              </span>
            )}
            <span className={`analysis-status status-${selectedBranchData.status}`}>
              Analysis: {({ queued: "Queued", running: "Running", completed: "Completed", failed: "Technical failure" })[selectedBranchData.status] ?? "Unknown"}
            </span>
            {commitUrl ? (
              <a className="commit-sha repository-reference" href={commitUrl} target="_blank" rel="noreferrer" title={selectedBranchData.commit_sha}>
                Commit {selectedBranchData.commit_sha.substring(0, 7)} ↗
              </a>
          ) : <span className="commit-sha" title={selectedBranchData.commit_sha}>{selectedBranchData.commit_sha ? `Commit ${selectedBranchData.commit_sha.substring(0, 7)}` : "Commit not available"}</span>}
            {pullRequestUrl && <a className="repository-reference" href={pullRequestUrl} target="_blank" rel="noreferrer">PR #{selectedBranchData.pr_id} ↗</a>}
            <span className="analysis-date">
              Updated {new Date(selectedBranchData.created_at).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })}
            </span>
            {scopeMode && onScopeChange && <ScopeToggle value={scopeMode} onChange={onScopeChange} />}
          </div>
        )}
      </div>
    </div>
  );
}
