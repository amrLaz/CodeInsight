import { Button, Checkbox, Grid, ListBox, TextBox } from '@/components/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { apiFetch, apiJson } from '@/services/api';
import type { AccessPrincipal, DirectoryPrincipal, DirectoryPrincipalPage, LocalUserRole, OrganizationCatalogItem, ProjectCatalogItem } from '@/types/api';
import DialogCloseButton from '@/components/common/DialogCloseButton';
import InfoHint from '@/components/common/InfoHint';
import ProjectPicker from '@/components/common/ProjectPicker';
import { useToast } from '@/context/ToastProvider';

type Assignment = {
  kind: AccessPrincipal['kind'];
  principal_id: string;
  object_id: string;
  display_name: string;
  email: string | null;
  role: LocalUserRole;
  is_active: boolean;
  read_only: boolean;
  organization_ids: string[];
  project_keys: string[];
};

const emptyAssignment: Assignment = {
  kind: 'entra-user',
  principal_id: '',
  object_id: '',
  display_name: '',
  email: null,
  role: 'default',
  is_active: true,
  read_only: false,
  organization_ids: [],
  project_keys: [],
};

function initials(name: string) {
  return (
    name
      .trim()
      .split(/\s+/)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase())
      .join('') || '?'
  );
}

function GroupIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M8.5 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm7-1a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5ZM3 18.5c0-3 2.2-5 5.5-5s5.5 2 5.5 5v.5H3v-.5Zm11.6.5v-.5c0-1.5-.5-2.8-1.3-3.9.7-.4 1.5-.6 2.4-.6 3 0 5 1.8 5 4.5v.5h-6.1Z" />
    </svg>
  );
}

function accessSummary(principal: AccessPrincipal) {
  if (principal.role === 'admin' || principal.grants.some((grant) => grant.scope_type === 'all')) return 'All projects';
  if (principal.grants.length === 0) return 'No explicit access';
  return principal.grants.map((grant) => `${grant.scope_type}: ${grant.scope_id}`).join(' · ');
}

function PrincipalAvatar({ principal, apiBase, authToken }: { principal: AccessPrincipal; apiBase: string; authToken?: string }) {
  const target = useRef<HTMLSpanElement>(null);
  const canLoadPhoto = principal.kind === 'entra-user' && !!principal.object_id;
  const [nearViewport, setNearViewport] = useState(false);

  useEffect(() => {
    if (!canLoadPhoto || nearViewport) return;
    if (!('IntersectionObserver' in window)) {
      setNearViewport(true);
      return;
    }
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting) {
          setNearViewport(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    if (target.current) observer.observe(target.current);
    return () => observer.disconnect();
  }, [canLoadPhoto, nearViewport]);

  const photoQuery = useQuery({
    queryKey: ['access-principal-photo', principal.object_id],
    enabled: canLoadPhoto && nearViewport && !!authToken,
    staleTime: 5 * 60_000,
    gcTime: 5 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => (await apiFetch(`${apiBase}/v1/auth/access-principals/entra-user/${encodeURIComponent(principal.object_id ?? '')}/photo`, authToken, { signal })).blob(),
  });
  const [photoUrl, setPhotoUrl] = useState<string>();

  useEffect(() => {
    if (!photoQuery.data) {
      setPhotoUrl(undefined);
      return;
    }
    const url = URL.createObjectURL(photoQuery.data);
    setPhotoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [photoQuery.data]);

  return (
    <span ref={target} className={`iam-principal-avatar ${principal.kind === 'entra-group' ? 'group' : ''}`} aria-hidden="true">
      {principal.kind === 'entra-group' ? <GroupIcon /> : photoUrl ? <img src={photoUrl} alt="" /> : initials(principal.display_name)}
    </span>
  );
}

export default function UsersAccess({ apiBase, authToken, localLoginVisible }: { apiBase: string; authToken?: string; localLoginVisible: boolean }) {
  const queryClient = useQueryClient();
  const notify = useToast();
  const assignmentDialog = useRef<HTMLDialogElement>(null);
  const localUserDialog = useRef<HTMLDialogElement>(null);
  const removalDialog = useRef<HTMLDialogElement>(null);
  const [assignmentDialogMode, setAssignmentDialogMode] = useState<'add' | 'edit' | null>(null);
  const [localUserDialogOpen, setLocalUserDialogOpen] = useState(false);
  const [principalToRemove, setPrincipalToRemove] = useState<AccessPrincipal | null>(null);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [comboboxOpen, setComboboxOpen] = useState(false);
  const [activeResultIndex, setActiveResultIndex] = useState(-1);
  const skipNextSearch = useRef(false);
  const listboxId = useId();
  const [assignment, setAssignment] = useState<Assignment>(emptyAssignment);
  const [newLocalUser, setNewLocalUser] = useState({ full_name: '', email: '', password: '', role: 'default' as LocalUserRole });
  const [passwordForm, setPasswordForm] = useState({ user_id: '', password: '' });

  useEffect(() => {
    if (!assignmentDialogMode || !assignmentDialog.current) return;
    const previousFocus = document.activeElement;
    assignmentDialog.current.showModal();
    return () => {
      if (assignmentDialog.current?.open) assignmentDialog.current.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [assignmentDialogMode]);

  useEffect(() => {
    if (!localUserDialogOpen || !localUserDialog.current) return;
    const previousFocus = document.activeElement;
    localUserDialog.current.showModal();
    return () => {
      if (localUserDialog.current?.open) localUserDialog.current.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [localUserDialogOpen]);

  useEffect(() => {
    if (!principalToRemove || !removalDialog.current) return;
    const previousFocus = document.activeElement;
    removalDialog.current.showModal();
    return () => {
      if (removalDialog.current?.open) removalDialog.current.close();
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) previousFocus.focus();
    };
  }, [principalToRemove]);

  const principalsQuery = useQuery({
    queryKey: ['access-principals'],
    enabled: !!authToken,
    queryFn: ({ signal }) => apiJson<AccessPrincipal[]>(`${apiBase}/v1/auth/access-principals`, authToken, { signal }),
  });
  const projectsQuery = useQuery({
    queryKey: ['projects'],
    enabled: !!authToken,
    queryFn: ({ signal }) => apiJson<ProjectCatalogItem[]>(`${apiBase}/v1/projects`, authToken, { signal }),
  });
  const projectOptions = useMemo(() => (projectsQuery.data ?? []).map((project) => ({ key: project.key, name: project.name })), [projectsQuery.data]);
  const organizationsQuery = useQuery({
    queryKey: ['organizations'],
    enabled: !!authToken,
    queryFn: ({ signal }) => apiJson<OrganizationCatalogItem[]>(`${apiBase}/v1/projects/organizations/catalog`, authToken, { signal }),
  });
  const directoryQuery = useQuery({
    queryKey: ['directory-principals', search],
    enabled: !!authToken && assignmentDialogMode === 'add' && search.length >= 2,
    staleTime: 60_000,
    retry: false,
    queryFn: ({ signal }) => {
      const parameters = new URLSearchParams({ search });
      return apiJson<DirectoryPrincipalPage>(`${apiBase}/v1/auth/directory-principals?${parameters}`, authToken, { signal });
    },
  });

  const normalizedSearchInput = searchInput.trim();
  useEffect(() => {
    setActiveResultIndex(-1);
    if (skipNextSearch.current) {
      skipNextSearch.current = false;
      setSearch('');
      return;
    }
    if (normalizedSearchInput.length < 2) {
      setSearch('');
      setComboboxOpen(false);
      return;
    }
    const timeout = window.setTimeout(() => {
      setSearch(normalizedSearchInput);
      setComboboxOpen(true);
    }, 300);
    return () => window.clearTimeout(timeout);
  }, [normalizedSearchInput]);

  const refresh = () =>
    Promise.all([queryClient.invalidateQueries({ queryKey: ['access-principals'] }), queryClient.invalidateQueries({ queryKey: ['me'] }), queryClient.invalidateQueries({ queryKey: ['audit'] })]);

  const loadAssignment = (principal: AccessPrincipal) => {
    setAssignment({
      kind: principal.kind,
      principal_id: principal.principal_id,
      object_id: principal.object_id ?? '',
      display_name: principal.display_name,
      email: principal.email,
      role: principal.role,
      is_active: principal.is_active,
      read_only: principal.read_only,
      organization_ids: principal.grants.filter((grant) => grant.scope_type === 'organization').map((grant) => grant.scope_id),
      project_keys: principal.grants.filter((grant) => grant.scope_type === 'project').map((grant) => grant.scope_id),
    });
  };

  const openAdd = () => {
    assignmentMutation.reset();
    deleteMutation.reset();
    setAssignment(emptyAssignment);
    setSearchInput('');
    setSearch('');
    setComboboxOpen(false);
    setAssignmentDialogMode('add');
  };

  const editConfigured = (principal: AccessPrincipal) => {
    assignmentMutation.reset();
    deleteMutation.reset();
    loadAssignment(principal);
    setAssignmentDialogMode('edit');
  };

  const selectDirectoryPrincipal = (principal: DirectoryPrincipal) => {
    const configured = principals.find((item) => item.kind === principal.kind && item.object_id === principal.object_id);
    if (configured) {
      loadAssignment(configured);
    } else {
      setAssignment({
        ...emptyAssignment,
        kind: principal.kind,
        object_id: principal.object_id,
        display_name: principal.display_name,
        email: principal.email,
        is_active: principal.is_active,
      });
    }
    skipNextSearch.current = searchInput !== principal.display_name;
    setSearchInput(principal.display_name);
    setComboboxOpen(false);
  };

  const assignmentMutation = useMutation({
    mutationFn: async (value: Assignment) => {
      const body = JSON.stringify({ role: value.role, is_active: value.is_active, organization_ids: value.organization_ids, project_keys: value.project_keys });
      if (value.kind === 'local') {
        await apiFetch(`${apiBase}/v1/auth/users/${encodeURIComponent(value.principal_id)}`, authToken, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ role: value.role, is_active: value.is_active }),
        });
        return apiFetch(`${apiBase}/v1/auth/users/${encodeURIComponent(value.principal_id)}/grants`, authToken, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body,
        });
      }
      return apiFetch(`${apiBase}/v1/auth/access-principals/${value.kind}/${encodeURIComponent(value.object_id)}`, authToken, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body,
      });
    },
    onSuccess: async () => {
      setAssignmentDialogMode(null);
      notify({ message: 'Access assignment saved.' });
      await refresh();
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (value: AccessPrincipal) =>
      apiFetch(
        value.kind === 'local'
          ? `${apiBase}/v1/auth/users/${encodeURIComponent(value.principal_id)}`
          : `${apiBase}/v1/auth/access-principals/${value.kind}/${encodeURIComponent(value.object_id ?? '')}`,
        authToken,
        { method: 'DELETE' },
      ),
    onSuccess: async () => {
      setPrincipalToRemove(null);
      notify({ message: 'Access assignment removed.' });
      await refresh();
    },
  });

  const addLocalMutation = useMutation({
    mutationFn: () => {
      const value = { ...newLocalUser, full_name: newLocalUser.full_name.trim(), email: newLocalUser.email.trim().toLowerCase(), password: newLocalUser.password.trim() };
      if (!value.full_name || !value.email || value.password.length < 8) throw new Error('Name, email and a password of at least 8 characters are required.');
      return apiFetch(`${apiBase}/v1/auth/users`, authToken, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(value),
      });
    },
    onSuccess: async () => {
      setLocalUserDialogOpen(false);
      setNewLocalUser({ full_name: '', email: '', password: '', role: 'default' });
      notify({ message: 'Local user created.' });
      await refresh();
    },
  });

  const passwordMutation = useMutation({
    mutationFn: () => {
      if (!passwordForm.user_id || passwordForm.password.trim().length < 8) throw new Error('Select a user and provide at least 8 characters.');
      return apiFetch(`${apiBase}/v1/auth/users/${encodeURIComponent(passwordForm.user_id)}/password`, authToken, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ new_password: passwordForm.password.trim() }),
      });
    },
    onSuccess: async () => {
      setPasswordForm({ user_id: '', password: '' });
      notify({ message: 'Password updated.' });
      await refresh();
    },
  });

  const principals = principalsQuery.data ?? [];
  const suggestions = search === normalizedSearchInput ? (directoryQuery.data?.items ?? []) : [];
  const autocompleteOpen = comboboxOpen && normalizedSearchInput.length >= 2;
  const localUsers = principals.filter((principal) => principal.kind === 'local');
  const assignmentSelected = !!assignment.object_id || !!assignment.principal_id;
  const noScopeSelected = assignment.organization_ids.length === 0 && assignment.project_keys.length === 0;
  const readError = principalsQuery.error ?? projectsQuery.error ?? organizationsQuery.error;
  const pending = assignmentMutation.isPending || deleteMutation.isPending || addLocalMutation.isPending || passwordMutation.isPending;

  return (
    <fieldset className="iam-fieldset" disabled={pending}>
      <legend className="sr-only">User and group access</legend>
      {readError && (
        <p className="feedback error" role="alert">
          {readError.message}
        </p>
      )}

      <div className="iam-summary-row">
        <div className="iam-summary-card">
          <strong>{principals.length}</strong>
          <span>Configured principals</span>
        </div>
        <div className="iam-summary-card">
          <strong>{principals.filter((principal) => principal.kind === 'entra-group').length}</strong>
          <span>Entra groups</span>
        </div>
      </div>

      <div className="iam-grid">
        <section className="auth-card iam-card">
          <div className="iam-card-heading">
            <div>
              <h3>Configured users and groups</h3>
              <p>Manage roles and project access from one focused dialog.</p>
            </div>
            <Button className="primary" type="button" onClick={openAdd}>
              Add user or group
            </Button>
          </div>
          {principalsQuery.isLoading && <p role="status">Loading access assignments…</p>}
          <Grid<AccessPrincipal>
            ariaLabel="Configured users and Entra groups"
            className="table-wrap iam-user-grid"
            rows={principals}
            rowKey={(principal) => `${principal.kind}:${principal.principal_id}`}
            columns={[
              {
                id: 'principal',
                header: 'Principal',
                rowHeader: true,
                render: (principal) => (
                  <span className="iam-principal-identity">
                    <PrincipalAvatar principal={principal} apiBase={apiBase} authToken={authToken} />
                    <strong>{principal.display_name}</strong>
                  </span>
                ),
                tooltip: (principal) => `${principal.display_name} · ${principal.email ?? 'No email address'}`,
                width: 260,
              },
              { id: 'type', header: 'Type', value: (principal) => principal.kind, render: (principal) => <span className="iam-chip type">{principal.kind}</span> },
              { id: 'role', header: 'Role', value: (principal) => principal.role, render: (principal) => <span className={`iam-chip role ${principal.role}`}>{principal.role}</span> },
              {
                id: 'status',
                header: 'Status',
                value: (principal) => (principal.is_active ? 'active' : 'disabled'),
                render: (principal) => <span className={`iam-chip status ${principal.is_active ? 'active' : 'disabled'}`}>{principal.is_active ? 'active' : 'disabled'}</span>,
              },
              { id: 'access', header: 'Explicit access', value: accessSummary, className: 'iam-subtext', width: 240 },
              {
                id: 'actions',
                header: 'Actions',
                render: (principal) => (
                  <div className="iam-row-actions">
                    <Button type="button" onClick={() => editConfigured(principal)}>
                      {principal.read_only ? 'View' : 'Edit'}
                    </Button>
                    {!principal.read_only && (
                      <Button
                        className="danger"
                        type="button"
                        onClick={() => {
                          deleteMutation.reset();
                          setPrincipalToRemove(principal);
                        }}
                      >
                        Remove
                      </Button>
                    )}
                  </div>
                ),
              },
            ]}
          />
        </section>

        {localLoginVisible && (
          <section className="auth-card iam-card">
            <div className="iam-card-heading">
              <div>
                <h3>Local fallback accounts</h3>
                <p>Available only while local account administration is enabled.</p>
              </div>
              <Button
                type="button"
                onClick={() => {
                  addLocalMutation.reset();
                  setLocalUserDialogOpen(true);
                }}
              >
                Create local user
              </Button>
            </div>
            <details className="admin-disclosure">
              <summary>Reset a local password</summary>
              <div className="form-grid compact">
                <label>
                  User
                  <ListBox value={passwordForm.user_id} onChange={(event) => setPasswordForm((current) => ({ ...current, user_id: event.target.value }))}>
                    <option value="">Select user</option>
                    {localUsers.map((user) => (
                      <option key={user.principal_id} value={user.principal_id}>
                        {user.display_name}
                      </option>
                    ))}
                  </ListBox>
                </label>
                <label>
                  New password
                  <TextBox type="password" value={passwordForm.password} minLength={8} onChange={(event) => setPasswordForm((current) => ({ ...current, password: event.target.value }))} />
                </label>
              </div>
              <Button type="button" onClick={() => passwordMutation.mutate()}>
                Reset password
              </Button>
              {passwordMutation.error && (
                <p className="feedback error" role="alert">
                  {passwordMutation.error.message}
                </p>
              )}
            </details>
          </section>
        )}
      </div>

      {assignmentDialogMode && (
        <dialog
          ref={assignmentDialog}
          className="config-modal iam-access-modal"
          aria-labelledby="iam-access-dialog-title"
          onCancel={(event) => {
            event.preventDefault();
            if (!pending) setAssignmentDialogMode(null);
          }}
        >
          <header className="config-modal-header iam-modal-header">
            <div>
              <span className="section-kicker">User access</span>
              <h3 id="iam-access-dialog-title">{assignmentDialogMode === 'add' ? 'Add a user or group' : assignment.read_only ? 'View user access' : 'Edit user access'}</h3>
            </div>
            <DialogCloseButton onClick={() => setAssignmentDialogMode(null)} disabled={pending} />
          </header>

          {assignmentDialogMode === 'add' && !assignmentSelected ? (
            <section className="iam-dialog-search" aria-labelledby="iam-directory-search-title">
              <h4 id="iam-directory-search-title">Find a Microsoft Entra user or group</h4>
              <p>Search by name or email, then configure role and project access.</p>
              <div className="iam-combobox">
                <label htmlFor={`${listboxId}-input`}>Name or email</label>
                <TextBox
                  autoFocus
                  id={`${listboxId}-input`}
                  value={searchInput}
                  onChange={(event) => setSearchInput(event.target.value)}
                  onFocus={() => normalizedSearchInput.length >= 2 && setComboboxOpen(true)}
                  onBlur={() => window.setTimeout(() => setComboboxOpen(false), 0)}
                  onKeyDown={(event) => {
                    if (event.key === 'Escape') {
                      setComboboxOpen(false);
                      setActiveResultIndex(-1);
                      return;
                    }
                    if (!autocompleteOpen || suggestions.length === 0) return;
                    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                      event.preventDefault();
                      const direction = event.key === 'ArrowDown' ? 1 : -1;
                      setActiveResultIndex((current) => (current + direction + suggestions.length) % suggestions.length);
                    } else if (event.key === 'Enter' && activeResultIndex >= 0) {
                      event.preventDefault();
                      selectDirectoryPrincipal(suggestions[activeResultIndex]);
                    }
                  }}
                  role="combobox"
                  aria-autocomplete="list"
                  aria-expanded={autocompleteOpen}
                  aria-controls={listboxId}
                  aria-activedescendant={activeResultIndex >= 0 ? `${listboxId}-option-${activeResultIndex}` : undefined}
                  minLength={2}
                  maxLength={100}
                  autoComplete="off"
                  placeholder="At least 2 characters"
                />
                {autocompleteOpen && (
                  <div className="iam-combobox-popover">
                    {directoryQuery.isFetching && <p role="status">Searching the Entra directory…</p>}
                    {directoryQuery.isError && (
                      <p className="feedback error" role="alert">
                        {directoryQuery.error.message}
                      </p>
                    )}
                    {!directoryQuery.isFetching && directoryQuery.isSuccess && suggestions.length === 0 && <p>No matching user or group.</p>}
                    {suggestions.length > 0 && (
                      <ul id={listboxId} role="listbox" aria-label="Microsoft Entra users and groups">
                        {suggestions.map((principal, index) => (
                          <li
                            id={`${listboxId}-option-${index}`}
                            role="option"
                            aria-selected={activeResultIndex === index}
                            aria-label={`${principal.display_name}, ${principal.kind === 'entra-user' ? 'user' : 'group'}`}
                            className={activeResultIndex === index ? 'active' : ''}
                            key={`${principal.kind}:${principal.object_id}`}
                            onMouseEnter={() => setActiveResultIndex(index)}
                            onMouseDown={(event) => {
                              event.preventDefault();
                              selectDirectoryPrincipal(principal);
                            }}
                          >
                            {principal.kind === 'entra-group' && (
                              <span className="iam-suggestion-icon group" aria-hidden="true">
                                <GroupIcon />
                              </span>
                            )}
                            <span>{principal.display_name}</span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                )}
              </div>
            </section>
          ) : (
            <div className="iam-assignment-form">
              <div className="iam-selected-principal">
                <strong>{assignment.display_name}</strong>
                <span>{assignment.email ?? assignment.object_id}</span>
              </div>

              <label className="iam-role-field">
                Role
                <ListBox value={assignment.role} disabled={assignment.read_only} onChange={(event) => setAssignment((current) => ({ ...current, role: event.target.value as LocalUserRole }))}>
                  <option value="admin">Admin</option>
                  <option value="regular">Regular</option>
                  <option value="default">Default</option>
                </ListBox>
              </label>

              <fieldset className="iam-project-picker" disabled={assignment.read_only}>
                <legend>
                  Projects <InfoHint label="Project access information">If no project or organization is selected, all projects are accessible to this user or group.</InfoHint>
                </legend>
                <ProjectPicker
                  multiple
                  ariaLabel="Projects"
                  options={projectOptions}
                  value={assignment.project_keys}
                  placeholder={assignment.organization_ids.length === 0 ? 'All projects' : 'No individual projects'}
                  emptySelectionHint={noScopeSelected ? 'No selection — all projects will be accessible.' : 'No individual project selected.'}
                  disabled={assignment.read_only}
                  loading={projectsQuery.isPending}
                  onChange={(projectKeys) => setAssignment((current) => ({ ...current, project_keys: projectKeys }))}
                />
              </fieldset>

              <fieldset className="iam-organization-picker" disabled={assignment.read_only}>
                <legend>
                  Organizations <InfoHint label="Organization access information">An organization grants access to every project attached to that CodeInsights organization.</InfoHint>
                </legend>
                {(organizationsQuery.data ?? []).length === 0 ? (
                  <p className="iam-empty-note">No organizations are configured. Production projects are currently managed individually.</p>
                ) : (
                  <div className="iam-organization-list">
                    {(organizationsQuery.data ?? []).map((organization) => {
                      const checked = assignment.organization_ids.includes(organization.id);
                      return (
                        <label className="iam-project-option" key={organization.id}>
                          <Checkbox
                            type="checkbox"
                            checked={checked}
                            onChange={() =>
                              setAssignment((current) => ({
                                ...current,
                                organization_ids: checked ? current.organization_ids.filter((id) => id !== organization.id) : [...current.organization_ids, organization.id],
                              }))
                            }
                          />
                          <span>
                            <strong>{organization.name}</strong>
                            <small>{organization.key}</small>
                          </span>
                        </label>
                      );
                    })}
                  </div>
                )}
              </fieldset>

              {assignmentMutation.error && (
                <p className="feedback error" role="alert">
                  {assignmentMutation.error.message}
                </p>
              )}
              {assignment.read_only && <p className="field-help">This break-glass account is read-only in Entra-only mode.</p>}
              <div className="action-row iam-modal-actions">
                {assignmentDialogMode === 'add' && (
                  <Button
                    type="button"
                    onClick={() => {
                      setAssignment(emptyAssignment);
                      setSearchInput('');
                    }}
                  >
                    Back to search
                  </Button>
                )}
                <Button className="primary" type="button" disabled={assignment.read_only} onClick={() => assignmentMutation.mutate(assignment)}>
                  {assignmentMutation.isPending ? 'Saving…' : 'Save assignment'}
                </Button>
              </div>
            </div>
          )}
        </dialog>
      )}

      {principalToRemove && (
        <dialog
          ref={removalDialog}
          className="config-modal iam-removal-modal"
          aria-labelledby="iam-removal-title"
          onCancel={(event) => {
            event.preventDefault();
            if (!deleteMutation.isPending) setPrincipalToRemove(null);
          }}
        >
          <header className="config-modal-header">
            <h3 id="iam-removal-title">Remove access?</h3>
            <DialogCloseButton onClick={() => setPrincipalToRemove(null)} disabled={deleteMutation.isPending} />
          </header>
          <p>
            Remove <strong>{principalToRemove.display_name}</strong> from the configured users and groups?{' '}
            {principalToRemove.kind === 'local' ? 'This local account will be deleted.' : 'This principal will no longer have access through this assignment.'}
          </p>
          {deleteMutation.error && (
            <p className="feedback error" role="alert">
              {deleteMutation.error.message}
            </p>
          )}
          <div className="action-row iam-modal-actions">
            <Button type="button" onClick={() => setPrincipalToRemove(null)} disabled={deleteMutation.isPending}>
              Cancel
            </Button>
            <Button className="danger" type="button" onClick={() => deleteMutation.mutate(principalToRemove)} disabled={deleteMutation.isPending}>
              {deleteMutation.isPending ? 'Removing…' : 'Remove access'}
            </Button>
          </div>
        </dialog>
      )}

      {localUserDialogOpen && (
        <dialog
          ref={localUserDialog}
          className="config-modal iam-local-user-modal"
          aria-labelledby="iam-local-user-title"
          onCancel={(event) => {
            event.preventDefault();
            if (!addLocalMutation.isPending) setLocalUserDialogOpen(false);
          }}
        >
          <header className="config-modal-header iam-modal-header">
            <div>
              <span className="section-kicker">Fallback access</span>
              <h3 id="iam-local-user-title">Create a local user</h3>
            </div>
            <DialogCloseButton onClick={() => setLocalUserDialogOpen(false)} disabled={addLocalMutation.isPending} />
          </header>
          <form
            className="form-grid compact"
            onSubmit={(event) => {
              event.preventDefault();
              addLocalMutation.mutate();
            }}
          >
            <label>
              Full name
              <TextBox autoFocus value={newLocalUser.full_name} onChange={(event) => setNewLocalUser((current) => ({ ...current, full_name: event.target.value }))} />
            </label>
            <label>
              Email
              <TextBox type="email" value={newLocalUser.email} onChange={(event) => setNewLocalUser((current) => ({ ...current, email: event.target.value }))} />
            </label>
            <label>
              Password
              <TextBox type="password" value={newLocalUser.password} minLength={8} onChange={(event) => setNewLocalUser((current) => ({ ...current, password: event.target.value }))} />
            </label>
            <label>
              Role
              <ListBox value={newLocalUser.role} onChange={(event) => setNewLocalUser((current) => ({ ...current, role: event.target.value as LocalUserRole }))}>
                <option value="admin">Admin</option>
                <option value="regular">Regular</option>
                <option value="default">Default</option>
              </ListBox>
            </label>
            <p className="field-help">A new local user starts with access to all projects. Edit the account afterward to restrict it.</p>
            {addLocalMutation.error && (
              <p className="feedback error" role="alert">
                {addLocalMutation.error.message}
              </p>
            )}
            <div className="action-row iam-modal-actions">
              <Button type="button" onClick={() => setLocalUserDialogOpen(false)} disabled={addLocalMutation.isPending}>
                Cancel
              </Button>
              <Button className="primary" type="submit">
                {addLocalMutation.isPending ? 'Creating…' : 'Create local user'}
              </Button>
            </div>
          </form>
        </dialog>
      )}
    </fieldset>
  );
}
