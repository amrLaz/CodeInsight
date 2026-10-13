import { lazy, type Dispatch, type SetStateAction } from 'react';
import { API_BASE } from '@/config/env';
import { ADMIN_PRIMARY_TABS, ADMIN_NAV_BY_TAB } from '@/config/navigation';
import ProjectPicker from '@/components/common/ProjectPicker';
import { Button, ListBox, TextBox } from '@/components/ui';
import type { AdminSection, ProjectIntegrationRecord, SessionUser } from '@/types/api';

const AuthProviderSettings = lazy(() => import('./AuthProviderSettings'));
const CiTokens = lazy(() => import('./CiTokens'));
const DevOpsIntegrations = lazy(() => import('./DevOpsIntegrations'));
const UsersAccess = lazy(() => import('./UsersAccess'));
const AuditLog = lazy(() => import('./AuditLog'));
const PlatformSettingsPanel = lazy(() => import('./PlatformSettings'));
const Notifications = lazy(() => import('./Notifications'));
const NewCodeSettings = lazy(() => import('./NewCodeSettings'));
const SlaSettings = lazy(() => import('./SlaSettings'));
const WorkItemSettings = lazy(() => import('./WorkItemSettings'));
const LicensePolicySettings = lazy(() => import('./LicensePolicySettings'));

type AdminForm = {
  project_key: string;
  repoProvider: ProjectIntegrationRecord['repo_provider'];
  repoUrl: string;
  pipelineProvider: ProjectIntegrationRecord['pipeline_provider'];
  pipelineUrl: string;
};
type Props = {
  activeAdminSection: AdminSection;
  setActiveAdminSection: Dispatch<SetStateAction<AdminSection>>;
  adminReadError: string | undefined;
  sessionUser: SessionUser | null;
  localLoginVisible: boolean;
  projectPickerOptions: Array<{ key: string }>;
  adminForm: AdminForm;
  setAdminForm: Dispatch<SetStateAction<AdminForm>>;
  savingProjectMeta: boolean;
  saveProjectMeta: () => void;
  azureDevOpsConfigs: Array<{ config_name: string }>;
  deleteTarget: string | null;
  setDeleteTarget: Dispatch<SetStateAction<string | null>>;
  setShowDeleteModal: Dispatch<SetStateAction<boolean>>;
};

export default function Administration({
  activeAdminSection,
  setActiveAdminSection,
  adminReadError,
  sessionUser,
  localLoginVisible,
  projectPickerOptions,
  adminForm,
  setAdminForm,
  savingProjectMeta,
  saveProjectMeta,
  azureDevOpsConfigs,
  deleteTarget,
  setDeleteTarget,
  setShowDeleteModal,
}: Props) {
  return (
    <section className="admin-screen">
      <div className="admin-body">
        <aside className="admin-side-menu">
          <nav aria-label="Administration sections">
            {ADMIN_PRIMARY_TABS.map((group) => (
              <section className="admin-nav-group" aria-labelledby={`admin-nav-${group.id}`} key={group.id}>
                <h2 id={`admin-nav-${group.id}`} className="admin-nav-group-label">
                  {group.label}
                </h2>
                <div className="admin-nav-items">
                  {ADMIN_NAV_BY_TAB[group.id].map((item) => (
                    <Button
                      key={item.id}
                      className={activeAdminSection === item.id ? 'active' : ''}
                      aria-current={activeAdminSection === item.id ? 'page' : undefined}
                      onClick={() => setActiveAdminSection(item.id)}
                    >
                      {item.label}
                    </Button>
                  ))}
                </div>
              </section>
            ))}
          </nav>
        </aside>

        <label className="admin-mobile-nav">
          Administration section
          <ListBox value={activeAdminSection} onChange={(event) => setActiveAdminSection(event.target.value as AdminSection)}>
            {ADMIN_PRIMARY_TABS.map((group) => (
              <optgroup label={group.label} key={group.id}>
                {ADMIN_NAV_BY_TAB[group.id].map((item) => (
                  <option value={item.id} key={item.id}>
                    {item.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </ListBox>
        </label>

        <section className="admin-content">
          {adminReadError && (
            <p className="feedback error" role="alert">
              {adminReadError}
            </p>
          )}
          {(activeAdminSection === 'general-settings' || activeAdminSection === 'data-retention') && <PlatformSettingsPanel authToken={sessionUser?.token} activeSection={activeAdminSection} />}

          {activeAdminSection === 'access-management' && (
            <>
              <AuthProviderSettings authToken={sessionUser?.token} />
              <UsersAccess apiBase={API_BASE} authToken={sessionUser?.token} localLoginVisible={localLoginVisible} />
            </>
          )}

          {activeAdminSection === 'pat-management' && <CiTokens authToken={sessionUser?.token} />}

          {activeAdminSection === 'audit-log' && <AuditLog authToken={sessionUser?.token} />}
          {activeAdminSection === 'notifications' && <Notifications authToken={sessionUser?.token} />}
          {activeAdminSection === 'license-policy' && <LicensePolicySettings authToken={sessionUser?.token} />}
          {activeAdminSection === 'new-code-settings' && <NewCodeSettings authToken={sessionUser?.token} projects={projectPickerOptions.map((option) => option.key)} />}
          {activeAdminSection === 'sla-settings' && <SlaSettings authToken={sessionUser?.token} projects={projectPickerOptions.map((option) => option.key)} />}
          {activeAdminSection === 'work-item-settings' && <WorkItemSettings authToken={sessionUser?.token} projects={projectPickerOptions.map((option) => option.key)} />}

          {activeAdminSection === 'devops-integrations' && (
            <>
              <DevOpsIntegrations authToken={sessionUser?.token} />
              <section className="admin-subsection" aria-labelledby="project-links-title">
                <h3 id="project-links-title">Project repository links</h3>
                <p>Associate a project with its source repository for exact file links and authorized pull-request feedback. The pipeline URL is an optional shortcut.</p>
                <div className="form-grid compact">
                  <ProjectPicker
                    label="Project"
                    options={projectPickerOptions}
                    value={adminForm.project_key}
                    onChange={(projectKey) => setAdminForm((current) => ({ ...current, project_key: projectKey }))}
                  />
                  <label>
                    Repository URL
                    <ListBox
                      value={adminForm.repoProvider}
                      onChange={(event) =>
                        setAdminForm((current) => ({
                          ...current,
                          repoProvider: event.target.value as ProjectIntegrationRecord['repo_provider'],
                        }))
                      }
                    >
                      <option value="github">GitHub</option>
                      <option value="azure-devops">Azure Repos</option>
                      <option value="other">Other</option>
                    </ListBox>
                    <TextBox
                      placeholder="https://github.com/org/repo"
                      value={adminForm.repoUrl}
                      onChange={(event) =>
                        setAdminForm((current) => ({
                          ...current,
                          repoUrl: event.target.value,
                        }))
                      }
                    />
                  </label>
                  <label>
                    Pipeline URL
                    <ListBox
                      value={adminForm.pipelineProvider}
                      onChange={(event) =>
                        setAdminForm((current) => ({
                          ...current,
                          pipelineProvider: event.target.value as ProjectIntegrationRecord['pipeline_provider'],
                        }))
                      }
                    >
                      <option value="azure-devops">Azure DevOps</option>
                      <option value="github-actions">GitHub Actions</option>
                      <option value="other">Other</option>
                    </ListBox>
                    <TextBox
                      placeholder="https://dev.azure.com/..."
                      value={adminForm.pipelineUrl}
                      onChange={(event) =>
                        setAdminForm((current) => ({
                          ...current,
                          pipelineUrl: event.target.value,
                        }))
                      }
                    />
                  </label>
                </div>
                <div className="action-row">
                  <Button className="primary" type="button" onClick={() => void saveProjectMeta()} disabled={!adminForm.project_key || savingProjectMeta}>
                    {savingProjectMeta ? 'Saving...' : 'Save links'}
                  </Button>
                  <Button type="button" disabled={!adminForm.repoUrl} onClick={() => window.open(adminForm.repoUrl, '_blank', 'noopener,noreferrer')}>
                    Open repo
                  </Button>
                  <Button type="button" disabled={!adminForm.pipelineUrl} onClick={() => window.open(adminForm.pipelineUrl, '_blank', 'noopener,noreferrer')}>
                    Open pipeline
                  </Button>
                </div>

                <div className="integration-context">
                  <strong>Azure DevOps feedback</strong>
                  <p>
                    {azureDevOpsConfigs.length > 0
                      ? `Connected through ${azureDevOpsConfigs[0].config_name}.`
                      : 'No organization connected. Scans still publish, but pull-request feedback is inactive.'}
                  </p>
                </div>
              </section>
            </>
          )}

          {activeAdminSection === 'project-management' && (
            <>
              <h2>Analysis cleanup</h2>

              <div className="auth-card">
                <h3>Delete project analyses</h3>
                <p className="supporting-copy">Permanently delete every analysis and ingestion attached to one project. This action cannot be undone.</p>

                <div className="form-grid compact">
                  <ProjectPicker label="Project" options={projectPickerOptions} clearable value={deleteTarget ?? ''} onChange={(projectKey) => setDeleteTarget(projectKey || null)} />
                </div>

                {deleteTarget && (
                  <div className="danger-zone">
                    <p>
                      Delete all analyses for <strong>{deleteTarget}</strong>
                    </p>
                    <Button className="danger" type="button" onClick={() => setShowDeleteModal(true)}>
                      Delete project analyses
                    </Button>
                  </div>
                )}
              </div>
            </>
          )}
        </section>
      </div>
    </section>
  );
}
