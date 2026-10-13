import { API_BASE } from '@/config/env';
import { Button, Checkbox, TextBox } from '@/components/ui';
import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch, apiJson } from '@/services/api';
import type { PersonalTokenItem } from '@/types/api';
import DialogCloseButton from '@/components/common/DialogCloseButton';
import InfoHint from '@/components/common/InfoHint';
import { useToast } from '@/context/ToastProvider';

const DEFAULT_TTL_DAYS = 90;

export type TokenForm = { name: string; ttlDays: number | ''; triage: boolean };

// Mirrors the server rules of POST /api/v1/auth/me/tokens so the refusal is explained
// before the request, never instead of it: the API stays the authority.
export const validateTokenForm = (form: TokenForm): string | null => {
  if (!form.name.trim()) return 'Token name is required.';
  if (form.name.trim().length > 255) return 'Token name must be 255 characters or fewer.';
  if (!Number.isInteger(form.ttlDays) || Number(form.ttlDays) < 1 || Number(form.ttlDays) > 365) return 'Expiration must be a whole number of days between 1 and 365.';
  return null;
};

export const tokenScopes = (form: TokenForm): string[] => (form.triage ? ['analysis:read', 'collaboration:write'] : ['analysis:read']);

export default function AccountTokens({ authToken, onClose }: Readonly<{ authToken?: string; onClose: () => void }>) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const queryClient = useQueryClient();
  const notify = useToast();
  const [form, setForm] = useState<TokenForm>({ name: 'VS Code', ttlDays: DEFAULT_TTL_DAYS, triage: false });
  const [validationError, setValidationError] = useState<string | null>(null);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    const previousFocus = document.activeElement;
    // Native modal dialogs keep the background inert without a focus-trap dependency.
    dialog.showModal();
    return () => {
      dialog.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, []);

  const tokensQuery = useQuery({
    queryKey: ['auth', 'me', 'tokens'],
    queryFn: ({ signal }) => apiJson<PersonalTokenItem[]>(`${API_BASE}/v1/auth/me/tokens`, authToken, { signal }),
  });
  const createMutation = useMutation({
    gcTime: 0,
    mutationFn: (payload: TokenForm) =>
      apiJson<{ token: string; expires_at: string }>(`${API_BASE}/v1/auth/me/tokens`, authToken, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: payload.name.trim(), ttl_days: payload.ttlDays, scopes: tokenScopes(payload) }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['auth', 'me', 'tokens'] }),
  });
  const revokeMutation = useMutation({
    mutationFn: (tokenId: string) => apiFetch(`${API_BASE}/v1/auth/me/tokens/${encodeURIComponent(tokenId)}`, authToken, { method: 'DELETE' }),
    onSuccess: async () => {
      notify({ message: 'Personal access token revoked.' });
      await queryClient.invalidateQueries({ queryKey: ['auth', 'me', 'tokens'] });
    },
  });

  const busy = createMutation.isPending || revokeMutation.isPending;
  const tokens = tokensQuery.data ?? [];
  const error = validationError ?? createMutation.error?.message ?? revokeMutation.error?.message;
  const created = createMutation.data;

  const submit = () => {
    if (busy) return;
    const invalid = validateTokenForm(form);
    setValidationError(invalid);
    if (invalid) return;
    revokeMutation.reset();
    createMutation.mutate(form);
  };

  return (
    <dialog
      className="config-modal"
      ref={dialogRef}
      aria-labelledby="account-tokens-title"
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) onClose();
      }}
    >
      <header className="config-modal-header">
        <h3 id="account-tokens-title">
          Personal access tokens <InfoHint>Used by workstation tooling such as the CodeInsights extension for VS Code. A token carries your own permissions and nothing more.</InfoHint>
        </h3>
        <DialogCloseButton onClick={onClose} disabled={busy} />
      </header>

      <div className="form-grid compact">
        <label>
          Token name
          <TextBox value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} placeholder="VS Code" />
        </label>
        <label>
          Expiration (days)
          <TextBox
            type="number"
            min={1}
            max={365}
            value={form.ttlDays}
            onChange={(event) => {
              const parsed = event.target.value === '' ? '' : Number(event.target.value);
              setForm({ ...form, ttlDays: parsed === '' || Number.isFinite(parsed) ? parsed : '' });
            }}
          />
        </label>
        <label className="integration-option">
          <Checkbox type="checkbox" checked={form.triage} onChange={(event) => setForm({ ...form, triage: event.target.checked })} />
          <span>
            Allow issue triage
            <small className="field-help">Adds the collaboration:write scope: assignment, comments and review decisions. Read-only otherwise.</small>
          </span>
        </label>
      </div>

      <div className="action-row">
        <Button type="button" className="primary" onClick={submit} disabled={busy}>
          {createMutation.isPending ? 'Creating...' : 'Create token'}
        </Button>
      </div>

      {error && (
        <p className="feedback error" role="alert">
          {error}
        </p>
      )}

      {created && (
        <div className="account-secret" role="status">
          <h4>Copy this token now — it is never shown again</h4>
          <code>{created.token}</code>
          <p className="field-help">Expires on {new Date(created.expires_at).toLocaleString()}. Store it in the extension, not in a settings file.</p>
        </div>
      )}

      <h4 className="account-tokens-subtitle">Your tokens</h4>
      {tokensQuery.isPending ? (
        <p role="status">Loading tokens…</p>
      ) : tokensQuery.isError ? (
        <p className="feedback error" role="alert">
          {tokensQuery.error.message}
        </p>
      ) : tokens.length === 0 ? (
        <p className="field-help">No personal access token yet.</p>
      ) : (
        <div className="token-history-list">
          {tokens.map((token) => (
            <article key={token.token_id} className="token-history-item">
              <div>
                <strong>{token.name}</strong>
                <p>
                  Preview: {token.preview}
                  {token.revoked ? ' • revoked' : ''}
                </p>
              </div>
              <div>
                <p>Created: {new Date(token.created_at).toLocaleString()}</p>
                <p>Expires: {token.expires_at ? new Date(token.expires_at).toLocaleString() : 'Never'}</p>
                {!token.revoked && (
                  <Button type="button" className="danger" onClick={() => revokeMutation.mutate(token.token_id)} disabled={busy}>
                    {revokeMutation.isPending && revokeMutation.variables === token.token_id ? 'Revoking...' : 'Revoke'}
                  </Button>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
    </dialog>
  );
}
