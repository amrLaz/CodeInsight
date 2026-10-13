import { Button, Grid, ListBox, TextBox } from '@/components/ui';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiJson, apiPage } from '@/services/api';
import type { RuleCard, ProjectSummary } from '@/types/api';
import { evidenceLink } from '@/utils/evidence-links';
import DialogCloseButton from '@/components/common/DialogCloseButton';
import ProjectPicker from '@/components/common/ProjectPicker';

export type RuleFilters = { search: string; category: string; severity: string; status: string; language: string };

type ProfileAssignment = { profile_id: string; profile_version: string; enforcement: string };
type ToggleRequest = { rule: RuleCard; enabled: boolean };

export default function Rules({
  apiBase,
  authToken,
  selectedProject,
  onProjectChange,
  summaries,
  canManageRules,
  filters,
  onFiltersChange,
  selectedRuleId,
  onSelectedRuleChange,
}: {
  apiBase: string;
  authToken?: string;
  selectedProject: string;
  onProjectChange: (project: string) => void;
  summaries: ProjectSummary[];
  canManageRules: boolean;
  filters: RuleFilters;
  onFiltersChange: (filters: RuleFilters) => void;
  selectedRuleId: string;
  onSelectedRuleChange: (ruleId: string) => void;
}) {
  const queryClient = useQueryClient();
  const [page, setPage] = useState({ key: '', cursor: '' });
  const [pendingToggle, setPendingToggle] = useState<ToggleRequest | null>(null);
  const toggleDialog = useRef<HTMLDialogElement>(null);
  const key = [selectedProject, filters.search, filters.category, filters.severity, filters.status, filters.language].join('|');
  const cursor = page.key === key ? page.cursor : '';
  const params = new URLSearchParams({
    project_key: selectedProject,
    search: filters.search,
    category: filters.category,
    severity: filters.severity,
    status: filters.status,
    language: filters.language,
    cursor,
  });
  const rulesQuery = useQuery({
    queryKey: ['rules', key, cursor],
    enabled: !!authToken,
    queryFn: ({ signal }) => apiPage<RuleCard>(`${apiBase}/v1/insights/rules?${params}`, authToken, { signal }),
  });
  const assignmentQuery = useQuery({
    queryKey: ['profile-assignment', selectedProject],
    enabled: !!authToken && !!selectedProject,
    queryFn: ({ signal }) => apiJson<ProfileAssignment | null>(`${apiBase}/v1/profiles/projects/${encodeURIComponent(selectedProject)}`, authToken, { signal }),
  });
  const rules = rulesQuery.data?.items ?? [];
  const projectOptions = useMemo(() => summaries.map((summary) => ({ key: summary.projectKey })), [summaries]);
  const selectedRule = useMemo(() => rules.find((rule) => (rule.key ?? `${rule.tool}/${rule.rule_id}`) === selectedRuleId) ?? rules[0] ?? null, [rules, selectedRuleId]);
  const languages = useMemo(() => [...new Set(rules.map((rule) => rule.language).filter((language) => language && language !== 'Not available'))].sort(), [rules]);

  useEffect(() => {
    if (rulesQuery.isSuccess && selectedRuleId && !rules.some((rule) => (rule.key ?? `${rule.tool}/${rule.rule_id}`) === selectedRuleId)) onSelectedRuleChange('');
  }, [rules, rulesQuery.isSuccess, selectedRuleId, onSelectedRuleChange]);

  useEffect(() => {
    const dialog = pendingToggle ? toggleDialog.current : null;
    if (!dialog) return;
    const previousFocus = document.activeElement;
    dialog.showModal();
    return () => {
      dialog.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [pendingToggle]);

  const activation = useMutation({
    mutationFn: ({ rule, enabled }: ToggleRequest) => {
      const assignment = assignmentQuery.data;
      if (!selectedProject || !assignment) throw new Error('Assign a versioned profile to this project first.');
      return apiJson(`${apiBase}/v1/profiles/projects/${encodeURIComponent(selectedProject)}/rule-activation`, authToken, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          tool: rule.tool,
          tool_version: rule.tool_version,
          rule_id: rule.rule_id,
          enabled,
          expected_profile_id: assignment.profile_id,
          expected_profile_version: assignment.profile_version,
        }),
      });
    },
    onSuccess: async () => {
      setPendingToggle(null);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['rules'] }),
        queryClient.invalidateQueries({ queryKey: ['profiles'] }),
        queryClient.invalidateQueries({ queryKey: ['profile-assignment', selectedProject] }),
        queryClient.invalidateQueries({ queryKey: ['profile-impact'] }),
        queryClient.invalidateQueries({ queryKey: ['audit'] }),
      ]);
    },
  });
  const update = (patch: Partial<RuleFilters>) => onFiltersChange({ ...filters, ...patch });
  const reset = () => onFiltersChange({ search: '', category: 'all', severity: 'all', status: 'all', language: 'all' });
  const profile = assignmentQuery.data;
  const canToggle = canManageRules && !!selectedProject && !!profile;

  return (
    <>
      <section className="rules-profile-summary">
        <div>
          <span className="section-kicker">Project profile</span>
          <h2>{selectedProject || 'All projects'}</h2>
          {selectedProject ? (
            assignmentQuery.isLoading ? (
              <p role="status">Loading assigned profile…</p>
            ) : profile ? (
              <p>
                <code>{profile.profile_id}</code> · version <code>{profile.profile_version}</code> · {profile.enforcement === 'blocking' ? 'blocking gate' : 'informative gate'}
              </p>
            ) : (
              <p>No versioned profile is assigned. Rules cannot be changed until a profile is assigned.</p>
            )
          ) : (
            <p>Select a project to inspect and change its executable rule profile.</p>
          )}
        </div>
        <ProjectPicker label="Project" options={projectOptions} value={selectedProject} clearable placeholder="All projects" onChange={onProjectChange} />
      </section>

      <details className="advanced-rules-panel">
        <summary>Advanced catalogue lookup</summary>
        <ProducerCatalogue api={apiBase} token={authToken} />
      </details>

      {activation.error && (
        <p className="feedback error" role="alert">
          {activation.error.message}
        </p>
      )}
      <section className="rules-screen" aria-labelledby="rules-catalogue-title">
        <header className="rules-heading">
          <div>
            <span className="section-kicker">Observed and configured rules</span>
            <h2 id="rules-catalogue-title">Rule catalogue</h2>
          </div>
          <p>{rulesQuery.isLoading ? 'Loading…' : `${rules.length} on this page`}</p>
        </header>
        <div className="rules-toolbar" aria-label="Rule filters">
          <label className="rule-search">
            <span>Search</span>
            <TextBox placeholder="Name or rule ID" value={filters.search} onChange={(event) => update({ search: event.target.value })} />
          </label>
          <label>
            <span>Category</span>
            <ListBox value={filters.category} onChange={(event) => update({ category: event.target.value })}>
              <option value="all">All</option>
              <option value="bug">Bugs</option>
              <option value="code_smell">Code smells</option>
              <option value="vulnerability">Vulnerabilities</option>
              <option value="security_hotspot">Security hotspots</option>
              <option value="secret">Secrets</option>
              <option value="dependency">Dependencies</option>
              <option value="code">Code</option>
              <option value="ioc">IOC</option>
              <option value="unknown">Unknown</option>
            </ListBox>
          </label>
          <label>
            <span>Severity</span>
            <ListBox value={filters.severity} onChange={(event) => update({ severity: event.target.value })}>
              <option value="all">All</option>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </ListBox>
          </label>
          <label>
            <span>Profile status</span>
            <ListBox value={filters.status} onChange={(event) => update({ status: event.target.value })}>
              <option value="all">All</option>
              <option value="enabled">Enabled</option>
              <option value="disabled">Disabled</option>
              <option value="unmanaged">Not managed</option>
            </ListBox>
          </label>
          <label>
            <span>Language</span>
            <ListBox value={filters.language} onChange={(event) => update({ language: event.target.value })}>
              <option value="all">All</option>
              {languages.map((language) => (
                <option key={language} value={language}>
                  {language}
                </option>
              ))}
            </ListBox>
          </label>
          <Button type="button" onClick={reset} disabled={!filters.search && [filters.category, filters.severity, filters.status, filters.language].every((value) => value === 'all')}>
            Reset
          </Button>
        </div>

        <div className="rules-workspace">
          <div className="rules-list" aria-label="Rules">
            {rulesQuery.isLoading && <p role="status">Loading rules…</p>}
            {rulesQuery.error && (
              <p className="feedback error" role="alert">
                {rulesQuery.error.message}.{' '}
                <Button type="button" onClick={() => void rulesQuery.refetch()}>
                  Retry
                </Button>
              </p>
            )}
            {!rulesQuery.isLoading && !rulesQuery.error && rules.length === 0 && <p className="empty-state">No rules match these filters.</p>}
            {rules.map((rule) => {
              const id = rule.key ?? `${rule.tool}/${rule.rule_id}`;
              return (
                <article key={id} className={selectedRule === rule ? 'rule-card selected' : 'rule-card'}>
                  <Button type="button" className="rule-title-button" aria-pressed={selectedRule === rule} onClick={() => onSelectedRuleChange(id)}>
                    <strong>{rule.title}</strong>
                    <span>
                      {rule.category} · {rule.severity} · {rule.language}
                    </span>
                    <small>
                      {rule.occurrences} occurrence{rule.occurrences !== 1 ? 's' : ''} · {rule.file_count} file{rule.file_count !== 1 ? 's' : ''}
                    </small>
                  </Button>
                  <span className={`chip rule-status ${rule.status}`}>{rule.status === 'enabled' ? 'Enabled' : rule.status === 'disabled' ? 'Disabled' : 'Not managed'}</span>
                </article>
              );
            })}
            <div className="rules-pagination">
              {cursor && (
                <Button type="button" onClick={() => setPage({ key, cursor: '' })}>
                  First page
                </Button>
              )}
              {rulesQuery.data?.next && (
                <Button type="button" onClick={() => setPage({ key, cursor: rulesQuery.data!.next! })}>
                  Next
                </Button>
              )}
            </div>
          </div>

          <aside className="rule-detail">
            {selectedRule ? (
              <>
                <span className="section-kicker">Rule detail</span>
                <h3>{selectedRule.title}</h3>
                <code>{selectedRule.rule_id}</code>
                <dl>
                  <div>
                    <dt>Severity</dt>
                    <dd>{selectedRule.severity}</dd>
                  </div>
                  <div>
                    <dt>Occurrences</dt>
                    <dd>{selectedRule.occurrences}</dd>
                  </div>
                  <div>
                    <dt>Files</dt>
                    <dd>{selectedRule.file_count}</dd>
                  </div>
                  <div>
                    <dt>Profile status</dt>
                    <dd>{selectedRule.status === 'enabled' ? 'Enabled' : selectedRule.status === 'disabled' ? 'Disabled' : 'Not managed'}</dd>
                  </div>
                </dl>
                {selectedRule.status === 'unmanaged' ? (
                  <p>This observed rule is not managed by the assigned executable profile.</p>
                ) : !selectedProject ? (
                  <p>Select one project before changing its profile.</p>
                ) : !canManageRules ? (
                  <p>Only an administrator can publish a new profile version.</p>
                ) : !profile ? (
                  <p>Assign a versioned profile before changing rules.</p>
                ) : (
                  <Button
                    type="button"
                    className={selectedRule.status === 'enabled' ? 'danger-link' : 'primary'}
                    disabled={!canToggle || activation.isPending}
                    onClick={() => setPendingToggle({ rule: selectedRule, enabled: selectedRule.status !== 'enabled' })}
                  >
                    {selectedRule.status === 'enabled' ? 'Disable in project profile' : 'Enable in project profile'}
                  </Button>
                )}
              </>
            ) : (
              <p>Select a rule to see its profile status and details.</p>
            )}
          </aside>
        </div>
      </section>

      {pendingToggle && profile && (
        <dialog
          ref={toggleDialog}
          className="config-modal"
          aria-labelledby="rule-activation-title"
          onCancel={(event) => {
            event.preventDefault();
            if (!activation.isPending) setPendingToggle(null);
          }}
        >
          <header className="config-modal-header">
            <div>
              <span className="section-kicker">New immutable profile version</span>
              <h3 id="rule-activation-title">{pendingToggle.enabled ? 'Enable' : 'Disable'} this rule?</h3>
            </div>
            <DialogCloseButton onClick={() => setPendingToggle(null)} disabled={activation.isPending} />
          </header>
          <div className="rule-activation-confirm">
            <code>
              {pendingToggle.rule.tool} · {pendingToggle.rule.rule_id}
            </code>
            <p>
              Project <strong>{selectedProject}</strong> currently uses{' '}
              <strong>
                {profile.profile_id} / {profile.profile_version}
              </strong>
              .
            </p>
            <p>CodeInsights will create and assign a new profile version. The exported analyzer configuration and future analyses will change; historical verdicts stay unchanged.</p>
            {activation.error && (
              <p className="feedback error" role="alert">
                {activation.error.message}
              </p>
            )}
            <div className="action-row">
              <Button type="button" onClick={() => setPendingToggle(null)} disabled={activation.isPending}>
                Cancel
              </Button>
              <Button type="button" className="primary" onClick={() => activation.mutate(pendingToggle)} disabled={activation.isPending}>
                {activation.isPending ? 'Publishing…' : `${pendingToggle.enabled ? 'Enable' : 'Disable'} and publish`}
              </Button>
            </div>
          </div>
        </dialog>
      )}
    </>
  );
}

type ProducerRule = {
  tool: string;
  tool_version: string;
  rule_id: string;
  language: string;
  category: string;
  parameters: Record<string, unknown>;
  documentation_url?: string;
  documentation_version?: string;
  remediation?: { model: string; duration_seconds: number; documentation_url: string };
};

function ProducerCatalogue({ api, token }: { api: string; token?: string }) {
  const [profile, setProfile] = useState('');
  const [version, setVersion] = useState('');
  const [search, setSearch] = useState('');
  const [language, setLanguage] = useState('');
  const [selection, setSelection] = useState({ profile: '', version: '', search: '', language: '', cursor: '' });
  const query = useQuery({
    queryKey: ['producer-catalogue', selection],
    enabled: !!selection.profile && !!selection.version,
    queryFn: ({ signal }) =>
      apiPage<ProducerRule>(
        `${api}/v1/profiles/${encodeURIComponent(selection.profile)}/versions/${encodeURIComponent(selection.version)}/catalogue?${new URLSearchParams({ search: selection.search, language: selection.language, cursor: selection.cursor })}`,
        token,
        { signal },
      ),
  });
  return (
    <section className="producer-catalogue">
      <form
        className="catalogue-form"
        onSubmit={(event) => {
          event.preventDefault();
          setSelection({ profile, version, search, language, cursor: '' });
        }}
      >
        <label>
          Profile
          <TextBox required value={profile} onChange={(event) => setProfile(event.target.value)} />
        </label>
        <label>
          Published version
          <TextBox required value={version} onChange={(event) => setVersion(event.target.value)} />
        </label>
        <label>
          Rule ID
          <TextBox value={search} onChange={(event) => setSearch(event.target.value)} />
        </label>
        <label>
          Exact language
          <TextBox value={language} onChange={(event) => setLanguage(event.target.value)} />
        </label>
        <Button type="submit">Browse catalogue</Button>
      </form>
      {query.isLoading && <p role="status">Loading catalogue…</p>}
      {query.error && <p role="alert">{query.error.message}</p>}
      {query.isSuccess && (
        <>
          <p>
            {selection.profile} / {selection.version} · {query.data.items.length} rules on this page.
          </p>
          {query.data.items.length > 0 && (
            <Grid<ProducerRule>
              ariaLabel="Producer rule catalogue"
              className="table-wrap"
              rows={query.data.items}
              rowKey={(rule) => `${rule.tool}/${rule.tool_version}/${rule.rule_id}`}
              columns={[
                { id: 'rule', header: 'Rule', rowHeader: true, value: (rule) => rule.rule_id, render: (rule) => <code>{rule.rule_id}</code> },
                { id: 'producer', header: 'Producer', value: (rule) => `${rule.tool} ${rule.tool_version}` },
                { id: 'classification', header: 'Classification', value: (rule) => `${rule.language} · ${rule.category}` },
                {
                  id: 'documentation',
                  header: 'Documentation',
                  render: (rule) => {
                    const link = evidenceLink(rule.documentation_url);
                    const remediationLink = evidenceLink(rule.remediation?.documentation_url);
                    return (
                      <>
                        {link ? (
                          <a href={link} target="_blank" rel="noreferrer">
                            Producer documentation
                          </a>
                        ) : (
                          'Not available'
                        )}
                        {remediationLink && (
                          <>
                            <br />
                            <a href={remediationLink} target="_blank" rel="noreferrer">
                              Remediation model
                            </a>
                          </>
                        )}
                        <details>
                          <summary>Parameters</summary>
                          <pre>{JSON.stringify(rule.parameters, null, 2)}</pre>
                        </details>
                      </>
                    );
                  },
                },
              ]}
            />
          )}
          <div className="action-row">
            {selection.cursor && (
              <Button type="button" onClick={() => setSelection({ ...selection, cursor: '' })}>
                First page
              </Button>
            )}
            {query.data.next && (
              <Button type="button" onClick={() => setSelection({ ...selection, cursor: query.data!.next! })}>
                Next
              </Button>
            )}
          </div>
        </>
      )}
    </section>
  );
}
