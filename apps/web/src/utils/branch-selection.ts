import type { BranchSummary } from '@/types/api';

export function displayBranchName(branch: string) {
  return branch.replace(/^refs\/heads\//, '');
}

/** Listed first and chosen by default, in this order. */
export const PREFERRED_BRANCHES = ['main', 'master'];

export function compareBranchNames(left: string, right: string) {
  const rank = (branch: string) => {
    const index = PREFERRED_BRANCHES.indexOf(displayBranchName(branch).toLocaleLowerCase());
    return index < 0 ? PREFERRED_BRANCHES.length : index;
  };
  return rank(left) - rank(right) || displayBranchName(left).localeCompare(displayBranchName(right));
}

export function selectAvailableBranch(branches: string[], currentBranch: string, latestBranch: string) {
  const findEquivalent = (candidate: string) => (candidate ? branches.find((branch) => displayBranchName(branch) === displayBranchName(candidate)) : undefined);
  const preferred = PREFERRED_BRANCHES.map((name) => branches.find((branch) => displayBranchName(branch).toLocaleLowerCase() === name)).find(Boolean);

  return (
    findEquivalent(currentBranch) ??
    preferred ??
    findEquivalent(latestBranch) ??
    branches[0] ??
    ''
  );
}

const PULL_REQUEST_PREFIX = 'pr:';

/** Selection key of a pull request. `:` is invalid in a Git ref, so it never collides with a branch name. */
export function pullRequestKey(id: string) {
  return `${PULL_REQUEST_PREFIX}${id}`;
}

export function pullRequestIdOf(key: string) {
  return key.startsWith(PULL_REQUEST_PREFIX) ? key.slice(PULL_REQUEST_PREFIX.length) : null;
}

/** A pull request build is selected through its pull request, never through its source branch. */
export function analysisSelectionKey(analysis: { branch: string; pr_id?: string | null }) {
  return analysis.pr_id ? pullRequestKey(analysis.pr_id) : analysis.branch;
}

type PullRequestReference = { id: string; source_branch: string; target_branch: string | null };

export function pullRequestRoute(pullRequest: PullRequestReference) {
  return `${displayBranchName(pullRequest.source_branch)} → ${pullRequest.target_branch ? displayBranchName(pullRequest.target_branch) : 'target not recorded'}`;
}

export function pullRequestLabel(pullRequest: PullRequestReference) {
  return `PR #${pullRequest.id} · ${pullRequestRoute(pullRequest)}`;
}

/** BRANCH picker options: branches first, then pull requests; headings only when both kinds exist. */
export function branchPickerOptions(branches: BranchSummary[]) {
  const pullRequests = branches.filter((branch) => branch.pull_request);
  const grouped = pullRequests.length > 0;
  return [
    ...[...new Set(branches.filter((branch) => !branch.pull_request).map((branch) => branch.name))]
      .sort(compareBranchNames)
      .map((name) => ({ key: name, name: displayBranchName(name), group: grouped ? 'Branches' : undefined })),
    ...pullRequests.map((branch) => ({ key: branch.name, name: pullRequestLabel(branch.pull_request!), group: 'Pull requests' })),
  ];
}
