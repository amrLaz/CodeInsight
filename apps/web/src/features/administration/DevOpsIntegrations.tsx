import { API_BASE } from '@/config/env';
import { Button, Checkbox, TextBox } from '@/components/ui';
import InfoHint from '@/components/common/InfoHint';
import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch as request, apiJson } from '@/services/api';
import type { AzureDevOpsOrganizationConfig } from '@/types/api';
import DialogCloseButton from '@/components/common/DialogCloseButton';
import { useToast } from '@/context/ToastProvider';

export default function DevOpsIntegrations({ authToken }: { authToken?: string }) {
  const [devOpsConfigModalOpen, setDevOpsConfigModalOpen] = useState(false);
  const [editingAzureDevOpsConfigId, setEditingAzureDevOpsConfigId] = useState<string | null>(null);
  const [azureDevOpsConfigForm, setAzureDevOpsConfigForm] = useState({
    config_name: '',
    organization_url: 'https://dev.azure.com/your-organization',
    repo_import_enabled: false,
    quality_gate_enabled: false,
    work_items_enabled: false,
  });

  const queryClient = useQueryClient();
  const notify = useToast();
  const devOpsQuery = useQuery({
    queryKey: ['devops'],
    queryFn: ({ signal }) => apiJson<AzureDevOpsOrganizationConfig[]>(`${API_BASE}/v1/integrations/azure-devops/configs`, authToken, { signal }),
  });
  const azureDevOpsConfigs = devOpsQuery.data ?? [];
  const configDialogRef = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = devOpsConfigModalOpen ? configDialogRef.current : null;
    if (!dialog) return;
    const previousFocus = document.activeElement;
    dialog.showModal();
    return () => {
      dialog.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [devOpsConfigModalOpen]);
  const openCreateAzureDevOpsConfigModal = () => {
    mutation.reset();
    setEditingAzureDevOpsConfigId(null);
    setAzureDevOpsConfigForm({
      config_name: '',
      organization_url: 'https://dev.azure.com/your-organization',
      repo_import_enabled: false,
      quality_gate_enabled: false,
      work_items_enabled: false,
    });
    setDevOpsConfigModalOpen(true);
  };

  const openEditAzureDevOpsConfigModal = (config: AzureDevOpsOrganizationConfig) => {
    mutation.reset();
    setEditingAzureDevOpsConfigId(config.config_id);
    setAzureDevOpsConfigForm({
      config_name: config.config_name,
      organization_url: config.organization_url,
      repo_import_enabled: config.repo_import_enabled,
      quality_gate_enabled: config.quality_gate_enabled,
      work_items_enabled: config.work_items_enabled,
    });
    setDevOpsConfigModalOpen(true);
  };

  const mutation = useMutation({
    mutationFn: async (action: { kind: 'save'; id: string | null; form: typeof azureDevOpsConfigForm } | { kind: 'test' | 'delete'; id: string }) => {
      const endpoint = `${API_BASE}/v1/integrations/azure-devops/configs${action.id ? `/${encodeURIComponent(action.id)}` : ''}`;
      await request(
        action.kind === 'test' ? `${endpoint}/test` : endpoint,
        authToken,
        action.kind === 'save'
          ? {
              method: action.id ? 'PUT' : 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(action.form),
            }
          : { method: action.kind === 'delete' ? 'DELETE' : 'POST' },
      );
    },
    onSuccess: (_result, action) => {
      if (action.kind === 'save') {
        setDevOpsConfigModalOpen(false);
      }
      notify({
        message:
          action.kind === 'save'
            ? `Azure DevOps configuration ${action.id ? 'updated' : 'created'}.`
            : action.kind === 'test'
              ? 'Azure DevOps configuration checked.'
              : 'Azure DevOps configuration deleted.',
      });
    },
    onSettled: async (_result, error, action) => {
      // The test endpoint persists last_error/status even when it returns an HTTP error.
      if (!error || action.kind === 'test') await queryClient.invalidateQueries({ queryKey: ['devops'] });
    },
  });
  const savingAzureDevOpsConfig = mutation.isPending && mutation.variables.kind === 'save';
  const testingAzureDevOpsConfigId = mutation.isPending && mutation.variables.kind === 'test' ? mutation.variables.id : null;
  const deletingAzureDevOpsConfigId = mutation.isPending && mutation.variables.kind === 'delete' ? mutation.variables.id : null;
  const error = mutation.error?.message;
  const saveAzureDevOpsConfig = () => {
    if (!mutation.isPending) mutation.mutate({ kind: 'save', id: editingAzureDevOpsConfigId, form: azureDevOpsConfigForm });
  };
  const testAzureDevOpsConfig = (id: string) => {
    if (!mutation.isPending) mutation.mutate({ kind: 'test', id });
  };
  const deleteAzureDevOpsConfig = (config: AzureDevOpsOrganizationConfig) => {
    if (!mutation.isPending && window.confirm(`Delete Azure DevOps configuration "${config.config_name}"?`)) mutation.mutate({ kind: 'delete', id: config.config_id });
  };

  return (
    <>
      {((!devOpsConfigModalOpen && error) || devOpsQuery.error) && (
        <p className="feedback error" role="alert">
          {error ?? devOpsQuery.error?.message}
        </p>
      )}
      <>
        <h2>
          Integrations <InfoHint>Connect repository metadata and optional Azure DevOps feedback.</InfoHint>
        </h2>
        <section className="admin-subsection" aria-labelledby="azure-devops-organization-title">
          <div className="section-heading-row">
            <div>
              <h3 id="azure-devops-organization-title">Azure DevOps organization</h3>
              <p>Optional connection for Quality Gate status and comments on pull requests. Analysis publication uses the CI token and works independently.</p>
            </div>
            <Button type="button" disabled={mutation.isPending} onClick={openCreateAzureDevOpsConfigModal}>
              Add organization
            </Button>
          </div>

          <div className="azure-devops-config-list">
            {devOpsQuery.isLoading ? (
              <p role="status">Loading Azure DevOps configurations…</p>
            ) : devOpsQuery.isError && !devOpsQuery.data ? null : azureDevOpsConfigs.length === 0 ? (
              <p className="empty-state">No organization connected. Scans still publish, but Azure DevOps pull-request feedback is inactive.</p>
            ) : (
              azureDevOpsConfigs.map((config) => (
                <article key={config.config_id} className="azure-devops-config-card">
                  <div className="azure-devops-config-header">
                    <div>
                      <h3>{config.config_name}</h3>
                      <p>{config.organization_url}</p>
                    </div>
                    <div className="action-row">
                      <Button type="button" disabled={mutation.isPending} onClick={() => openEditAzureDevOpsConfigModal(config)}>
                        Edit
                      </Button>
                      <Button type="button" className="danger" onClick={() => void deleteAzureDevOpsConfig(config)} disabled={mutation.isPending}>
                        {deletingAzureDevOpsConfigId === config.config_id ? 'Deleting...' : 'Delete'}
                      </Button>
                    </div>
                  </div>

                  <div className="azure-devops-config-capabilities">
                    <p className={config.quality_gate_enabled ? 'integration-check ok' : 'integration-check ko'}>
                      <span aria-hidden="true" />
                      Quality Gate status reporting
                    </p>
                    <p className={config.work_items_enabled ? 'integration-check ok' : 'integration-check ko'}>
                      <span aria-hidden="true" />
                      Azure Boards work items
                    </p>
                    <p className={config.repo_import_enabled ? 'integration-check ok' : 'integration-check ko'}>
                      <span aria-hidden="true" />
                      Import repositories from your DevOps Platform
                    </p>
                    <p className={config.status === 'validated' ? 'integration-check ok' : 'integration-check ko'}>
                      <span aria-hidden="true" />
                      Managed identity authentication
                    </p>
                  </div>

                  <div className="azure-devops-config-meta">
                    <p>
                      Status: <strong>{config.status}</strong>
                    </p>
                    <p>Last checked: {config.last_tested_at ? new Date(config.last_tested_at).toLocaleString() : 'never'}</p>
                    {config.last_error && <p className="fail">Error: {config.last_error}</p>}
                  </div>

                  <div className="action-row">
                    <Button type="button" onClick={() => void testAzureDevOpsConfig(config.config_id)} disabled={mutation.isPending}>
                      {testingAzureDevOpsConfigId === config.config_id ? 'Checking...' : 'Check configuration'}
                    </Button>
                  </div>
                </article>
              ))
            )}
          </div>
        </section>
      </>
      {devOpsConfigModalOpen && (
        <dialog
          className="config-modal"
          ref={configDialogRef}
          aria-labelledby="config-dialog-title"
          onCancel={(event) => {
            event.preventDefault();
            if (!savingAzureDevOpsConfig) setDevOpsConfigModalOpen(false);
          }}
        >
          <header className="config-modal-header">
            <div>
              <span className="section-kicker">Azure DevOps organization</span>
              <h3 id="config-dialog-title">{editingAzureDevOpsConfigId ? 'Edit configuration' : 'Create configuration'}</h3>
            </div>
            <DialogCloseButton onClick={() => setDevOpsConfigModalOpen(false)} disabled={savingAzureDevOpsConfig} />
          </header>
          {error && (
            <p className="feedback error" role="alert">
              {error}
            </p>
          )}
          <fieldset className="notification-fields" disabled={savingAzureDevOpsConfig} aria-label="Azure DevOps configuration">
            <div className="form-grid compact">
              <label>
                Configuration name
                <TextBox
                  value={azureDevOpsConfigForm.config_name}
                  onChange={(event) => setAzureDevOpsConfigForm((current) => ({ ...current, config_name: event.target.value }))}
                  placeholder="up-group-azure-devops"
                />
              </label>
              <label>
                Azure DevOps URL
                <TextBox
                  value={azureDevOpsConfigForm.organization_url}
                  onChange={(event) => setAzureDevOpsConfigForm((current) => ({ ...current, organization_url: event.target.value }))}
                  placeholder="https://dev.azure.com/up-group"
                />
              </label>
              <p className="field-help">
                Azure DevOps REST operations use the CodeInsights App Service managed identity. Add that identity to this organization and grant only the permissions required by enabled capabilities.
              </p>
              <label className="integration-option">
                <Checkbox
                  type="checkbox"
                  checked={azureDevOpsConfigForm.quality_gate_enabled}
                  onChange={(event) =>
                    setAzureDevOpsConfigForm((current) => ({
                      ...current,
                      quality_gate_enabled: event.target.checked,
                    }))
                  }
                />
                <span>
                  <strong>PR decoration &amp; quality gate reporting</strong>
                  <small>Publish the gate status and its analysis link on pull requests.</small>
                </span>
              </label>
              <label className="integration-option">
                <Checkbox
                  type="checkbox"
                  checked={azureDevOpsConfigForm.work_items_enabled}
                  onChange={(event) =>
                    setAzureDevOpsConfigForm((current) => ({
                      ...current,
                      work_items_enabled: event.target.checked,
                    }))
                  }
                />
                <span>
                  <strong>Azure Boards work items</strong>
                  <small>Create a work item from a finding, manually or by project rules. Requires Work Items (Read &amp; Write) for the managed identity.</small>
                </span>
              </label>
              <label className="integration-option">
                <Checkbox
                  type="checkbox"
                  checked={azureDevOpsConfigForm.repo_import_enabled}
                  onChange={(event) =>
                    setAzureDevOpsConfigForm((current) => ({
                      ...current,
                      repo_import_enabled: event.target.checked,
                    }))
                  }
                />
                <span>
                  <strong>Repository import</strong>
                  <small>Allow CodeInsights to discover repositories in this organization.</small>
                </span>
              </label>
            </div>
            <div className="action-row" style={{ marginTop: '1rem' }}>
              <Button className="primary" type="button" onClick={() => void saveAzureDevOpsConfig()} disabled={savingAzureDevOpsConfig}>
                {savingAzureDevOpsConfig ? 'Saving...' : 'Save configuration'}
              </Button>
              <Button type="button" disabled={savingAzureDevOpsConfig} onClick={() => setDevOpsConfigModalOpen(false)}>
                Cancel
              </Button>
            </div>
          </fieldset>
        </dialog>
      )}
    </>
  );
}
