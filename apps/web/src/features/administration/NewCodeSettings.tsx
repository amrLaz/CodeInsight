import { API_BASE } from '@/config/env';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { apiFetch, apiJson } from '@/services/api';
import { displayBranchName } from '@/utils/branch-selection';
import { useToast } from '@/context/ToastProvider';
import ProjectPicker from '@/components/common/ProjectPicker';
import { Button, Grid, ListBox, TextBox, type GridColumn } from '@/components/ui';

type DefinitionLevel = 'global' | 'project' | 'branch';
type DefinitionMode = 'previous_version' | 'number_of_days' | 'reference_branch' | 'specific_analysis';
type NewCodeDefinition = {
  scope_key: string;
  level: DefinitionLevel;
  project_key: string | null;
  branch: string | null;
  mode: DefinitionMode;
  number_of_days: number | null;
  reference_branch: string | null;
  analysis_id: string | null;
  updated_at: string;
};
type ProjectNewCodeSettings = {
  project_key: string;
  main_branch: string | null;
  definitions: NewCodeDefinition[];
};

const MODES: Record<DefinitionLevel, readonly DefinitionMode[]> = {
  global: ['previous_version', 'number_of_days'],
  project: ['previous_version', 'number_of_days', 'reference_branch'],
  branch: ['previous_version', 'number_of_days', 'reference_branch', 'specific_analysis'],
};
const MODE_LABELS: Record<DefinitionMode, string> = {
  previous_version: 'Previous version',
  number_of_days: 'Number of days',
  reference_branch: 'Reference branch',
  specific_analysis: 'Specific analysis',
};

function canonicalBranch(value: string) {
  const branch = value.trim();
  return !branch || branch.startsWith('refs/') ? branch : `refs/heads/${branch}`;
}

function definitionScopeKey(level: DefinitionLevel, projectKey: string, branch: string) {
  if (level === 'global') return 'global';
  if (level === 'project') return `project:${projectKey}`;
  return `branch:${projectKey}:${canonicalBranch(branch)}`;
}

function definitionValue(definition: NewCodeDefinition) {
  if (definition.mode === 'number_of_days') return `${definition.number_of_days ?? 'Not set'} days`;
  if (definition.mode === 'reference_branch') return definition.reference_branch ? displayBranchName(definition.reference_branch) : 'Not set';
  if (definition.mode === 'specific_analysis') return definition.analysis_id ?? 'Not set';
  return 'Latest completed snapshot from a different project version';
}

export default function NewCodeSettings({ authToken, projects }: { authToken?: string; projects: string[] }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const projectOptions = useMemo(() => projects.map((key) => ({ key })), [projects]);
  const [level, setLevel] = useState<DefinitionLevel>('global');
  const [pickedProject, setProjectKey] = useState('');
  // The project list loads asynchronously: default to its first entry once it arrives.
  const projectKey = pickedProject || projects[0] || '';
  const [branch, setBranch] = useState('');
  const [mode, setMode] = useState<DefinitionMode>('previous_version');
  const [numberOfDays, setNumberOfDays] = useState('30');
  const [referenceBranch, setReferenceBranch] = useState('');
  const [analysisId, setAnalysisId] = useState('');
  const [editingScopeKey, setEditingScopeKey] = useState<string | null>(null);
  const [mainBranchDraft, setMainBranchDraft] = useState<string | null>(null);

  const definitionsQuery = useQuery({
    queryKey: ['new-code-definitions'],
    queryFn: ({ signal }) => apiJson<NewCodeDefinition[]>(`${API_BASE}/v1/new-code/definitions`, authToken, { signal }),
  });
  const projectQuery = useQuery({
    queryKey: ['new-code-project', projectKey],
    enabled: !!projectKey,
    queryFn: ({ signal }) => apiJson<ProjectNewCodeSettings>(`${API_BASE}/v1/new-code/projects/${encodeURIComponent(projectKey)}`, authToken, { signal }),
  });
  const definitionMutation = useMutation({
    mutationFn: async ({ action, definition }: { action: 'save' | 'delete'; definition: NewCodeDefinition }) => {
      const path = `${API_BASE}/v1/new-code/definitions/${definition.level}/${encodeURIComponent(definition.scope_key)}`;
      if (action === 'delete') {
        await apiFetch(path, authToken, { method: 'DELETE' });
        return;
      }
      await apiJson<NewCodeDefinition>(path, authToken, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          level: definition.level,
          project_key: definition.project_key,
          branch: definition.branch,
          mode: definition.mode,
          number_of_days: definition.number_of_days,
          reference_branch: definition.reference_branch,
          analysis_id: definition.analysis_id,
        }),
      });
    },
    onSuccess: async (_, variables) => {
      setEditingScopeKey(null);
      notify({ message: variables.action === 'delete' ? 'New Code definition deleted.' : 'New Code definition saved.' });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['new-code-definitions'] }), queryClient.invalidateQueries({ queryKey: ['new-code-project'] })]);
    },
  });
  const mainBranchMutation = useMutation({
    mutationFn: (mainBranch: string) =>
      apiJson<ProjectNewCodeSettings>(`${API_BASE}/v1/new-code/projects/${encodeURIComponent(projectKey)}/main-branch`, authToken, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ main_branch: canonicalBranch(mainBranch) }),
      }),
    onSuccess: async () => {
      setMainBranchDraft(null);
      notify({ message: 'Main branch saved.' });
      await queryClient.invalidateQueries({ queryKey: ['new-code-project', projectKey] });
    },
  });

  const resetForm = () => {
    setEditingScopeKey(null);
    setLevel('global');
    setMode('previous_version');
    setProjectKey(projects[0] ?? '');
    setBranch('');
    setNumberOfDays('30');
    setReferenceBranch('');
    setAnalysisId('');
  };
  const edit = (definition: NewCodeDefinition) => {
    setEditingScopeKey(definition.scope_key);
    setLevel(definition.level);
    setProjectKey(definition.project_key ?? projects[0] ?? '');
    setBranch(definition.branch ?? '');
    setMode(definition.mode);
    setNumberOfDays(String(definition.number_of_days ?? 30));
    setReferenceBranch(definition.reference_branch ?? '');
    setAnalysisId(definition.analysis_id ?? '');
  };
  const saveDefinition = () => {
    const canonical = canonicalBranch(branch);
    const definition: NewCodeDefinition = {
      scope_key: editingScopeKey ?? definitionScopeKey(level, projectKey, canonical),
      level,
      project_key: level === 'global' ? null : projectKey,
      branch: level === 'branch' ? canonical : null,
      mode,
      number_of_days: mode === 'number_of_days' ? Number(numberOfDays) : null,
      reference_branch: mode === 'reference_branch' ? canonicalBranch(referenceBranch) : null,
      analysis_id: mode === 'specific_analysis' ? analysisId.trim() : null,
      updated_at: '',
    };
    definitionMutation.mutate({ action: 'save', definition });
  };
  const validScope = level === 'global' || (!!projectKey && (level !== 'branch' || !!branch.trim()));
  const validModeValue = mode !== 'number_of_days' || (Number.isInteger(Number(numberOfDays)) && Number(numberOfDays) >= 1 && Number(numberOfDays) <= 90);
  const validReference = mode !== 'reference_branch' || !!referenceBranch.trim();
  const validAnalysis = mode !== 'specific_analysis' || !!analysisId.trim();
  const saving = definitionMutation.isPending || mainBranchMutation.isPending;
  const mutationError = definitionMutation.error ?? mainBranchMutation.error;
  const definitionColumns = useMemo<GridColumn<NewCodeDefinition>[]>(
    () => [
      { id: 'level', header: 'Level', value: (definition) => definition.level, width: 110, minWidth: 90 },
      { id: 'scope', header: 'Scope', value: (definition) => definition.scope_key, width: 290, minWidth: 150, className: 'new-code-grid-code' },
      { id: 'mode', header: 'Mode', value: (definition) => MODE_LABELS[definition.mode], width: 180, minWidth: 130 },
      { id: 'value', header: 'Value', value: definitionValue, width: 320, minWidth: 160, className: 'new-code-grid-code' },
      {
        id: 'actions',
        header: 'Actions',
        width: 170,
        minWidth: 150,
        render: (definition) => (
          <div className="table-actions">
            <Button disabled={saving} onClick={() => edit(definition)}>
              Edit
            </Button>
            <Button
              variant="danger"
              disabled={saving}
              onClick={() => {
                if (window.confirm(`Delete the New Code definition for ${definition.scope_key}?`)) {
                  definitionMutation.mutate({ action: 'delete', definition });
                }
              }}
            >
              Delete
            </Button>
          </div>
        ),
      },
    ],
    [saving],
  );

  return (
    <section aria-labelledby="new-code-settings-title">
      <h2 id="new-code-settings-title">New Code settings</h2>
      <p className="supporting-copy">Choose how CodeInsights selects a completed analysis snapshot as the baseline. This is an exact snapshot comparison, not an SCM blame-date calculation.</p>

      {mutationError && (
        <p className="feedback error" role="alert">
          {mutationError.message}
        </p>
      )}

      <section className="admin-subsection" aria-labelledby="main-branch-title">
        <h3 id="main-branch-title">Project main branch</h3>
        <p>The main branch prevents a branch or project definition from referencing itself.</p>
        <div className="form-grid compact new-code-main-branch-form">
          <ProjectPicker
            label="Project"
            options={projectOptions}
            value={projectKey}
            onChange={(value) => {
              setProjectKey(value);
              setMainBranchDraft(null);
            }}
            placeholder="Select a project"
          />
          <label>
            Main branch
            <TextBox
              value={mainBranchDraft ?? projectQuery.data?.main_branch ?? ''}
              placeholder="main"
              disabled={!projectKey || projectQuery.isLoading}
              onChange={(event) => setMainBranchDraft(event.target.value)}
            />
            {mainBranchDraft === null && projectQuery.data?.main_branch && (
              <span className="field-help">Detected automatically from the first analysis published on main or master. You can override it here.</span>
            )}
          </label>
        </div>
        {projectQuery.error && (
          <p className="feedback error" role="alert">
            {projectQuery.error.message}
          </p>
        )}
        <div className="action-row">
          <Button
            disabled={saving || !projectKey || !(mainBranchDraft ?? projectQuery.data?.main_branch ?? '').trim()}
            onClick={() => mainBranchMutation.mutate(mainBranchDraft ?? projectQuery.data?.main_branch ?? '')}
          >
            {mainBranchMutation.isPending ? 'Saving...' : 'Save main branch'}
          </Button>
        </div>
      </section>

      <section className="admin-subsection" aria-labelledby="definition-editor-title">
        <h3 id="definition-editor-title">{editingScopeKey ? 'Edit definition' : 'Add definition'}</h3>
        <p>Global settings allow Previous version and Number of days. Project settings also allow Reference branch. Specific analysis is available only for a branch.</p>
        <fieldset className="new-code-definition-form" disabled={saving}>
          <legend className="sr-only">New Code definition</legend>
          <div className="form-grid compact">
            <label>
              Level
              <ListBox
                value={level}
                disabled={!!editingScopeKey}
                onChange={(event) => {
                  const next = event.target.value as DefinitionLevel;
                  setLevel(next);
                  if (!MODES[next].includes(mode)) setMode(MODES[next][0]);
                }}
              >
                <option value="global">Global</option>
                <option value="project">Project</option>
                <option value="branch">Branch</option>
              </ListBox>
            </label>
            {level !== 'global' && <ProjectPicker label="Project" options={projectOptions} value={projectKey} onChange={setProjectKey} disabled={!!editingScopeKey} placeholder="Select a project" />}
            {level === 'branch' && (
              <label>
                Branch
                <TextBox value={displayBranchName(branch)} disabled={!!editingScopeKey} placeholder="feature/example" onChange={(event) => setBranch(event.target.value)} />
              </label>
            )}
            <label>
              Mode
              <ListBox value={mode} onChange={(event) => setMode(event.target.value as DefinitionMode)}>
                {MODES[level].map((candidate) => (
                  <option value={candidate} key={candidate}>
                    {MODE_LABELS[candidate]}
                  </option>
                ))}
              </ListBox>
            </label>
            {mode === 'number_of_days' && (
              <label>
                Number of days
                <TextBox
                  type="number"
                  min="1"
                  max="90"
                  required
                  aria-label="Number of days"
                  aria-describedby="new-code-days-help"
                  value={numberOfDays}
                  onChange={(event) => setNumberOfDays(event.target.value)}
                />
                <span className="field-help" id="new-code-days-help">
                  From 1 to 90 days. Without an old-enough completed analysis, New Code remains unavailable.
                </span>
              </label>
            )}
            {mode === 'reference_branch' && (
              <label>
                Reference branch
                <TextBox
                  required
                  aria-label="Reference branch"
                  aria-describedby="new-code-reference-help"
                  value={displayBranchName(referenceBranch)}
                  placeholder="main"
                  onChange={(event) => setReferenceBranch(event.target.value)}
                />
                <span className="field-help" id="new-code-reference-help">
                  Uses the latest completed snapshot on this branch, which may lag behind the live Git branch.
                </span>
              </label>
            )}
            {mode === 'specific_analysis' && (
              <label>
                Analysis ID
                <TextBox required value={analysisId} placeholder="Analysis UUID" onChange={(event) => setAnalysisId(event.target.value)} />
              </label>
            )}
          </div>
          <div className="action-row">
            <Button variant="primary" disabled={!validScope || !validModeValue || !validReference || !validAnalysis} onClick={saveDefinition}>
              {definitionMutation.isPending ? 'Saving...' : 'Save definition'}
            </Button>
            {editingScopeKey && <Button onClick={resetForm}>Cancel</Button>}
          </div>
        </fieldset>
      </section>

      <section className="admin-subsection" aria-labelledby="definitions-title">
        <h3 id="definitions-title">Configured definitions</h3>
        {definitionsQuery.isLoading ? (
          <p role="status">Loading New Code definitions...</p>
        ) : definitionsQuery.error ? (
          <p className="feedback error" role="alert">
            {definitionsQuery.error.message}
          </p>
        ) : !definitionsQuery.data?.length ? (
          <p className="empty-state" role="status">
            No New Code definition is configured.
          </p>
        ) : (
          <Grid ariaLabel="New Code definitions" rows={definitionsQuery.data} columns={definitionColumns} rowKey={(definition) => definition.scope_key} className="new-code-definitions-table" />
        )}
      </section>
    </section>
  );
}
