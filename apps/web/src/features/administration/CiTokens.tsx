import { API_BASE } from '@/config/env';
import { Button, TextBox } from '@/components/ui';
import InfoHint from '@/components/common/InfoHint';
import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch, apiJson } from '@/services/api';
import type { PlatformTokenHistoryItem, ProjectCatalogItem } from '@/types/api';
import { useToast } from '@/context/ToastProvider';
import ProjectPicker from '@/components/common/ProjectPicker';

const DEFAULT_TOKEN_TTL_DAYS = 90;
const CI_TOKEN_SCOPES = ['analysis:read', 'analysis:write', 'integration:write'] as const;

export default function CiTokens({ authToken }: { authToken?: string }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const [platformTokenName, setPlatformTokenName] = useState('Azure DevOps CI token');
  const [platformTokenInput, setPlatformTokenInput] = useState('');
  const [platformTokenTtlDays, setPlatformTokenTtlDays] = useState<number | ''>(DEFAULT_TOKEN_TTL_DAYS);
  const [platformTokenProjectKeys, setPlatformTokenProjectKeys] = useState<string[]>([]);
  const [validationError, setValidationError] = useState<string | null>(null);
  const projectsQuery = useQuery({
    queryKey: ['projects'],
    queryFn: ({ signal }) => apiJson<ProjectCatalogItem[]>(`${API_BASE}/v1/projects`, authToken, { signal }),
  });
  const projectOptions = useMemo(() => (projectsQuery.data ?? []).map((project) => ({ key: project.key, name: project.name })), [projectsQuery.data]);
  const accessCatalogLoading = projectsQuery.isPending;
  const tokenStatusQuery = useQuery({
    queryKey: ['tokens', 'status'],
    queryFn: ({ signal }) => apiJson<{ configured: boolean; expired: boolean; expires_at: string | null }>(`${API_BASE}/v1/platform/token/status`, authToken, { signal }),
  });
  const platformTokenConfigured = tokenStatusQuery.data?.configured ?? null;
  const platformTokenExpired = tokenStatusQuery.data?.expired ?? false;
  const platformTokenExpiresAt = tokenStatusQuery.data?.expires_at ?? null;
  const tokenHistoryQuery = useQuery({
    queryKey: ['tokens', 'history'],
    queryFn: ({ signal }) => apiJson<PlatformTokenHistoryItem[]>(`${API_BASE}/v1/platform/token/history`, authToken, { signal }),
  });
  const platformTokenHistory = tokenHistoryQuery.data ?? [];
  const invalidateTokens = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['tokens'] }), queryClient.invalidateQueries({ queryKey: ['audit'] })]);
  const generateMutation = useMutation({
    gcTime: 0,
    mutationFn: async (mode: 'new' | 'rotate') => {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (mode === 'rotate') headers['X-Current-Platform-Token'] = platformTokenInput.trim();
      const result = await apiJson<{ token: string }>(`${API_BASE}/v1/platform/token/generate`, authToken, {
        method: 'POST',
        headers,
        body: JSON.stringify({
          token_name: platformTokenName.trim(),
          ttl_days: platformTokenTtlDays,
          require_current_token: mode === 'rotate',
          project_keys: platformTokenProjectKeys,
          scopes: CI_TOKEN_SCOPES,
        }),
      });
      if (typeof result.token !== 'string' || !result.token) throw new Error('Token generation returned an invalid response.');
      return result.token;
    },
    onSuccess: async (_, mode) => {
      if (mode === 'rotate') setPlatformTokenInput('');
      await invalidateTokens();
    },
  });
  const revokeMutation = useMutation({
    mutationFn: (tokenId: string) => apiFetch(`${API_BASE}/v1/platform/token/${encodeURIComponent(tokenId)}/revoke`, authToken, { method: 'POST' }),
    onSuccess: async () => {
      notify({ message: 'CI/CD token revoked.' });
      await invalidateTokens();
    },
  });
  const generatingPlatformToken = generateMutation.isPending;
  const revokingPlatformTokenId = revokeMutation.isPending ? revokeMutation.variables : null;
  const generatedPlatformToken = generateMutation.data;
  const platformTokenError = validationError ?? generateMutation.error?.message ?? revokeMutation.error?.message;
  const readError = [projectsQuery, tokenStatusQuery, tokenHistoryQuery].find((query) => query.isError)?.error?.message;
  const generatePlatformToken = (mode: 'new' | 'rotate') => {
    if (generatingPlatformToken || revokeMutation.isPending) return;
    setValidationError(null);
    if (!platformTokenProjectKeys.length) {
      setValidationError('Select at least one project for this token.');
      return;
    }
    if (!platformTokenName.trim()) {
      setValidationError('Token name is required.');
      return;
    }
    if (!Number.isInteger(platformTokenTtlDays) || Number(platformTokenTtlDays) < 1 || Number(platformTokenTtlDays) > 3650) {
      setValidationError('Expiration must be a whole number between 1 and 3650 days.');
      return;
    }
    if (mode === 'rotate' && !platformTokenInput.trim()) {
      setValidationError('Current token is required to rotate.');
      return;
    }
    if (mode === 'new' && !window.confirm('Generate this independent CI/CD token for the selected projects?')) return;
    revokeMutation.reset();
    generateMutation.mutate(mode);
  };
  const revokePlatformToken = (tokenId: string) => {
    if (generatingPlatformToken || revokeMutation.isPending) return;
    if (!window.confirm('Revoke this CI/CD token? Pipelines using it will immediately lose access.')) return;
    setValidationError(null);
    revokeMutation.mutate(tokenId);
  };
  return (
    <>
      <h2>
        PAT Management <InfoHint>Used by Azure Pipelines or GitHub Actions to authenticate against this platform.</InfoHint>
      </h2>
      {readError && (
        <p className="feedback error" role="alert">
          {readError}
        </p>
      )}
      <p>
        {platformTokenConfigured === true && ' A token is currently active.'}
        {platformTokenConfigured === false && ' No token configured — generate one to enable CI authentication.'}
      </p>

      <div className="auth-card">
        <div className="form-grid compact">
          <label>
            Token name
            <TextBox value={platformTokenName} onChange={(event) => setPlatformTokenName(event.target.value)} placeholder="Azure DevOps CI token" />
          </label>
          <label>
            Expiration (days)
            <TextBox
              type="number"
              min={1}
              max={3650}
              value={platformTokenTtlDays}
              onChange={(event) => {
                const parsed = event.target.value === '' ? '' : Number(event.target.value);
                setPlatformTokenTtlDays(parsed === '' || Number.isFinite(parsed) ? parsed : '');
              }}
            />
          </label>
          <ProjectPicker
            multiple
            label="Authorized projects (required)"
            options={projectOptions}
            value={platformTokenProjectKeys}
            loading={accessCatalogLoading}
            onChange={setPlatformTokenProjectKeys}
          />
          <div>
            <span className="issue-filter-label">Fixed scopes</span>
            <p className="scope-note">
              <code>{CI_TOKEN_SCOPES.join(' · ')}</code>
            </p>
          </div>
          {platformTokenConfigured && (
            <label>
              Current token (required for rotation)
              <TextBox
                type="password"
                value={platformTokenInput}
                onChange={(event) => setPlatformTokenInput(event.target.value)}
                placeholder="Paste the current token for secure rotation"
                autoComplete="off"
              />
            </label>
          )}
        </div>

        {platformTokenExpiresAt && (
          <p style={{ fontSize: '0.85rem', color: platformTokenExpired ? 'var(--danger)' : 'var(--text-secondary)', margin: '0.5rem 0 0' }}>
            {platformTokenExpired ? '⚠ Expired on' : 'Expires on'} {new Date(platformTokenExpiresAt).toLocaleString()}
          </p>
        )}
      </div>

      <div className="action-row">
        <Button
          type="button"
          onClick={() => void generatePlatformToken('new')}
          disabled={generatingPlatformToken || revokeMutation.isPending || platformTokenProjectKeys.length === 0 || accessCatalogLoading}
        >
          {generatingPlatformToken ? 'Generating...' : 'New token'}
        </Button>
        {platformTokenConfigured && (
          <Button
            type="button"
            onClick={() => void generatePlatformToken('rotate')}
            disabled={generatingPlatformToken || revokeMutation.isPending || platformTokenProjectKeys.length === 0 || accessCatalogLoading}
          >
            {generatingPlatformToken ? 'Rotating...' : 'Rotate token'}
          </Button>
        )}
      </div>

      {platformTokenError && (
        <p className="feedback error" role="alert">
          {platformTokenError}
        </p>
      )}

      {generatedPlatformToken && (
        <div className="auth-card" style={{ marginTop: '1rem', background: 'var(--bg-light)' }}>
          <h4 style={{ marginBottom: '0.5rem' }}>Token generated — copy it now, it won't be shown again</h4>
          <code
            style={{
              display: 'block',
              padding: '0.75rem',
              background: 'var(--bg-lighter)',
              borderRadius: '4px',
              wordBreak: 'break-all',
              fontSize: '0.85rem',
              letterSpacing: '0.04em',
            }}
          >
            {generatedPlatformToken}
          </code>
          <p style={{ marginTop: '0.75rem', fontSize: '0.85rem', opacity: 0.7 }}>
            Set <strong>PLATFORM_API_TOKEN</strong> to this value in your Azure DevOps pipeline variables (as a secret).
          </p>
        </div>
      )}

      <div className="auth-card" style={{ marginTop: '1rem' }}>
        <h4 style={{ marginBottom: '0.75rem' }}>Token history</h4>
        {tokenHistoryQuery.isPending ? (
          <p role="status">Loading token history…</p>
        ) : tokenHistoryQuery.isError ? null : platformTokenHistory.length === 0 ? (
          <p style={{ margin: 0, opacity: 0.7 }}>No tokens generated yet.</p>
        ) : (
          <div className="token-history-list">
            {platformTokenHistory.map((item) => (
              <article key={item.token_id} className="token-history-item">
                <div>
                  <strong>{item.token_name}</strong>
                  <p>
                    Preview: {item.token_preview}
                    {item.is_current ? ' • current' : ''}
                    {item.revoked ? ' • revoked' : ''}
                  </p>
                  <p>Projects: {item.project_keys.length > 0 ? item.project_keys.join(', ') : 'None'}</p>
                  <p>Scopes: {item.scopes.length > 0 ? item.scopes.join(', ') : 'None'}</p>
                </div>
                <div>
                  <p>Created: {new Date(item.created_at).toLocaleString()}</p>
                  <p>Expires: {item.expires_at ? new Date(item.expires_at).toLocaleString() : 'Never'}</p>
                  {!item.revoked && (
                    <Button type="button" className="danger" onClick={() => void revokePlatformToken(item.token_id)} disabled={revokeMutation.isPending || generatingPlatformToken}>
                      {revokingPlatformTokenId === item.token_id ? 'Revoking...' : 'Revoke'}
                    </Button>
                  )}
                </div>
              </article>
            ))}
          </div>
        )}
      </div>
    </>
  );
}
