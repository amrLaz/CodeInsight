import { Button, ListBox, TextArea, TextBox } from '@/components/ui';
import InfoHint from '@/components/common/InfoHint';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiJson, apiPage } from '@/services/api';
import { useToast } from '@/context/ToastProvider';

type Impact = {
  current_profile: string | null;
  current_version: string | null;
  target_profile: string;
  target_version: string;
  changes: {
    added_rules: string[];
    removed_rules: string[];
    changed_rules: string[];
    before_configuration_hash: string;
    after_configuration_hash: string;
    before_conditions: unknown[];
    after_conditions: unknown[];
    before_required_tools: string[];
    after_required_tools: string[];
  };
};

export default function AnalysisProfiles({ api, token, project, admin }: { api: string; token?: string; project: string; admin: boolean }) {
  const client = useQueryClient();
  const notify = useToast();
  const [cursor, setCursor] = useState('');
  const [definition, setDefinition] = useState('');
  const [profile, setProfile] = useState('');
  const [version, setVersion] = useState('');
  const [enforcement, setEnforcement] = useState('informative');
  const [preview, setPreview] = useState({ project: '', profile: '', version: '', enforcement: '' });
  const impact = useQuery({
    queryKey: ['profile-impact', preview],
    enabled: admin && !!preview.project && preview.project === project,
    queryFn: ({ signal }) =>
      apiJson<Impact>(`${api}/v1/profiles/projects/${encodeURIComponent(preview.project)}/impact?${new URLSearchParams({ profileId: preview.profile, version: preview.version })}`, token, { signal }),
  });
  const previewMatches = preview.project === project && preview.profile === profile && preview.version === version && preview.enforcement === enforcement && impact.isSuccess;
  const configuration = useMutation({
    mutationFn: async () => {
      const data = await apiJson<unknown>(`${api}/v1/profiles/projects/${encodeURIComponent(project)}/configuration`, token);
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = 'codeinsights-profile.json';
      link.click();
      URL.revokeObjectURL(url);
    },
  });
  const [exception, setException] = useState({ tool: '', rule_id: '', path: '', reason: '', expires_at: '' });
  const catalogue = useQuery({ queryKey: ['profiles', cursor], queryFn: ({ signal }) => apiPage<unknown>(`${api}/v1/profiles?cursor=${encodeURIComponent(cursor)}`, token, { signal }) });
  const assignment = useQuery({
    queryKey: ['profile-assignment', project],
    enabled: !!project,
    queryFn: ({ signal }) => apiJson<{ profile_id: string; profile_version: string; enforcement: string } | null>(`${api}/v1/profiles/projects/${encodeURIComponent(project)}`, token, { signal }),
  });
  const exceptions = useQuery({
    queryKey: ['profile-exceptions', project],
    enabled: !!project,
    queryFn: ({ signal }) =>
      apiJson<{ id: string; tool: string; rule_id: string; path?: string; reason: string; expires_at: string }[]>(`${api}/v1/profiles/projects/${encodeURIComponent(project)}/exceptions`, token, {
        signal,
      }),
  });
  const save = useMutation({
    mutationFn: ({ path, body, method }: { path: string; body: unknown; method: string }) =>
      apiJson(`${api}/v1/profiles${path}`, token, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
    onSuccess: async () => {
      notify({ message: 'Changes saved.' });
      await Promise.all([
        client.invalidateQueries({ queryKey: ['profiles'] }),
        client.invalidateQueries({ queryKey: ['profile-assignment'] }),
        client.invalidateQueries({ queryKey: ['profile-exceptions'] }),
      ]);
    },
  });
  const [parseError, setParseError] = useState('');
  return (
    <section className="panel analysis-profiles">
      <h2>
        Versioned analysis profiles{' '}
        <InfoHint>
          New versions are immutable; analyses retain their configuration. Historical global suppressions do not apply to these profiles. Each condition can specify <code>scope: "global"</code> or{' '}
          <code>scope: "new_code"</code>. Without a scope, it applies to overall code. New Code requires an available Git comparison; coverage with no changed executable lines is not applicable. The
          producer catalogue is independent of occurrences. Published native configurations can be exported for existing analysis steps; their execution and effective fingerprint are checked when
          reports are published. To return to an older version, enter that published version and review the differences. Past analyses retain their verdict. This changes configuration only: it does
          not rerun analyses or predict a verdict.
        </InfoHint>
      </h2>
      {[catalogue.error, assignment.error, exceptions.error, save.error, impact.error, configuration.error].filter(Boolean).map((error, index) => (
        <p role="alert" key={index}>
          {error!.message}
        </p>
      ))}
      {catalogue.isLoading && <p role="status">Loading profiles…</p>}
      <div className="action-row">
        {cursor && (
          <Button type="button" onClick={() => setCursor('')}>
            First page
          </Button>
        )}
        {catalogue.data?.next && (
          <Button type="button" onClick={() => setCursor(catalogue.data!.next!)}>
            Next
          </Button>
        )}
      </div>
      <details>
        <summary>Catalogue and conditions</summary>
        <pre>{JSON.stringify(catalogue.data?.items ?? [], null, 2)}</pre>
      </details>
      {assignment.data && (
        <p>
          Project: {assignment.data.profile_id} / {assignment.data.profile_version} · {assignment.data.enforcement}
        </p>
      )}
      {assignment.data && (
        <Button type="button" disabled={configuration.isPending} onClick={() => configuration.mutate()}>
          Export assigned CI configuration
        </Button>
      )}
      {project && assignment.isSuccess && !assignment.data && <p>No P1 profile is assigned to this project.</p>}
      {admin && (
        <>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              try {
                const body: unknown = JSON.parse(definition);
                setParseError('');
                save.mutate({ path: '', body, method: 'POST' });
              } catch {
                setParseError('The profile must be valid JSON.');
              }
            }}
          >
            <label htmlFor="profile-json">New version (JSON definition)</label>
            <TextArea id="profile-json" value={definition} onChange={(event) => setDefinition(event.target.value)} required rows={8} />
            {parseError && <p role="alert">{parseError}</p>}
            <Button type="submit" disabled={save.isPending}>
              Create version
            </Button>
          </form>
          {project && (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                if (previewMatches) {
                  save.mutate({ path: `/projects/${encodeURIComponent(project)}`, method: 'PUT', body: { profile_id: profile, profile_version: version, enforcement } });
                  setPreview({ project: '', profile: '', version: '', enforcement: '' });
                }
              }}
            >
              <label htmlFor="assigned-profile">Profile ID</label>
              <TextBox id="assigned-profile" required value={profile} onChange={(event) => setProfile(event.target.value)} />
              <label htmlFor="assigned-version">Version</label>
              <TextBox id="assigned-version" required value={version} onChange={(event) => setVersion(event.target.value)} />
              <label htmlFor="enforcement">Gate CI</label>
              <ListBox id="enforcement" value={enforcement} onChange={(event) => setEnforcement(event.target.value)}>
                <option value="informative">Informational</option>
                <option value="blocking">Blocking</option>
              </ListBox>
              <Button type="button" disabled={!profile.trim() || !version.trim() || impact.isFetching || save.isPending} onClick={() => setPreview({ project, profile, version, enforcement })}>
                Preview impact and differences
              </Button>
              {impact.isFetching && <p role="status">Loading impact…</p>}
              {previewMatches && impact.data && (
                <section aria-label="Assignment impact">
                  <h3>Differences before assignment</h3>
                  <p>
                    {impact.data.current_profile ?? 'No profile'} / {impact.data.current_version ?? '—'} → {impact.data.target_profile} / {impact.data.target_version} ·{' '}
                    {enforcement === 'blocking' ? 'blocking' : 'informational'} gate.
                  </p>
                  <p>
                    {impact.data.changes.added_rules.length} rules added · {impact.data.changes.removed_rules.length} removed · {impact.data.changes.changed_rules.length} changed.
                  </p>
                  <details>
                    <summary>Compared rules, conditions, tools, and fingerprints</summary>
                    <pre>{JSON.stringify(impact.data.changes, null, 2)}</pre>
                  </details>
                </section>
              )}
              <Button type="submit" disabled={save.isPending || !previewMatches}>
                Assign this version to the project
              </Button>
            </form>
          )}
          {project && (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate({
                  path: `/projects/${encodeURIComponent(project)}/exceptions`,
                  method: 'POST',
                  body: { ...exception, path: exception.path || null, expires_at: new Date(exception.expires_at).toISOString() },
                });
              }}
            >
              <h3>Project-scoped exception</h3>
              {(['tool', 'rule_id', 'path', 'reason', 'expires_at'] as const).map((field) => (
                <label key={field}>
                  {{ tool: 'Tool', rule_id: 'Rule', path: 'Path (optional)', reason: 'Reason', expires_at: 'Expiry' }[field]}
                  <TextBox
                    type={field === 'expires_at' ? 'datetime-local' : 'text'}
                    required={field !== 'path'}
                    value={exception[field]}
                    onChange={(event) => setException({ ...exception, [field]: event.target.value })}
                  />
                </label>
              ))}
              <Button type="submit" disabled={save.isPending}>
                Create exception
              </Button>
            </form>
          )}
        </>
      )}
      {(exceptions.data ?? []).map((item) => (
        <p key={item.id}>
          {item.tool} · {item.rule_id} · {item.path ?? 'All paths'} · {item.reason} · expires {new Date(item.expires_at).toLocaleString()}
        </p>
      ))}
    </section>
  );
}
