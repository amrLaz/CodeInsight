import { describe, expect, it } from 'vitest';
import type { AnalysisRecord } from '@/types/api';
import { analysisSelectionKey, branchPickerOptions, compareBranchNames, displayBranchName, pullRequestIdOf, pullRequestKey, selectAvailableBranch } from './branch-selection';

describe('branch selection', () => {
  it('hides refs/heads only in the displayed branch name', () => {
    expect(displayBranchName('refs/heads/feat/claude-setup-notice')).toBe('feat/claude-setup-notice');
    expect(displayBranchName('feat/refs/heads/example')).toBe('feat/refs/heads/example');
  });

  it('preserves the current branch when the new project exposes an equivalent ref', () => {
    expect(selectAvailableBranch(['main', 'refs/heads/feat/shared'], 'feat/shared', 'main')).toBe('refs/heads/feat/shared');
  });

  it('orders main and master before other branch names even when refs are stored', () => {
    expect(['develop', 'refs/heads/master', 'refs/heads/main'].sort(compareBranchNames)).toEqual(['refs/heads/main', 'refs/heads/master', 'develop']);
  });

  it('falls back to main, then master, the latest known branch, and finally the first branch', () => {
    expect(selectAvailableBranch(['develop', 'main'], 'feat/old', 'develop')).toBe('main');
    expect(selectAvailableBranch(['develop', 'master'], 'feat/old', 'develop')).toBe('master');
    expect(selectAvailableBranch(['develop', 'release'], 'feat/old', 'release')).toBe('release');
    expect(selectAvailableBranch(['develop'], 'feat/old', 'release')).toBe('develop');
  });

  it('returns an empty value when the project has no branch', () => {
    expect(selectAvailableBranch([], 'feat/old', 'main')).toBe('');
  });

  it('selects a pull request build through its pull request, never through its source branch', () => {
    expect(pullRequestKey('51382')).toBe('pr:51382');
    expect(pullRequestIdOf('pr:51382')).toBe('51382');
    expect(pullRequestIdOf('refs/heads/develop')).toBeNull();
    expect(analysisSelectionKey({ branch: 'refs/heads/develop', pr_id: '51382' })).toBe('pr:51382');
    expect(analysisSelectionKey({ branch: 'refs/heads/develop', pr_id: null })).toBe('refs/heads/develop');
  });

  it('lists branches before pull requests and shows group headings only when both exist', () => {
    const latest = { analysis_id: 'a' } as AnalysisRecord;
    expect(branchPickerOptions([{ name: 'develop', count: 1, latest }, { name: 'refs/heads/main', count: 1, latest }])).toEqual([
      { key: 'refs/heads/main', name: 'main', group: undefined },
      { key: 'develop', name: 'develop', group: undefined },
    ]);
    expect(
      branchPickerOptions([
        { name: 'pr:51382', count: 2, latest, pull_request: { id: '51382', source_branch: 'refs/heads/develop', target_branch: 'refs/heads/master' } },
        { name: 'pr:7', count: 1, latest, pull_request: { id: '7', source_branch: 'refs/heads/feature/x', target_branch: null } },
        { name: 'refs/heads/develop', count: 0, latest: null },
      ]),
    ).toEqual([
      { key: 'refs/heads/develop', name: 'develop', group: 'Branches' },
      { key: 'pr:51382', name: 'PR #51382 · develop → master', group: 'Pull requests' },
      { key: 'pr:7', name: 'PR #7 · feature/x → target not recorded', group: 'Pull requests' },
    ]);
  });
});
