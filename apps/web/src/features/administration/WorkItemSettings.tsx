import { API_BASE } from '@/config/env';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { apiJson } from '@/services/api';
import { useToast } from '@/context/ToastProvider';
import ProjectPicker from '@/components/common/ProjectPicker';
import { Button, Checkbox, ListBox, TextBox } from '@/components/ui';
import { AUTO_SEVERITIES, parseMaxPerRun, type WorkItemAvailability, type WorkItemSettings as Settings } from '@/utils/work-items';

type ProjectWorkItems = { settings: Settings; availability: WorkItemAvailability };
type Draft = { work_item_type: string; area_path: string; iteration_path: string; auto_min_severity: string; auto_on_sla_breach: boolean; auto_max_per_run: string };

const toDraft = (settings: Settings): Draft => ({
  work_item_type: settings.work_item_type,
  area_path: settings.area_path ?? '',
  iteration_path: settings.iteration_path ?? '',
  auto_min_severity: settings.auto_min_severity ?? '',
  auto_on_sla_breach: settings.auto_on_sla_breach,
  auto_max_per_run: String(settings.auto_max_per_run),
});

function SettingsForm({ settings, saving, onSave }: { settings: Settings; saving: boolean; onSave: (body: Record<string, unknown>) => void }) {
  const [draft, setDraft] = useState<Draft>(() => toDraft(settings));
  const max = parseMaxPerRun(draft.auto_max_per_run);
  const valid = draft.work_item_type.trim().length > 0 && max !== undefined;
  const automatic = draft.auto_min_severity !== '' || draft.auto_on_sla_breach;
  const set = (patch: Partial<Draft>) => setDraft((current) => ({ ...current, ...patch }));
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (!valid || saving) return;
        onSave({
          version: settings.version,
          work_item_type: draft.work_item_type.trim(),
          area_path: draft.area_path.trim() || null,
          iteration_path: draft.iteration_path.trim() || null,
          auto_min_severity: draft.auto_min_severity || null,
          auto_on_sla_breach: draft.auto_on_sla_breach,
          auto_max_per_run: max,
        });
      }}
    >
      <fieldset className="admin-settings-form" disabled={saving}>
        <legend className="sr-only">Work item fields and rules</legend>
        <div className="form-grid compact">
          <label>
            Work item type
            <TextBox required maxLength={128} value={draft.work_item_type} onChange={(event) => set({ work_item_type: event.target.value })} />
            <span className="field-help">Must exist in the project&apos;s process: Bug for Agile, Scrum and CMMI, Issue for Basic.</span>
          </label>
          <label>
            Area path
            <TextBox maxLength={256} value={draft.area_path} placeholder="Project default" onChange={(event) => set({ area_path: event.target.value })} />
          </label>
          <label>
            Iteration path
            <TextBox maxLength={256} value={draft.iteration_path} placeholder="Project default" onChange={(event) => set({ iteration_path: event.target.value })} />
          </label>
        </div>
        <h4>Automatic creation</h4>
        <p className="field-help">Rules run on the main branch after each analysis and every hour. They only consider findings detected, or deadlines due, after the rules are enabled.</p>
        <div className="form-grid compact">
          <label>
            New findings with severity
            <ListBox value={draft.auto_min_severity} onChange={(event) => set({ auto_min_severity: event.target.value })}>
              {AUTO_SEVERITIES.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </ListBox>
          </label>
          <label>
            At most per run
            <TextBox inputMode="numeric" value={draft.auto_max_per_run} aria-invalid={max === undefined} onChange={(event) => set({ auto_max_per_run: event.target.value })} />
            <span className="field-help">From 1 to 100 work items per project; the rest waits for the next run.</span>
          </label>
        </div>
        <label className="integration-option">
          <Checkbox type="checkbox" checked={draft.auto_on_sla_breach} onChange={(event) => set({ auto_on_sla_breach: event.target.checked })} />
          <span>
            <strong>Findings that breach their SLA</strong>
            <small>Also open a work item when a main-branch finding passes its SLA deadline.</small>
          </span>
        </label>
        {automatic && settings.auto_since && <p className="supporting-copy">Rules enabled since {new Date(settings.auto_since).toLocaleString()}.</p>}
        <div className="action-row">
          <Button type="submit" variant="primary" disabled={!valid}>
            {saving ? 'Saving...' : 'Save work item settings'}
          </Button>
        </div>
      </fieldset>
    </form>
  );
}

export default function WorkItemSettings({ authToken, projects }: { authToken?: string; projects: string[] }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const projectOptions = useMemo(() => projects.map((key) => ({ key })), [projects]);
  const [pickedProject, setProjectKey] = useState('');
  // The project list loads asynchronously: default to its first entry once it arrives.
  const projectKey = pickedProject || projects[0] || '';
  const path = `${API_BASE}/v1/work-items/projects/${encodeURIComponent(projectKey)}`;
  const query = useQuery({
    queryKey: ['work-item-settings', projectKey],
    enabled: !!projectKey,
    queryFn: ({ signal }) => apiJson<ProjectWorkItems>(path, authToken, { signal }),
  });
  const save = useMutation({
    mutationFn: (body: Record<string, unknown>) => apiJson<Settings>(path, authToken, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
    onSuccess: async () => {
      notify({ message: 'Work item settings saved.' });
      await queryClient.invalidateQueries({ queryKey: ['work-item-settings', projectKey] });
    },
  });
  const data = query.data;

  return (
    <section aria-labelledby="work-item-settings-title">
      <h2 id="work-item-settings-title">Work item settings</h2>
      <p className="supporting-copy">
        A finding can open one Azure Boards work item, from its review panel or through the rules below. Resolving the finding adds a comment to the work item; closing the work item never changes the finding.
      </p>
      {save.error && (
        <p className="feedback error" role="alert">
          {save.error.message}
        </p>
      )}
      <section className="admin-subsection" aria-labelledby="work-item-project-title">
        <h3 id="work-item-project-title">Project</h3>
        <div className="form-grid compact admin-settings-project">
          <ProjectPicker label="Project" options={projectOptions} value={projectKey} onChange={setProjectKey} placeholder="Select a project" />
        </div>
        {query.isLoading && <p role="status">Loading work item settings...</p>}
        {query.error && (
          <p className="feedback error" role="alert">
            {query.error.message}
          </p>
        )}
        {data && (
          <>
            {data.availability.enabled ? (
              <p role="status">
                Work items go to <code>{data.availability.ado_project}</code> in {data.availability.organization_url}.
              </p>
            ) : (
              <p className="feedback warning" role="status">
                Work items are unavailable for this project: {data.availability.reason}.
              </p>
            )}
            <SettingsForm key={`${projectKey}-${data.settings.version}`} settings={data.settings} saving={save.isPending} onSave={(body) => save.mutate(body)} />
          </>
        )}
      </section>
    </section>
  );
}
