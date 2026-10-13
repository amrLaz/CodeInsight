import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { QueryClient } from '@tanstack/react-query';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('admin data contracts', async () => {
  function expression(file, name, context) {
    const source = readFileSync(new URL(`./features/administration/${file}.tsx`, sourceUrl), 'utf8');
    const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    let initializer;
    function visit(node) {
      if (ts.isVariableDeclaration(node) && node.name.getText(tree) === name) initializer = node.initializer.getText(tree);
      ts.forEachChild(node, visit);
    }
    visit(tree);
    assert.ok(initializer, `${file}.${name} is present`);
    const { outputText } = ts.transpileModule(`(${initializer})`, { compilerOptions: { target: ts.ScriptTarget.ES2020 } });
    return vm.runInNewContext(outputText, { useInfiniteQuery: (value) => value, useMutation: (value) => value, URLSearchParams, ...context });
  }

  const signal = new AbortController().signal;
  let requested;
  const audit = expression('AuditLog', 'auditQuery', {
    auditFilters: { action: 'user.login', actor: 'reader@example.test' },
    authToken: 'session-test',
    API_BASE: '/api',
    apiJson: async (...args) => {
      requested = args;
      return { entries: [], total: 0 };
    },
  });
  await audit.queryFn({ signal, pageParam: 50 });
  assert.equal(requested[0], '/api/v1/audit-logs?limit=50&offset=50&action=user.login&actor=reader%40example.test');
  assert.equal(requested[2].signal, signal);
  assert.equal(audit.getNextPageParam({ entries: [1], total: 2 }, [{ entries: [1] }]), 1);
  assert.equal(audit.getNextPageParam({ entries: [2], total: 2 }, [{ entries: [1] }, { entries: [2] }]), undefined);
  assert.equal(audit.getNextPageParam({ entries: [], total: 99 }, [{ entries: [] }]), undefined);

  const client = new QueryClient({ defaultOptions: { mutations: { retry: false, gcTime: 0 } } });
  let failure = true;
  let invalidations = 0;
  let feedback = null;
  const notifications = expression('Notifications', 'channelMutation', {
    API_BASE: '/api',
    authToken: 'session-test',
    apiFetch: async () => {
      if (failure) throw new Error('HTTP 403');
    },
    apiJson: async () => {
      if (failure) throw new Error('HTTP 500');
      return { message: 'Test sent.' };
    },
    queryClient: {
      invalidateQueries: async () => {
        invalidations++;
      },
    },
    notify: (value) => {
      feedback = value.message;
    },
  });
  for (const action of ['delete', 'toggle', 'test']) {
    await assert.rejects(client.getMutationCache().build(client, notifications).execute({ action, id: 'channel-1', enabled: false }), /HTTP/);
    assert.equal(feedback, null, 'Failed actions must not display success');
    assert.equal(invalidations, 0, 'Failed actions must not invalidate as success');
  }
  failure = false;
  await client.getMutationCache().build(client, notifications).execute({ action: 'delete', id: 'channel-1' });
  assert.equal(feedback, 'Channel deleted.');
  assert.equal(invalidations, 1);

  const generation = expression('CiTokens', 'generateMutation', {
    API_BASE: '/api',
    authToken: 'session-test',
    platformTokenInput: ' current-test ',
    platformTokenName: ' CI ',
    platformTokenTtlDays: 90,
    platformTokenProjectKeys: ['project-1'],
    CI_TOKEN_SCOPES: ['analysis:read', 'analysis:write', 'integration:write'],
    apiJson: async (...args) => {
      requested = args;
      return { token: 'generated-test' };
    },
    setPlatformTokenInput: () => {},
    invalidateTokens: async () => {},
  });
  assert.equal(await generation.mutationFn('rotate'), 'generated-test');
  assert.equal(requested[2].headers['X-Current-Platform-Token'], 'current-test');
  assert.deepEqual(JSON.parse(requested[2].body), {
    token_name: 'CI',
    ttl_days: 90,
    require_current_token: true,
    project_keys: ['project-1'],
    scopes: ['analysis:read', 'analysis:write', 'integration:write'],
  });
  assert.equal(generation.gcTime, 0, 'Generated secrets must not survive component removal in an inactive mutation');
  assert.equal(generation.mutationKey, undefined);
  client.clear();

  const appSource = [
    './workspace/Workspace.tsx', './features/administration/Administration.tsx', './config/navigation.ts',
  ].map((path) => readFileSync(new URL(path, sourceUrl), 'utf8')).join('\n');
  const usersSource = readFileSync(new URL('./features/administration/UsersAccess.tsx', sourceUrl), 'utf8');
  const projectPickerSource = readFileSync(new URL('./components/common/ProjectPicker.tsx', sourceUrl), 'utf8');
  const providersSource = readFileSync(new URL('./features/administration/AuthProviderSettings.tsx', sourceUrl), 'utf8');
  const devOpsSource = readFileSync(new URL('./features/administration/DevOpsIntegrations.tsx', sourceUrl), 'utf8');
  const settingsSource = readFileSync(new URL('./features/administration/PlatformSettings.tsx', sourceUrl), 'utf8');
  const newCodeSettingsSource = readFileSync(new URL('./features/administration/NewCodeSettings.tsx', sourceUrl), 'utf8');
  const vitestConfigSource = readFileSync(new URL('../vitest.config.ts', sourceUrl), 'utf8');
  const stylesSource = readFileSync(new URL('./styles/shared-details.css', sourceUrl), 'utf8');
  assert.doesNotMatch(appSource, /admin-top-nav/, 'Administration uses one grouped navigation instead of stacked tab rows');
  assert.match(appSource, /className="admin-nav-group"/, 'Administration navigation is grouped');
  assert.match(appSource, /className="admin-mobile-nav"/, 'Administration has one mobile section selector');
  assert.match(appSource, /Project repository links/, 'Repository links live in the integrations view');
  assert.doesNotMatch(appSource, /analysis-scope|activeAdminSection === 'project-links'/, 'Unused administration destinations are removed');
  assert.doesNotMatch(devOpsSource, /devops-provider-tabs|Bitbucket|GitLab/, 'Integrations do not advertise unavailable providers');
  assert.doesNotMatch(settingsSource, /Source File Exclusions/, 'Unenforced exclusion settings are not presented as active configuration');
  assert.match(appSource, /new-code-settings[\s\S]*?New Code/, 'Administration exposes New Code configuration in the Projects group');
  assert.match(appSource, /sla-settings[\s\S]*?SLA/, 'Administration exposes SLA policies in the Projects group');
  assert.match(appSource, /work-item-settings[\s\S]*?Work items/, 'Administration exposes work item settings in the Projects group');
  assert.match(appSource, /license-policy[\s\S]*?Licenses/, 'Administration exposes the licence policy in the Configuration group');
  assert.match(newCodeSettingsSource, /global: \['previous_version', 'number_of_days'\]/, 'Global New Code modes are restricted');
  assert.match(newCodeSettingsSource, /project: \['previous_version', 'number_of_days', 'reference_branch'\]/, 'Project New Code modes are restricted');
  assert.match(newCodeSettingsSource, /branch: \['previous_version', 'number_of_days', 'reference_branch', 'specific_analysis'\]/, 'Branch New Code modes include Specific analysis');
  assert.match(newCodeSettingsSource, /min="1"\s+max="90"/, 'Number of days is visibly constrained from 1 to 90');
  assert.match(newCodeSettingsSource, /\/v1\/new-code\/definitions/, 'New Code definitions use their dedicated API');
  assert.match(newCodeSettingsSource, /\/v1\/new-code\/projects\/\$\{encodeURIComponent\(projectKey\)\}\/main-branch/, 'Project main branch uses its dedicated API');
  assert.match(newCodeSettingsSource, /latest completed snapshot[\s\S]*?lag behind the live Git branch/, 'Reference branch help states the snapshot lag boundary');
  assert.match(vitestConfigSource, /include: \['src\/\*\*\/\*\.\{ts,tsx\}'\]/, 'Vitest coverage includes source files that tests never import');
  assert.match(providersSource, /className="auth-provider-picker"/, 'Authentication providers use the native form selector');
  assert.match(usersSource, /new URLSearchParams\(\{ search \}\)/, 'Administration sends one mixed Entra search without a type selector');
  assert.doesNotMatch(usersSource, /searchKind|setSearchKind|<option value="user">User<\/option>/, 'The autocomplete has no user/group type field');
  assert.match(usersSource, /normalizedSearchInput\.length < 2/, 'Directory search waits for two normalized characters');
  assert.match(usersSource, /window\.setTimeout\([\s\S]*?, 300\)/, 'Directory search is debounced by 300 ms');
  assert.match(usersSource, /staleTime: 60_000/, 'Directory suggestions remain fresh for one minute');
  assert.match(usersSource, /queryFn: \(\{ signal \}\)[\s\S]*?authToken, \{ signal \}/, 'A superseded query forwards TanStack cancellation to fetch');
  assert.match(usersSource, /role="combobox"[\s\S]*?aria-autocomplete="list"[\s\S]*?aria-expanded=[\s\S]*?aria-activedescendant=/, 'The search exposes an accessible combobox contract');
  assert.match(usersSource, /event\.key === 'ArrowDown'[\s\S]*?event\.key === 'ArrowUp'[\s\S]*?event\.key === 'Enter'/, 'The autocomplete supports arrow and Enter selection');
  assert.match(usersSource, /event\.key === 'Escape'/, 'Escape closes the autocomplete');
  assert.match(usersSource, /role="listbox"[\s\S]*?role="option"[\s\S]*?aria-selected=/, 'Suggestions expose listbox and option semantics');
  assert.match(usersSource, /const configured = principals\.find[\s\S]*?if \(configured\) \{\s*loadAssignment\(configured\)/, 'Selecting an existing principal restores its configured assignment');
  assert.match(usersSource, /\/v1\/auth\/access-principals\/\$\{value\.kind\}/, 'Entra access assignments use the unified atomic endpoint');
  assert.doesNotMatch(usersSource, /JSON\.stringify\(\{[^}]*display_name|JSON\.stringify\(\{[^}]*email/, 'Canonical Entra names and emails are never trusted from the browser');
  assert.match(usersSource, /localLoginVisible &&/, 'Local account administration is hidden in Entra-only mode');
  assert.match(usersSource, /IntersectionObserver[\s\S]*?rootMargin: '200px'/, 'Entra photos load only when a configured row approaches the viewport');
  assert.match(usersSource, /access-principal-photo[\s\S]*?staleTime: 5 \* 60_000[\s\S]*?gcTime: 5 \* 60_000/, 'Configured Entra photo blobs are cached for five minutes');
  assert.match(usersSource, /URL\.createObjectURL[\s\S]*?URL\.revokeObjectURL/, 'Configured Entra photo Blob URLs are revoked after use');
  assert.match(usersSource, /principal\.kind === 'entra-group' \? <GroupIcon \/>/, 'Groups use the generic group icon without a photo request');
  assert.match(usersSource, /principal\.email \?\? 'No email address'/, 'The identity tooltip explicitly handles a missing email address');
  assert.match(
    usersSource,
    /tooltip: \(principal\) => `\$\{principal\.display_name\} · \$\{principal\.email \?\? 'No email address'\}`/,
    'The shared Grid exposes the complete identity in its overflow tooltip',
  );
  assert.match(usersSource, /id: 'type', header: 'Type'/, 'The configured-principals Grid retains its Type column');
  assert.match(usersSource, /<dialog[\s\S]*?className="config-modal iam-access-modal"/, 'User add and edit use a native modal dialog');
  assert.match(usersSource, /onClick=\{openAdd\}[\s\S]*?>\s*Add user or group/, 'The configured-principals view exposes one focused add action');
  assert.match(usersSource, /assignmentDialogMode === 'add'[\s\S]*?role="combobox"/, 'The add dialog includes the Entra directory search');
  assert.match(usersSource, /Projects\s*<InfoHint[\s\S]*?no project or organization is selected/, 'Project access explains the all-projects empty selection');
  assert.match(
    projectPickerSource,
    /className="project-picker-trigger"[\s\S]*?aria-expanded=\{open\}[\s\S]*?type="search"[\s\S]*?placeholder=\{searchPlaceholder\}/,
    'Projects use a dropdown with integrated name or key search',
  );
  assert.match(
    projectPickerSource,
    /className="project-picker-panel"[\s\S]*?project-picker-options \$\{props\.multiple \? 'multiple' : 'single'\}[\s\S]*?role=\{props\.multiple \|\| singleWithActions \? 'group' : 'listbox'\}[\s\S]*?type="checkbox"/,
    'The shared project dropdown uses listbox single-selection and native checkbox multi-selection',
  );
  assert.match(usersSource, /<ProjectPicker[\s\S]*?multiple[\s\S]*?value=\{assignment\.project_keys\}/, 'Access management reuses the shared project picker');
  assert.doesNotMatch(usersSource, />\s*Individual projects\s*</, 'The obsolete Individual projects label is removed');
  assert.match(usersSource, /No organizations are configured\. Production projects are currently managed individually\./, 'An empty organization catalogue explains the production state');
  assert.doesNotMatch(usersSource, /className="iam-checkbox iam-active-checkbox"/, 'The access dialog does not expose an Active checkbox');
  assert.match(usersSource, /setPrincipalToRemove\(principal\)[\s\S]*?Remove access\?/, 'Configured principals can be removed from their row after confirmation');
  assert.doesNotMatch(usersSource, />\s*Access Management Matrix\s*</, 'The duplicated role matrix is removed');
  assert.match(
    stylesSource,
    /@media \(max-width: 1025px\)[\s\S]*?\.admin-side-menu \{\s*display: none;[\s\S]*?\.admin-mobile-nav \{\s*display: grid;/,
    'Administration switches to one responsive selector before its content is crushed',
  );
  console.log('Admin query and mutation checks passed.');
});
