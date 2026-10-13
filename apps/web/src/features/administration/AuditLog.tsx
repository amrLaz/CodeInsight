import { API_BASE } from '@/config/env';
import InfoHint from '@/components/common/InfoHint';
import { useMemo, useState } from 'react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { apiJson } from '@/services/api';
import type { AuditLogEntry } from '@/types/api';
import { Button, Grid, ListBox, TextBox, type GridColumn } from '@/components/ui';

export default function AuditLog({ authToken }: { authToken?: string }) {
  const queryClient = useQueryClient();
  const [auditLogActionFilter, setAuditLogActionFilter] = useState('');
  const [auditLogActorFilter, setAuditLogActorFilter] = useState('');
  const [auditFilters, setAuditFilters] = useState({ action: '', actor: '' });
  const auditQuery = useInfiniteQuery({
    queryKey: ['audit', auditFilters],
    initialPageParam: 0,
    queryFn: ({ signal, pageParam }) => {
      const params = new URLSearchParams({ limit: '50', offset: String(pageParam) });
      if (auditFilters.action) params.set('action', auditFilters.action);
      if (auditFilters.actor) params.set('actor', auditFilters.actor);
      return apiJson<{ total: number; entries: AuditLogEntry[] }>(`${API_BASE}/v1/audit-logs?${params}`, authToken, { signal });
    },
    getNextPageParam: (last, pages) => {
      const count = pages.reduce((sum, page) => sum + page.entries.length, 0);
      return last.entries.length && count < last.total ? count : undefined;
    },
  });
  const auditLogs = auditQuery.data?.pages.flatMap((page) => page.entries) ?? [];
  const auditLogLoading = auditQuery.isFetching;
  const columns = useMemo<GridColumn<AuditLogEntry>[]>(
    () => [
      {
        id: 'date',
        header: 'Date',
        value: (entry) => new Date(entry.created_at).toLocaleString(),
        width: 170,
        minWidth: 140,
        className: 'audit-date',
      },
      {
        id: 'actor',
        header: 'Actor',
        value: (entry) => entry.actor_email,
        width: 245,
        minWidth: 150,
        className: 'audit-code',
      },
      {
        id: 'role',
        header: 'Role',
        render: (entry) => <span className={`audit-role ${entry.actor_role === 'admin' ? 'admin' : 'regular'}`}>{entry.actor_role}</span>,
        width: 100,
        minWidth: 84,
      },
      {
        id: 'action',
        header: 'Action',
        value: (entry) => entry.action,
        width: 190,
        minWidth: 130,
        className: 'audit-code',
      },
      {
        id: 'resource',
        header: 'Resource',
        value: (entry) => entry.resource_label ?? entry.resource_id ?? entry.resource_type,
        width: 245,
        minWidth: 140,
      },
      {
        id: 'details',
        header: 'Details',
        value: (entry) => (entry.details ? JSON.stringify(entry.details) : '-'),
        width: 320,
        minWidth: 180,
        className: 'audit-details',
      },
    ],
    [],
  );

  return (
    <>
      <h2>
        Audit Log <InfoHint>Immutable trace of every security-relevant action — role changes, rule suppressions, token rotations, settings updates.</InfoHint>
      </h2>

      <div className="auth-card" style={{ marginBottom: '1rem' }}>
        <form
          className="form-grid compact"
          style={{ alignItems: 'flex-end' }}
          onSubmit={(event) => {
            event.preventDefault();
            const next = { action: auditLogActionFilter, actor: auditLogActorFilter.trim() };
            void queryClient.resetQueries({ queryKey: ['audit', next], exact: true });
            setAuditFilters(next);
          }}
        >
          <label>
            Filter by action
            <ListBox value={auditLogActionFilter} onChange={(e) => setAuditLogActionFilter(e.target.value)}>
              <option value="">All actions</option>
              <option value="user.login">user.login</option>
              <option value="user.login.sso">user.login.sso</option>
              <option value="user.login.sso.first">user.login.sso.first (first SSO)</option>
              <option value="user.create">user.create</option>
              <option value="user.delete">user.delete</option>
              <option value="user.role.change">user.role.change</option>
              <option value="user.status.change">user.status.change</option>
              <option value="user.grants.replace">user.grants.replace</option>
              <option value="user.password.reset">user.password.reset</option>
              <option value="rule.suppress">rule.suppress</option>
              <option value="rule.unsuppress">rule.unsuppress</option>
              <option value="token.generate">token.generate</option>
              <option value="token.revoke">token.revoke</option>
              <option value="settings.update">settings.update</option>
              <option value="data.cleanup">data.cleanup</option>
            </ListBox>
          </label>
          <label>
            Filter by actor
            <TextBox placeholder="email..." value={auditLogActorFilter} onChange={(e) => setAuditLogActorFilter(e.target.value)} />
          </label>
          <Button type="submit" disabled={auditLogLoading}>
            {auditLogLoading ? 'Loading...' : 'Apply'}
          </Button>
        </form>
      </div>

      <div className="auth-card" style={{ padding: 0, overflow: 'hidden' }}>
        {auditLogLoading && <p role="status">Loading audit events…</p>}
        {auditQuery.isError ? (
          <p className="feedback error" role="alert">
            {auditQuery.error.message}
          </p>
        ) : auditLogs.length === 0 && !auditLogLoading ? (
          <p style={{ padding: '1.5rem', margin: 0, opacity: 0.7, textAlign: 'center' }}>No audit events yet. Actions will appear here as users interact with the platform.</p>
        ) : (
          <Grid ariaLabel="Audit events" rows={auditLogs} columns={columns} rowKey={(entry) => entry.id} className="audit-grid" />
        )}
        {auditQuery.hasNextPage && (
          <div style={{ padding: '0.75rem', textAlign: 'center', borderTop: '1px solid var(--border)' }}>
            <Button onClick={() => void auditQuery.fetchNextPage()} disabled={auditLogLoading} style={{ fontSize: '0.85rem' }}>
              {auditLogLoading ? 'Loading...' : 'Next'}
            </Button>
          </div>
        )}
      </div>
    </>
  );
}
