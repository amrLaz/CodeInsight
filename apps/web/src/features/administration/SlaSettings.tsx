import { API_BASE } from '@/config/env';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { apiFetch, apiJson } from '@/services/api';
import { displayBranchName } from '@/utils/branch-selection';
import { useToast } from '@/context/ToastProvider';
import ProjectPicker from '@/components/common/ProjectPicker';
import { Button, Grid, TextBox, type GridColumn } from '@/components/ui';
import { parseSlaDays, policyDays, SLA_MAX_DAYS, SLA_SEVERITIES, SLA_STATUSES, type SlaPolicy, type SlaSeverity, type SlaStatus } from '@/utils/sla';

type ProjectSla = {
  project_key: string;
  main_branch: string | null;
  override: SlaPolicy | null;
  effective: SlaPolicy | null;
  counts: Record<SlaStatus, number>;
};
type Draft = Record<SlaSeverity, string>;
type CountRow = { status: SlaStatus; count: number };

const API = `${API_BASE}/v1/sla`;
const json = (method: string, body: unknown) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
const toDraft = (policy: SlaPolicy | null | undefined): Draft =>
  Object.fromEntries(SLA_SEVERITIES.map(({ value }) => [value, String(policyDays(policy, value) ?? '')])) as Draft;

function PolicyForm({ policy, fallback, saving, submitLabel, onSave }: { policy: SlaPolicy | null; fallback?: SlaPolicy | null; saving: boolean; submitLabel: string; onSave: (body: Record<string, number | null>) => void }) {
  const [draft, setDraft] = useState<Draft>(() => toDraft(policy ?? fallback));
  const parsed = SLA_SEVERITIES.map(({ value }) => [value, parseSlaDays(draft[value])] as const);
  const valid = parsed.every(([, days]) => days !== undefined);
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (valid && !saving) onSave({ version: policy?.version ?? 0, ...Object.fromEntries(parsed.map(([severity, days]) => [`${severity}_days`, days ?? null])) });
      }}
    >
      <fieldset className="admin-settings-form" disabled={saving}>
        <legend className="sr-only">Remediation delays</legend>
        <div className="form-grid compact admin-settings-days">
          {SLA_SEVERITIES.map(({ value, label }) => (
            <label key={value}>
              {label} (days)
              <TextBox
                inputMode="numeric"
                value={draft[value]}
                placeholder="No deadline"
                aria-invalid={parseSlaDays(draft[value]) === undefined}
                onChange={(event) => setDraft((current) => ({ ...current, [value]: event.target.value }))}
              />
            </label>
          ))}
        </div>
        <p className="field-help">Whole days from 1 to {SLA_MAX_DAYS}. Leave a field empty when that severity has no deadline.</p>
        <div className="action-row">
          <Button type="submit" variant="primary" disabled={!valid}>
            {saving ? 'Saving...' : submitLabel}
          </Button>
        </div>
      </fieldset>
    </form>
  );
}

function PolicyMeta({ policy }: { policy: SlaPolicy }) {
  return (
    <p className="supporting-copy">
      Version {policy.version}, updated by {policy.updated_by || 'unknown'} on {new Date(policy.updated_at).toLocaleString()}.
    </p>
  );
}

export default function SlaSettings({ authToken, projects }: { authToken?: string; projects: string[] }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const projectOptions = useMemo(() => projects.map((key) => ({ key })), [projects]);
  const [pickedProject, setProjectKey] = useState('');
  // The project list loads asynchronously: default to its first entry once it arrives.
  const projectKey = pickedProject || projects[0] || '';
  const globalQuery = useQuery({
    queryKey: ['sla-policy'],
    queryFn: ({ signal }) => apiJson<SlaPolicy | null>(`${API}/policy`, authToken, { signal }),
  });
  const projectQuery = useQuery({
    queryKey: ['sla-project', projectKey],
    enabled: !!projectKey,
    queryFn: ({ signal }) => apiJson<ProjectSla>(`${API}/projects/${encodeURIComponent(projectKey)}`, authToken, { signal }),
  });
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['sla-policy'] }), queryClient.invalidateQueries({ queryKey: ['sla-project'] }), queryClient.invalidateQueries({ queryKey: ['findings'] })]);
  const globalMutation = useMutation({
    mutationFn: (body: Record<string, number | null>) => apiJson<SlaPolicy>(`${API}/policy`, authToken, json('PUT', body)),
    onSuccess: async () => {
      notify({ message: 'Global SLA policy saved. Deadlines are being recomputed.' });
      await refresh();
    },
  });
  const overrideMutation = useMutation({
    mutationFn: async ({ body }: { body: Record<string, number | null> | null }) => {
      const path = `${API}/projects/${encodeURIComponent(projectKey)}`;
      if (body === null) await apiFetch(path, authToken, { method: 'DELETE' });
      else await apiJson<SlaPolicy>(path, authToken, json('PUT', body));
    },
    onSuccess: async (_, { body }) => {
      notify({ message: body === null ? 'Project override removed. The global policy applies again.' : 'Project SLA override saved. Deadlines are being recomputed.' });
      await refresh();
    },
  });
  const saving = globalMutation.isPending || overrideMutation.isPending;
  const mutationError = globalMutation.error ?? overrideMutation.error;
  const countColumns = useMemo<GridColumn<CountRow>[]>(
    () => [
      { id: 'status', header: 'SLA status', value: (row) => SLA_STATUSES[row.status], width: 240, minWidth: 160, rowHeader: true },
      { id: 'count', header: 'Findings', value: (row) => row.count, width: 120, minWidth: 90 },
    ],
    [],
  );
  const project = projectQuery.data;
  const countRows = project ? (Object.keys(SLA_STATUSES) as SlaStatus[]).map((status) => ({ status, count: project.counts[status] ?? 0 })) : [];

  return (
    <section aria-labelledby="sla-settings-title">
      <h2 id="sla-settings-title">SLA settings</h2>
      <p className="supporting-copy">
        Remediation deadlines start at a finding&apos;s first detection and only apply to its project&apos;s main branch. An active exception suspends the deadline; a resolved finding closes it. A finding whose detection date or severity is unknown stays indeterminate.
      </p>

      {mutationError && (
        <p className="feedback error" role="alert">
          {mutationError.message}
        </p>
      )}

      <section className="admin-subsection" aria-labelledby="sla-global-title">
        <h3 id="sla-global-title">Global policy</h3>
        {globalQuery.isLoading ? (
          <p role="status">Loading SLA policy...</p>
        ) : globalQuery.error ? (
          <p className="feedback error" role="alert">
            {globalQuery.error.message}
          </p>
        ) : (
          <>
            {globalQuery.data ? <PolicyMeta policy={globalQuery.data} /> : <p role="status">No global policy is configured: deadlines are not configured for projects without an override.</p>}
            <PolicyForm key={`global-${globalQuery.data?.version ?? 0}`} policy={globalQuery.data ?? null} saving={saving} submitLabel="Save global policy" onSave={(body) => globalMutation.mutate(body)} />
          </>
        )}
      </section>

      <section className="admin-subsection" aria-labelledby="sla-project-title">
        <h3 id="sla-project-title">Project override</h3>
        <p>An override replaces the global policy entirely for one project.</p>
        <div className="form-grid compact admin-settings-project">
          <ProjectPicker label="Project" options={projectOptions} value={projectKey} onChange={setProjectKey} placeholder="Select a project" />
        </div>
        {projectQuery.isLoading && <p role="status">Loading project SLA...</p>}
        {projectQuery.error && (
          <p className="feedback error" role="alert">
            {projectQuery.error.message}
          </p>
        )}
        {project && (
          <>
            <p>
              {project.main_branch ? (
                <>
                  Main branch <code>{displayBranchName(project.main_branch)}</code>.{' '}
                </>
              ) : (
                <>No main branch is configured for this project: its findings stay indeterminate until one is set in New Code settings. </>
              )}
              {project.override ? 'This project uses its own override.' : project.effective ? 'This project follows the global policy.' : 'No policy applies to this project.'}
            </p>
            {project.override && <PolicyMeta policy={project.override} />}
            <PolicyForm
              key={`${project.project_key}-${project.override?.version ?? 0}`}
              policy={project.override}
              fallback={project.effective}
              saving={saving}
              submitLabel={project.override ? 'Save override' : 'Create override'}
              onSave={(body) => overrideMutation.mutate({ body })}
            />
            {project.override && (
              <div className="action-row">
                <Button
                  variant="danger"
                  disabled={saving}
                  onClick={() => {
                    if (window.confirm(`Remove the SLA override of ${project.project_key}? The global policy will apply again.`)) overrideMutation.mutate({ body: null });
                  }}
                >
                  Remove override
                </Button>
              </div>
            )}
            <Grid ariaLabel={`SLA status of ${project.project_key} findings`} caption="Tracked findings by SLA status, all branches" rows={countRows} columns={countColumns} rowKey={(row) => row.status} />
          </>
        )}
      </section>
    </section>
  );
}
