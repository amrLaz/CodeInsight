import type { AdminPrimaryTab, AdminSection, AppTab } from '../types/api';

export const TABS: Array<{ id: AppTab; label: string }> = [
  { id: 'projects', label: 'Projects' },
  { id: 'issues', label: 'Issues' },
  { id: 'rules', label: 'Rules' },
  { id: 'quality-gates', label: 'Quality Gates' },
  { id: 'administration', label: 'Administration' },
];

export const ADMIN_PRIMARY_TABS: Array<{ id: AdminPrimaryTab; label: string }> = [
  { id: 'configuration', label: 'Configuration' },
  { id: 'security', label: 'Security' },
  { id: 'projects', label: 'Projects' },
];

export const ADMIN_NAV_BY_TAB: Record<AdminPrimaryTab, Array<{ id: AdminSection; label: string }>> = {
  configuration: [
    { id: 'general-settings', label: 'General' },
    { id: 'data-retention', label: 'Data Retention' },
    { id: 'devops-integrations', label: 'Integrations' },
    { id: 'notifications', label: 'Notifications' },
    { id: 'license-policy', label: 'Licenses' },
  ],
  security: [
    { id: 'access-management', label: 'Authentication' },
    { id: 'pat-management', label: 'PAT Management' },
    { id: 'audit-log', label: 'Audit Log' },
  ],
  projects: [
    { id: 'new-code-settings', label: 'New Code' },
    { id: 'sla-settings', label: 'SLA' },
    { id: 'work-item-settings', label: 'Work items' },
    { id: 'project-management', label: 'Analysis cleanup' },
  ],
};
