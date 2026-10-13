import { API_BASE } from '@/config/env';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { apiJson } from '@/services/api';
import { useToast } from '@/context/ToastProvider';
import { Button, Grid, TextArea, type GridColumn } from '@/components/ui';
import { appendLicense, licenseOverlap, parseLicenseLines, type LicenseInventory, type LicensePolicy, type LicenseUsage } from '@/utils/licenses';

const API = `${API_BASE}/v1/licenses`;

function PolicyForm({ policy, inventory, saving, onSave }: { policy: LicensePolicy | null; inventory: LicenseInventory | undefined; saving: boolean; onSave: (body: Record<string, unknown>) => void }) {
  const [allowed, setAllowed] = useState(() => (policy?.allowed_licenses ?? []).join('\n'));
  const [denied, setDenied] = useState(() => (policy?.denied_licenses ?? []).join('\n'));
  const allowedList = parseLicenseLines(allowed);
  const deniedList = parseLicenseLines(denied);
  const overlap = licenseOverlap(allowedList, deniedList);
  const listed = useMemo(() => new Set([...allowedList, ...deniedList]), [allowedList, deniedList]);
  const columns = useMemo<GridColumn<LicenseUsage>[]>(
    () => [
      { id: 'license', header: 'Licence as declared', value: (row) => row.license, width: 320, minWidth: 180, rowHeader: true },
      { id: 'components', header: 'Components', value: (row) => row.components, width: 130, minWidth: 110 },
      { id: 'projects', header: 'Projects', value: (row) => row.projects, width: 110, minWidth: 90 },
      {
        id: 'actions',
        header: 'Classify',
        width: 190,
        minWidth: 170,
        render: (row) =>
          listed.has(row.license) ? (
            <span>In the draft</span>
          ) : (
            <div className="table-actions">
              <Button type="button" onClick={() => setAllowed((text) => appendLicense(text, row.license))}>
                Allow
              </Button>
              <Button type="button" variant="danger" onClick={() => setDenied((text) => appendLicense(text, row.license))}>
                Deny
              </Button>
            </div>
          ),
      },
    ],
    [listed],
  );

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (!overlap.length && !saving) onSave({ version: policy?.version ?? 0, allowed_licenses: allowedList, denied_licenses: deniedList });
        }}
      >
        <fieldset className="admin-settings-form" disabled={saving}>
          <legend className="sr-only">Licence lists</legend>
          <div className="form-grid compact">
            <label>
              Allowed licences ({allowedList.length})
              <TextArea rows={12} value={allowed} onChange={(event) => setAllowed(event.target.value)} />
            </label>
            <label>
              Denied licences ({deniedList.length})
              <TextArea rows={12} value={denied} onChange={(event) => setDenied(event.target.value)} />
            </label>
          </div>
          <p className="field-help">
            One licence per line, exactly as the SBOM declares it. Matching is case-sensitive and does not evaluate expressions: list <code>MIT OR Apache-2.0</code> as it appears. A component whose licences are not all listed needs a review and keeps its project&apos;s licence violations unmeasured.
          </p>
          {overlap.length > 0 && (
            <p className="feedback error" role="alert">
              Listed as both allowed and denied: {overlap.join(', ')}
            </p>
          )}
          <div className="action-row">
            <Button type="submit" variant="primary" disabled={overlap.length > 0}>
              {saving ? 'Saving...' : 'Save licence policy'}
            </Button>
          </div>
        </fieldset>
      </form>
      <section className="admin-subsection" aria-labelledby="license-inventory-title">
        <h3 id="license-inventory-title">Unclassified licences</h3>
        {inventory && (
          <>
            <p className="supporting-copy">
              From the latest analysis of {inventory.projects} project(s), {inventory.components} component(s). Classifying a licence adds it to the draft above; save to apply it from the next ingestion.
            </p>
            {inventory.unlicensed_components > 0 && (
              <p className="feedback warning" role="status">
                {inventory.unlicensed_components} component(s) in {inventory.unlicensed_projects} project(s) declare no licence: no list can classify them, so those projects keep licence violations unmeasured until their SBOM declares one.
              </p>
            )}
            {inventory.licenses.length ? (
              <Grid ariaLabel="Unclassified licence inventory" caption={inventory.truncated ? 'The 200 most frequent unclassified licences' : 'Unclassified licences, most frequent first'} rows={inventory.licenses} columns={columns} rowKey={(row) => row.license} />
            ) : (
              <p className="empty-state" role="status">
                Every declared licence is classified.
              </p>
            )}
          </>
        )}
      </section>
    </>
  );
}

export default function LicensePolicySettings({ authToken }: { authToken?: string }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const policy = useQuery({ queryKey: ['license-policy'], queryFn: ({ signal }) => apiJson<LicensePolicy | null>(`${API}/policy`, authToken, { signal }) });
  const inventory = useQuery({ queryKey: ['license-inventory'], queryFn: ({ signal }) => apiJson<LicenseInventory>(`${API}/unclassified`, authToken, { signal }) });
  const save = useMutation({
    mutationFn: (body: Record<string, unknown>) => apiJson<LicensePolicy>(`${API}/policy`, authToken, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
    onSuccess: async () => {
      notify({ message: 'Licence policy saved. It applies from the next ingestion.' });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['license-policy'] }), queryClient.invalidateQueries({ queryKey: ['license-inventory'] })]);
    },
  });
  const error = policy.error ?? inventory.error ?? save.error;

  return (
    <section aria-labelledby="license-policy-title">
      <h2 id="license-policy-title">Licence policy</h2>
      <p className="supporting-copy">
        Organization-wide lists used to classify SBOM components. A profile that carries its own lists keeps them. Each ingestion freezes the policy it was judged with, so saving never rewrites a past verdict.
      </p>
      {policy.data && (
        <p className="supporting-copy">
          Version {policy.data.version}, updated by {policy.data.updated_by || 'unknown'} on {new Date(policy.data.updated_at).toLocaleString()}.
        </p>
      )}
      {error && (
        <p className="feedback error" role="alert">
          {error.message}
        </p>
      )}
      {policy.isLoading && <p role="status">Loading licence policy...</p>}
      {policy.isSuccess && <PolicyForm key={policy.data?.version ?? 0} policy={policy.data} inventory={inventory.data} saving={save.isPending} onSave={(body) => save.mutate(body)} />}
    </section>
  );
}
