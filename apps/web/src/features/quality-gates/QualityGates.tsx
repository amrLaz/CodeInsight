import { Button, Grid, ListBox, TextBox } from '@/components/ui';
import InfoHint from '@/components/common/InfoHint';
import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { GateLibraryItem, ProjectSummary } from '@/types/api';
import { analysisGateClass, analysisGateLabel } from '@/utils/metric-display';
import { useToast } from '@/context/ToastProvider';

type Props = {
  gateLibrary: GateLibraryItem[];
  gateAssignments: Record<string, string>;
  summaries: ProjectSummary[];
  canAccessAdministration: boolean;
  apiBase: string;
  apiFetch: (url: string, init?: RequestInit) => Promise<Response>;
  selectedGateId: string;
  onSelectedGateChange: (gateId: string) => void;
};
type GateForm = { name: string } & Record<
  'min_maintainability_score' | 'min_security_score' | 'min_reliability_score' | 'max_vulnerabilities' | 'max_security_hotspots' | 'max_bugs' | 'max_code_smells',
  number | ''
>;
type GateAction = { kind: 'create'; form: GateForm } | { kind: 'save'; gateId: string; form: GateForm } | { kind: 'delete'; gateId: string } | { kind: 'assign'; projectKey: string; gateId: string };

const emptyForm = (gate?: GateLibraryItem | null): GateForm => ({
  name: '',
  min_maintainability_score: gate?.min_maintainability_score ?? 50,
  min_security_score: gate?.min_security_score ?? 50,
  min_reliability_score: gate?.min_reliability_score ?? 50,
  max_vulnerabilities: gate?.max_vulnerabilities ?? 10,
  max_security_hotspots: gate?.max_security_hotspots ?? 10,
  max_bugs: gate?.max_bugs ?? 10,
  max_code_smells: gate?.max_code_smells ?? 100,
});

const formFromGate = (gate: GateLibraryItem): GateForm => ({
  name: gate.name,
  min_maintainability_score: gate.min_maintainability_score,
  min_security_score: gate.min_security_score,
  min_reliability_score: gate.min_reliability_score ?? 50,
  max_vulnerabilities: gate.max_vulnerabilities,
  max_security_hotspots: gate.max_security_hotspots,
  max_bugs: gate.max_bugs ?? 10,
  max_code_smells: gate.max_code_smells,
});

export default function QualityGates({ gateLibrary, gateAssignments, summaries, canAccessAdministration, apiBase, apiFetch, selectedGateId, onSelectedGateChange }: Props) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const selectedGate = gateLibrary.find((gate) => gate.gate_id === selectedGateId) ?? gateLibrary.find((gate) => gate.builtIn) ?? gateLibrary[0] ?? null;
  const [draft, setDraft] = useState<{ gateId: string; form: GateForm } | null>(null);
  const [creating, setCreating] = useState(false);
  const [gateFormError, setGateFormError] = useState<string | null>(null);
  const currentForm = creating ? (draft?.form ?? emptyForm(selectedGate)) : draft && draft.gateId === selectedGate?.gate_id ? draft.form : selectedGate ? formFromGate(selectedGate) : emptyForm();
  const originalForm = selectedGate ? formFromGate(selectedGate) : emptyForm();
  const dirty = creating || (!!draft && JSON.stringify(currentForm) !== JSON.stringify(originalForm));

  useEffect(() => {
    if (selectedGateId && !gateLibrary.some((gate) => gate.gate_id === selectedGateId)) onSelectedGateChange('');
  }, [gateLibrary, selectedGateId, onSelectedGateChange]);

  const mutation = useMutation({
    mutationFn: async (action: GateAction) => {
      if (!canAccessAdministration) throw new Error('Administrator permission is required.');
      let path = '/v1/gates';
      let method = 'POST';
      let body: unknown;
      if (action.kind === 'create') body = action.form;
      else if (action.kind === 'assign') {
        path += `/assignments/${encodeURIComponent(action.projectKey)}`;
        const defaultId = gateLibrary.find((gate) => gate.builtIn)?.gate_id ?? 'default';
        method = action.gateId === defaultId ? 'DELETE' : 'PUT';
        if (method === 'PUT') body = { gate_id: action.gateId };
      } else {
        path += `/${encodeURIComponent(action.gateId)}`;
        method = action.kind === 'delete' ? 'DELETE' : 'PUT';
        if (action.kind === 'save') body = action.form;
      }
      const response = await apiFetch(`${apiBase}${path}`, { method, ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
      if (!response.ok) throw new Error(`Gate operation failed (${response.status}).`);
      return action.kind === 'create' || action.kind === 'save' ? ((await response.json()) as GateLibraryItem) : null;
    },
    onSuccess: async (gate, action) => {
      if (gate) onSelectedGateChange(gate.gate_id);
      if (action.kind === 'delete') onSelectedGateChange('');
      setDraft(null);
      setCreating(false);
      notify({
        message: action.kind === 'assign' ? 'Project gate assignment saved.' : action.kind === 'delete' ? 'Gate deleted.' : `${action.kind === 'create' ? 'Created' : 'Saved'} ${gate?.name}.`,
      });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['gates'] }), queryClient.invalidateQueries({ queryKey: ['gate-assignments'] })]);
    },
    onError: (error: Error) => setGateFormError(error.message),
  });
  const saving = mutation.isPending;
  const savingAssignment = saving && mutation.variables?.kind === 'assign' ? mutation.variables.projectKey : null;
  const run = (action: GateAction) => {
    if (saving || !canAccessAdministration) return;
    setGateFormError(null);
    mutation.mutate(action);
  };
  const cancelDraft = () => {
    setDraft(null);
    setCreating(false);
    setGateFormError(null);
  };
  const discardDraft = () => {
    if (dirty && !window.confirm('Discard the unsaved Quality Gate changes?')) return;
    cancelDraft();
  };
  const selectGate = (gateId: string) => {
    if (dirty && !window.confirm('Discard the unsaved Quality Gate changes?')) return;
    cancelDraft();
    onSelectedGateChange(gateId);
  };
  const updateField = (field: keyof GateForm, value: string) => {
    setDraft({ gateId: creating ? '__new__' : (selectedGate?.gate_id ?? ''), form: { ...currentForm, [field]: field === 'name' || value === '' ? value : Number(value) } });
  };
  const conditionGroups = useMemo(
    () => [
      {
        title: 'Legacy score compatibility',
        description: 'These Overall Code score thresholds are retained only for historical profiles. MQR profiles use immutable A–E New Code conditions.',
        fields: [
          ['min_security_score', 'Legacy security score', 100],
          ['min_maintainability_score', 'Legacy maintainability score', 100],
          ['min_reliability_score', 'Legacy reliability score', 100],
        ] as const,
      },
      {
        title: 'Maximum findings',
        description: 'A count above its limit fails the analysis.',
        fields: [
          ['max_vulnerabilities', 'Vulnerabilities', 10000],
          ['max_security_hotspots', 'Security hotspots', 10000],
          ['max_bugs', 'Bugs', 10000],
          ['max_code_smells', 'Code smells', 100000],
        ] as const,
      },
    ],
    [],
  );

  return (
    <section className="two-pane-screen gates-screen">
      <aside className="gates-sidebar">
        <div className="gates-head">
          <div>
            <h2>Gate library</h2>
            <span>{gateLibrary.length} configured</span>
          </div>
          {canAccessAdministration && (
            <Button
              type="button"
              onClick={() => {
                if (dirty && !window.confirm('Discard the unsaved Quality Gate changes?')) return;
                setCreating(true);
                setDraft({ gateId: '__new__', form: emptyForm(selectedGate) });
              }}
              disabled={saving}
            >
              New gate
            </Button>
          )}
        </div>
        <ul>
          {gateLibrary.map((gate) => (
            <li key={gate.gate_id} className={!creating && selectedGate?.gate_id === gate.gate_id ? 'active' : ''}>
              <Button type="button" disabled={saving} aria-current={!creating && selectedGate?.gate_id === gate.gate_id ? 'true' : undefined} onClick={() => selectGate(gate.gate_id)}>
                <span className="gate-name">{gate.name}</span>
                <span className="gate-meta">
                  {gate.projectCount} project{gate.projectCount !== 1 ? 's' : ''}
                  {gate.builtIn && <span className="gate-default-badge">Default</span>}
                </span>
              </Button>
            </li>
          ))}
        </ul>
      </aside>

      <div className="gates-main">
        {(selectedGate || creating) && (
          <form
            className="gate-editor"
            onSubmit={(event) => {
              event.preventDefault();
              if (creating) run({ kind: 'create', form: currentForm });
              else if (selectedGate) run({ kind: 'save', gateId: selectedGate.gate_id, form: currentForm });
            }}
            onInvalid={(event) => setGateFormError((event.target as HTMLInputElement).validationMessage)}
            onChange={() => setGateFormError(null)}
          >
            <header className="gate-editor-header">
              <div>
                <div className="gate-editor-kicker">{creating ? 'New Quality Gate' : 'Quality Gate definition'}</div>
                <label htmlFor="gate-name">Name</label>
                <TextBox
                  id="gate-name"
                  className="gate-title-input"
                  readOnly={saving || !canAccessAdministration}
                  required
                  maxLength={255}
                  pattern=".*\S.*"
                  value={currentForm.name}
                  placeholder="For example: Web applications"
                  onChange={(event) => updateField('name', event.target.value)}
                />
              </div>
              <div className="gate-editor-state">
                {!creating && selectedGate?.builtIn && <span className="gate-default-badge">Default</span>}
                {dirty && <span className="chip chip-warning">Unsaved changes</span>}
                <InfoHint>Conditions apply to Overall Code. Historical analysis verdicts never change.</InfoHint>
              </div>
            </header>
            {creating && selectedGate && (
              <p className="gate-copy-note">
                Thresholds copied from <strong>{selectedGate.name}</strong>. Name the gate, review the values, then create it.
              </p>
            )}
            {gateFormError && (
              <p id="gate-form-error" className="feedback error" role="alert">
                {gateFormError}
              </p>
            )}
            <fieldset disabled={saving || !canAccessAdministration} className="gate-condition-groups">
              <legend className="sr-only">Quality Gate conditions</legend>
              {conditionGroups.map((group) => (
                <section className="gate-condition-group" key={group.title}>
                  <header>
                    <h3>{group.title}</h3>
                    <p>{group.description}</p>
                  </header>
                  <div className="gate-condition-grid">
                    {group.fields.map(([field, label, max]) => (
                      <label key={field}>
                        <span>{label}</span>
                        <span className="number-control">
                          <TextBox
                            type="number"
                            value={currentForm[field]}
                            required
                            min={0}
                            max={max}
                            step={1}
                            aria-describedby={gateFormError ? 'gate-form-error' : undefined}
                            onChange={(event) => updateField(field, event.target.value)}
                          />
                          <small>{max === 100 ? '/ 100' : 'maximum'}</small>
                        </span>
                      </label>
                    ))}
                  </div>
                </section>
              ))}
            </fieldset>
            {canAccessAdministration && (
              <footer className="gate-editor-actions">
                {!creating && selectedGate && !selectedGate.builtIn && (
                  <Button
                    type="button"
                    className="danger-link"
                    disabled={saving}
                    onClick={() => {
                      if (window.confirm(`Delete gate "${selectedGate.name}"? ${selectedGate.projectCount} assigned project(s) will revert to the default gate.`))
                        run({ kind: 'delete', gateId: selectedGate.gate_id });
                    }}
                  >
                    Delete gate
                  </Button>
                )}
                <span className="gate-actions-spacer" />
                {dirty && (
                  <Button type="button" onClick={discardDraft} disabled={saving}>
                    Cancel
                  </Button>
                )}
                <Button type="submit" className="primary" disabled={saving || !dirty}>
                  {saving ? 'Saving…' : creating ? 'Create gate' : 'Save changes'}
                </Button>
              </footer>
            )}
          </form>
        )}

        <section className="gate-assignments">
          <header>
            <div>
              <h2>Project assignments</h2>
              <p>Projects without an explicit assignment use the default gate.</p>
            </div>
            <InfoHint>Changing an assignment affects future analyses only.</InfoHint>
          </header>
          <Grid<ProjectSummary>
            ariaLabel="Project gate assignments"
            className="table-wrap"
            rows={summaries}
            rowKey={(summary) => summary.projectKey}
            columns={[
              { id: 'project', header: 'Project', rowHeader: true, value: (summary) => summary.projectKey, render: (summary) => <code>{summary.projectKey}</code> },
              {
                id: 'gate',
                header: 'Assigned gate',
                render: (summary) => {
                  const assignedId = gateAssignments[summary.projectKey] ?? gateLibrary.find((gate) => gate.builtIn)?.gate_id ?? 'default';
                  return (
                    <>
                      <ListBox
                        aria-label={`Assigned gate for ${summary.projectKey}`}
                        value={assignedId}
                        disabled={saving || !canAccessAdministration}
                        onChange={(event) => run({ kind: 'assign', projectKey: summary.projectKey, gateId: event.target.value })}
                      >
                        {gateLibrary.map((gate) => (
                          <option key={gate.gate_id} value={gate.gate_id}>
                            {gate.name}
                            {gate.builtIn ? ' (default)' : ''}
                          </option>
                        ))}
                      </ListBox>
                      {savingAssignment === summary.projectKey && (
                        <span className="saving-indicator" role="status">
                          Saving…
                        </span>
                      )}
                    </>
                  );
                },
              },
              {
                id: 'result',
                header: 'Last result',
                value: (summary) => analysisGateLabel(summary.latest.quality_gate, summary.latest.completeness),
                render: (summary) => (
                  <span className={`chip ${analysisGateClass(summary.latest.quality_gate, summary.latest.completeness)}`}>
                    {analysisGateLabel(summary.latest.quality_gate, summary.latest.completeness)}
                  </span>
                ),
              },
            ]}
          />
        </section>
      </div>
    </section>
  );
}
