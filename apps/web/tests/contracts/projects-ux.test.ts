import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import vm from 'node:vm';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('projects ux contracts', async () => {
const projects = readFileSync(new URL('./features/projects/Projects.tsx', sourceUrl), 'utf8');
const history = readFileSync(new URL('./features/projects/components/MetricsHistory.tsx', sourceUrl), 'utf8');
const issues = readFileSync(new URL('./features/issues/Issues.tsx', sourceUrl), 'utf8');
const dialogClose = readFileSync(new URL('./components/common/DialogCloseButton.tsx', sourceUrl), 'utf8');
const app = [
  './workspace/Workspace.tsx', './workspace/AuthScreens.tsx', './layouts/AppShell.tsx', './features/administration/Administration.tsx',
].map((path) => readFileSync(new URL(path, sourceUrl), 'utf8')).join('\n');
const projectHeader = readFileSync(new URL('./features/issues/components/ProjectHeader.tsx', sourceUrl), 'utf8');
const rules = readFileSync(new URL('./features/rules/Rules.tsx', sourceUrl), 'utf8');
const ciTokens = readFileSync(new URL('./features/administration/CiTokens.tsx', sourceUrl), 'utf8');
const usersAccess = readFileSync(new URL('./features/administration/UsersAccess.tsx', sourceUrl), 'utf8');
const projectPicker = readFileSync(new URL('./components/common/ProjectPicker.tsx', sourceUrl), 'utf8');
const branchSelection = readFileSync(new URL('./utils/branch-selection.ts', sourceUrl), 'utf8');
const codeQuality = readFileSync(new URL('./features/projects/components/CodeQualityDetails.tsx', sourceUrl), 'utf8');
const analysisEvidence = readFileSync(new URL('./features/projects/components/AnalysisEvidence.tsx', sourceUrl), 'utf8');
const workspaceStyles = readFileSync(new URL('./styles/workspaces-base.css', sourceUrl), 'utf8');
const adminStyles = readFileSync(new URL('./styles/administration.css', sourceUrl), 'utf8');
const sharedDetails = readFileSync(new URL('./styles/shared-details.css', sourceUrl), 'utf8');
const shellStyles = readFileSync(new URL('./styles/shell.css', sourceUrl), 'utf8');
const themeTokens = readFileSync(new URL('./styles/theme.css', sourceUrl), 'utf8');
const controlStyles = readFileSync(new URL('./styles/controls.css', sourceUrl), 'utf8');
const pickerStyles = readFileSync(new URL('./components/common/ProjectPicker.css', sourceUrl), 'utf8');
const projectHeaderStyles = readFileSync(new URL('./features/issues/components/ProjectHeader.css', sourceUrl), 'utf8');
const gridStyles = readFileSync(new URL('./components/ui/ui.css', sourceUrl), 'utf8');
const styles = [workspaceStyles, adminStyles, sharedDetails].join('\n');
const newCode = readFileSync(new URL('./features/projects/components/NewCodePanel.tsx', sourceUrl), 'utf8');
const favoritesSource = readFileSync(new URL('./utils/project-favorites.ts', sourceUrl), 'utf8');
const favoritesOutput = ts.transpileModule(favoritesSource, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const favoriteExports = {};
vm.runInNewContext(favoritesOutput, { exports: favoriteExports });

const projectPickerTree = ts.createSourceFile('ProjectPicker.tsx', projectPicker, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const filterDeclaration = projectPickerTree.statements.find((statement) => ts.isFunctionDeclaration(statement) && statement.name?.text === 'filterProjectOptions');
assert.ok(filterDeclaration, 'ProjectPicker keeps its search predicate testable');
const projectPickerFilterOutput = ts.transpileModule(`${filterDeclaration.getText(projectPickerTree)}\nexports.filterProjectOptions = filterProjectOptions;`, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;
const projectPickerExports = {};
vm.runInNewContext(projectPickerFilterOutput, { exports: projectPickerExports });
const filteredProjects = projectPickerExports.filterProjectOptions(
  [
    { key: 'Up.France.CodeInsights', name: 'CodeInsights' },
    { key: 'Up.France.StoreLocator', name: 'Store locator' },
  ],
  'codeinsights',
);
assert.deepEqual(
  Array.from(filteredProjects, (project) => project.key),
  ['Up.France.CodeInsights'],
  'Project search matches a normalized name or key',
);
assert.deepEqual(
  Array.from(projectPickerExports.filterProjectOptions([{ key: 'Up.France.CodeInsights', name: 'Platform' }], 'france.code'), (project) => project.key),
  ['Up.France.CodeInsights'],
  'Project search also matches the technical key',
);

const ordered = [
  { projectKey: 'a-plain', isFavorite: false },
  { projectKey: 'd-favorite', isFavorite: true },
  { projectKey: 'b-favorite', isFavorite: true },
].sort(favoriteExports.compareFavoriteProjects);
assert.deepEqual(
  ordered.map((project) => project.projectKey),
  ['b-favorite', 'd-favorite', 'a-plain'],
  'Favorites sort before plain projects and remain alphabetical',
);

for (const removed of ['Focus API', 'View all issues', '<AnalysisEvidence', 'Export all history', 'Exact values', 'All branches', 'issue-severity-links']) {
  assert.ok(!projects.includes(removed) && !history.includes(removed), `${removed} is removed from Projects`);
}
assert.ok(!app.includes('<IngestionDetails'), 'Projects no longer renders the reports and SBOM administration panel');
assert.ok(!app.includes('workspace-context') && !app.includes('Historique :'), 'Redundant project context and history explanation are removed');

for (const mapping of [
  "security_rating: 'vulnerability'",
  "maintainability_rating: 'code_smell'",
  "reliability_rating: 'bug'",
  "security_review_rating: 'security_hotspot'",
  "dependency_vulnerabilities: 'dependency'",
])
  assert.ok(projects.includes(mapping), `Metric navigation keeps ${mapping}`);
assert.ok(
  !projects.includes('coverage: "code"') && !projects.includes('duplication: "code"') && projects.includes('onOpenCodeQuality') && projects.includes('qualityDetailMetrics'),
  'Coverage and duplication open native details instead of pretending to have finding rows',
);

assert.ok((projects.match(/onMetricClick=/g) ?? []).length >= 2, 'Overview and detailed metric cards navigate to filtered Issues');
assert.ok(/<MetricGrid\s+measuredOnly/.test(projects) && styles.includes('.metric-grid-compact'), 'Project cards show only measured metrics in a compact layout');
assert.ok(
  analysisEvidence.includes("key !== 'security_hotspots'") && analysisEvidence.includes('metrics.security_hotspots'),
  'The detailed grid merges the duplicate hotspot count into Security review',
);
assert.ok(
  analysisEvidence.includes("vulnerabilities: 'security'") &&
    analysisEvidence.includes("bugs: 'reliability'") &&
    analysisEvidence.includes("code_smells: 'maintainability'") &&
    analysisEvidence.includes("security_hotspots: 'security_review'"),
  'Raw finding counters are not repeated when their MQR rating already carries the same evidence',
);
assert.ok(
  /<span aria-hidden="true">-<\/span>\s*<span className="sr-only">Not available<\/span>/.test(analysisEvidence) &&
    analysisEvidence.includes('MQR ratings were not calculated for this analysis.') &&
    !analysisEvidence.includes('Historical analyses without an MQR snapshot'),
  'Unavailable metrics use a compact dash with one accurate explanation',
);
assert.ok(projects.includes('project-latest-status') && projects.includes('analysisGateLabel(') && projects.includes('summary.latest.quality_gate'), 'Project cards lead with the latest gate status');
assert.ok(history.includes("label: 'Ratings'") && history.includes("label: 'Percentages'") && history.includes("label: 'Findings'"), 'History groups compatible scales into tabs');
assert.ok(!projects.includes('/100') && !history.includes('/100'), 'MQR dashboard surfaces never mix legacy scores with A-E ratings');
assert.ok(history.includes("group.id === 'ratings'") && history.includes('ratingLabel(value)'), 'Rating history uses an explicit A-to-E vertical scale');
assert.ok(history.includes('role="tablist"') && history.includes('role="tabpanel"'), 'Metric groups expose accessible tab semantics');
assert.ok(history.includes('downsampleMetrics') && history.includes('MAX_CHART_POINTS = 32'), 'Long history charts cap their rendered point count');
assert.ok(history.includes('continuousMetric') && history.includes('representative points'), 'Downsampling remains explicit and keeps measurement breaks');
assert.ok(
  history.includes('<LineChart') && history.includes('<XAxis') && history.includes('scale="time"') && history.includes('<Tooltip'),
  'Every metric group uses the Recharts time axis and tooltip',
);
assert.ok(history.includes('Another measured analysis is needed to show a trend.'), 'A single measured analysis explains why no progression is visible');
assert.ok(!history.includes('<svg') && history.includes("from 'recharts'"), 'Metric history no longer maintains a custom SVG chart');
assert.ok(history.includes('const dataKey = `${key}:${segment}`') && !history.includes('(start + end) / 2'), 'Missing values and version changes split real measured series without interpolation');
assert.ok(
  /<GateFailures\s+analysis=\{displayedAnalysis\}/.test(projects) &&
    /condition\.result === ['"]fail['"] \|\| condition\.result === ['"]unknown['"]/.test(projects) &&
    projects.includes('Quality Gate cannot be evaluated'),
  'Failed and unknown gates use clear structured condition copy',
);
assert.ok(
  /condition\.scope !== ['"]new_code['"]/.test(projects) &&
    projects.includes('<NewCodeGateSummary analysis={displayedAnalysis}') &&
    projects.includes('Review New Code') &&
    app.includes("scope: 'new-code', projectMetric: ''"),
  'Overall keeps New Code gate evidence compact and routes its action to the dedicated tab',
);
assert.ok(
  !app.includes('global-tools') &&
    !app.includes('<ScopeToggle') &&
    /<ScopeToggle value=\{scopeMode\} onChange=\{onScopeChange\} \/>\s*<\/div>\s*\{scopeMode === 'new-code' \?/.test(projects) &&
    /Updated [\s\S]*?<\/span>\s*\{scopeMode && onScopeChange && <ScopeToggle/.test(projectHeader) &&
    /\.scope-toggle button \{[^}]*min-height: 0;/.test(shellStyles),
  'The scope toggle closes the analysis metadata line of a selected project without growing it, and keeps that line visible in New Code',
);
assert.ok(
  projects.includes('analysis.analysis_id === selectedAnalysisId') &&
    projects.includes('onOpenIssues(selectedSummary, displayedAnalysis') &&
    /branch: analysisSelectionKey\(analysis\),\s*analysis: analysis\.analysis_id/.test(app),
  'Project metric navigation retains the exact displayed analysis, under its pull request when it is a pull request build',
);
assert.ok(
  app.includes('projectMetric: metric') && app.includes('<CodeQualityDetails') && app.includes('setProjectMetric(restored.projectMetric)'),
  'Code quality details restore from project_metric and browser history',
);
assert.ok(
  codeQuality.includes('/code-quality') && codeQuality.includes('/coverage/files') && codeQuality.includes('/source?') && codeQuality.includes('/duplications'),
  'Code quality details load native evidence endpoints',
);
assert.ok(
  codeQuality.includes('Covered') &&
    codeQuality.includes('Uncovered') &&
    codeQuality.includes('Not executable') &&
    codeQuality.includes('Previous uncovered') &&
    codeQuality.includes('Next uncovered'),
  'Coverage source exposes distinct states and keyboard-operable navigation',
);
assert.ok(
  codeQuality.includes('Compare source') && codeQuality.includes("kind: 'duplicate'") && styles.includes('.duplication-source-grid'),
  'Duplicate blocks expose a side-by-side source comparison',
);
assert.ok(
  codeQuality.includes('gcTime: 0') && codeQuality.includes('staleTime: Infinity') && codeQuality.includes('SOURCE_WINDOW = 300'),
  'Source stays bounded in the browser and is discarded after the viewer closes',
);
assert.ok(
  codeQuality.includes('role="tablist"') && codeQuality.includes('role="tabpanel"') && codeQuality.includes('role="alert"') && codeQuality.includes('Retry'),
  'Code quality details expose tabs and explicit loading failures',
);
assert.ok(
  codeQuality.includes('aria-label="Back to project"') &&
    codeQuality.includes('title="Back to project"') &&
    codeQuality.includes('className="detail-breadcrumb"') &&
    codeQuality.includes('repositoryBranchLink') &&
    codeQuality.includes('repositoryCommitLink'),
  'Detail views use an icon back action and link their breadcrumb to exact repository context',
);
assert.ok(
  sharedDetails.includes('.code-quality-table tbody th') && sharedDetails.includes('.code-quality-table thead th:not(:first-child)') && sharedDetails.includes('white-space: nowrap'),
  'Coverage file rows have readable left padding and numeric headings do not break into letters',
);
assert.ok(
  codeQuality.includes("'ArrowRight'") && codeQuality.includes("'ArrowLeft'") && codeQuality.includes('aria-controls') && codeQuality.includes('aria-labelledby'),
  'Code quality tabs support roving keyboard navigation and panel relationships',
);
assert.ok(
  codeQuality.includes('No duplicated blocks.') && codeQuality.includes('details_purged') && codeQuality.includes('direct_ingestion') && codeQuality.includes('report_not_measured'),
  'Code quality details distinguish measured zero, purged, direct and unmeasured states',
);
assert.ok(styles.includes('.duplication-locations') && styles.includes('grid-template-columns: minmax(0, 1fr)'), 'Duplicate locations stack at the mobile breakpoint');
assert.ok(
  /label: ['"]Vulnerabilities['"]/.test(projects) && /label: ['"]Security hotspots['"]/.test(projects) && styles.includes('.gate-failure-bar'),
  'Gate failures expose readable labels, contextual icons and bounded bars',
);
assert.ok(
  projects.includes('repositoryCommitLink') && projects.includes('repositoryPullRequestLink') && projectHeader.includes('repositoryBranchLink'),
  'Repository references link to their exact revision',
);
assert.ok(newCode.includes('repositoryCommitLink') && newCode.includes('baseUrl') && newCode.includes('headUrl'), 'New Code links both comparison commits');
assert.ok(
  newCode.includes('effective_definition') &&
    newCode.includes('effective_source') &&
    newCode.includes('number_of_days_baseline_absent') &&
    newCode.includes('reference_branch_baseline_absent') &&
    newCode.includes('coverage_producer_incomplete'),
  'New Code explains the effective snapshot definition and its actionable unavailable reasons',
);
assert.ok(
  newCode.includes('<NewCodeGateEvidence conditions={newCodeConditions}') &&
    newCode.includes('New Code Quality Gate cannot be evaluated') &&
    newCode.includes('gateConditionValue(condition)'),
  'The dedicated New Code view retains full structured gate evidence',
);
assert.ok(/coverage_state === ['"]not_applicable['"]/.test(newCode) && newCode.includes('No executable New Code lines'), 'No executable New Code is distinct from missing coverage');
assert.ok(
  newCode.includes('Git comparison was not provided') && newCode.includes('publishes the target SHA and Git diff') && newCode.includes('target commit must already have a completed analysis'),
  'New Code explains how to populate a missing Git comparison',
);
assert.ok(styles.includes("input:not([type='checkbox'])") && styles.includes('.collaboration-checkbox input'), 'Issue checkbox keeps its native compact layout');
assert.ok(styles.includes('.metric-card:has(.metric-card-link):hover') && styles.includes('box-shadow: inset 0 0 0 2px var(--primary)'), 'Clickable metric hover has a clear bounded state');
assert.ok(styles.includes('button.metric-card-link:hover:not(:disabled)') && styles.includes('background: transparent'), 'Metric link overlay stays transparent above readable card content');
assert.ok(projects.includes('aria-pressed={summary.isFavorite}'), 'Favorite controls expose their pressed state');
for (const [surface, source] of [
  ['Projects', projects],
  ['Issues project header', projectHeader],
  ['Rules', rules],
  ['CI tokens', ciTokens],
  ['User access', usersAccess],
  ['Administration project actions', app],
]) {
  assert.ok(source.includes('<ProjectPicker'), `${surface} reuses the searchable project dropdown`);
}
assert.ok(projectPicker.includes('filterProjectOptions(options, search)') && projectPicker.includes('Project name or key'), 'The shared picker filters every catalogue by its displayed name or technical key');
assert.ok(projectPicker.includes('data-picker-option') && projectPicker.includes("aria-selected={singleWithActions ? undefined : checked}"), 'A single picker exposes listbox options without visible radio controls');
assert.ok(projectPicker.includes('type="checkbox"'), 'The shared picker keeps native checkboxes for genuine multiple selection');
assert.ok(
  /const selectionStatus = props\.multiple/.test(projectPicker) && projectPicker.includes("project-picker-options ${props.multiple ? 'multiple' : 'single'}"),
  'Only a multiple selection announces a project count; a single choice does not read as one',
);
assert.ok(
  pickerStyles.includes(".project-picker-option[aria-selected='true'] .project-picker-check") && pickerStyles.includes('.project-picker-option-row.selected'),
  'A single choice marks its selected listbox row without rendering a radio column',
);
assert.ok(
  projectPicker.includes("event.key === 'ArrowDown'") && projectPicker.includes("event.key === 'ArrowUp'") && projectPicker.includes("event.key === 'Escape'"),
  'The shared picker handles arrow navigation and Escape explicitly',
);
// The rows and the search field are <label> elements. A container that styles its
// labels once restacked them into a column and pushed the selected mark onto a
// second line, so the picker owns their layout at a specificity no ancestor reaches.
assert.ok(
  pickerStyles.includes('.project-picker .project-picker-option {') && pickerStyles.includes('.project-picker .project-picker-search {'),
  'Picker rows and search own their layout so an ancestor label rule cannot restack them',
);
assert.ok(!/^\.project-context-selectors .*label/m.test(sharedDetails), 'The selector row leaves every label layout to the shared picker');
assert.ok(!/\.branch-selector-group label/.test(projectHeaderStyles), 'The Issues header does not restyle labels inside the branch picker');
assert.ok(projectPicker.includes('project-picker-favorite') && projectPicker.includes('favoriteAction(option.key)'), 'Favorite actions live on their own project row');
assert.ok(!projects.includes('project-favorite-select'), 'The isolated favorite button is removed from the project and branch selector row');
assert.ok(
  projectPicker.includes('clearable?: boolean') && projectPicker.includes('!props.multiple && props.clearable &&'),
  'The empty row is a deliberate choice, never a prompt rendered as an option',
);
assert.ok(
  (projects.match(/<ProjectPicker/g) ?? []).length >= 2 && (projectHeader.match(/<ProjectPicker/g) ?? []).length >= 2 && !projects.includes('id="dashboard-branch"') && !projectHeader.includes('id="branch-selector"'),
  'Project and branch selectors reuse the same searchable picker on Projects and Issues',
);
assert.ok(
  branchSelection.includes("replace(/^refs\\/heads\\//, '')") && projects.includes('hideOptionKey') && projectHeader.includes('hideOptionKey'),
  'Branch selectors keep the technical ref but never expose refs/heads in their visible option text',
);
assert.ok(
  pickerStyles.includes('max-height: min(420px, calc(100vh - 180px))') && pickerStyles.includes('min-height: 44px') && pickerStyles.includes('scrollbar-width: thin'),
  'The shared dropdown is viewport-bounded, compact, and uses a restrained scrollbar',
);
assert.ok(sharedDetails.includes('.reference-picker > a') && sharedDetails.includes('width: 28px'), 'The branch repository action is compact and associated with the picker');

for (const token of ['--control-height:', '--control-height-sm:', '--control-padding-block:', '--control-padding-inline:', '--control-radius:', '--control-font-size:'])
  assert.ok(themeTokens.includes(token), `Control geometry token ${token} is defined once in the theme`);
assert.ok(
  /input:not\(\[type='checkbox'\]\)[\s\S]*?min-height: var\(--control-height\)/.test(controlStyles) && /button,\r?\n\.button \{[\s\S]*?min-height: var\(--control-height\)/.test(controlStyles),
  'Native fields and buttons share one height token',
);
assert.ok(!/min-height: 36px/.test(sharedDetails), 'The shared height is no longer hard-coded next to the token');
for (const [name, sheet] of [
  ['issue filters', workspaceStyles],
  ['administration filters', adminStyles],
  ['app bar', shellStyles],
  ['project picker', pickerStyles],
])
  assert.ok(sheet.includes('var(--control-height'), `${name} controls follow the shared height token`);
for (const removed of ['Search projects', 'Favorite projects only', 'All projects']) assert.ok(!projects.includes(removed), `${removed} is removed from the Projects sidebar`);
assert.ok(app.includes('.sort(compareFavoriteProjects)'), 'Favorite projects always sort before plain projects');
assert.ok(app.includes("method: isFavorite ? 'DELETE' : 'PUT'") && app.includes("['analyses', 'summary-project']"), 'Favorite mutation uses idempotent methods and refreshes both summary caches');
assert.ok(app.includes("setProjectCursor('')") && styles.includes('.project-favorite.active'), 'Favorite changes reset pagination and use the shared visual tokens');
assert.ok(
  usersAccess.includes('className="table-wrap iam-user-grid"') && gridStyles.includes('.ui-grid-wrap') && gridStyles.includes('overflow-x: auto') && gridStyles.includes('@media (max-width: 767px)'),
  'The shared Grid scrolls on intermediate widths and stacks labeled user rows on mobile',
);
assert.ok(styles.includes('.left-filters .info-hint-button') && styles.includes('width: 28px'), 'Sidebar help keeps its compact icon width');

for (const filter of ['severity', 'category', 'rule', 'path', 'mine', 'state', 'team_id']) {
  assert.ok(new RegExp(`(?:${filter}:|\\b${filter},)`).test(issues), `Issue exports include active ${filter} filter`);
}
assert.ok(issues.includes('/findings/export/csv?') && (issues.match(/\/findings\/export\//g) ?? []).length === 1, 'Issues expose one filtered CSV export');
assert.ok(!issues.includes('none provided') && !issues.includes('Report message:') && !issues.includes('File Details:'), 'Issue details hide absent evidence and do not repeat the summary');
assert.ok(
  issues.includes('relatedLocations.length > 0 || flowLocations.length > 0') && issues.includes('Rule guidance'),
  'Issue details reveal only evidence that exists and group developer guidance',
);
assert.ok(!issues.includes('className="issue-sep"') && styles.includes('.issue-rule') && styles.includes('flex-wrap: wrap'), 'Issue metadata groups wrap without orphan separators');
assert.ok(issues.includes('className="icon-button"') && issues.includes('aria-label="Export filtered issues as CSV"'), 'CSV export is a named icon button');
assert.ok(
  issues.includes('className={`icon-button prompt-copy-button${promptState.copied') &&
    issues.includes("promptState.copied ? 'Copied' : 'Copy'") &&
    issues.includes('}, 2000)') &&
    issues.includes('<DialogCloseButton onClick={closePrompt}') &&
    dialogClose.includes('title="Close"') &&
    !issues.includes('{promptState.copied && <span role="status">Copied</span>}'),
  'Prompt dialog keeps icon actions in its header and resets the copied label after two seconds',
);
assert.ok(styles.includes('.icon-button.prompt-copy-button.copied') && styles.includes('color: var(--success)'), 'Copied prompt action uses the semantic success color');
assert.ok(!issues.includes('sur cette page') && !issues.includes('issues-toolbar-summary">\n            <p>'), 'Issues omit the per-page technical summary');
assert.ok(issues.indexOf('aria-label="Issue pages"') > issues.indexOf('className="issues-list"'), 'Issue pagination follows the list');
assert.ok(!app.includes('workspace-heading') && app.includes('`CodeInsights - ${pageName}`'), 'The app avoids a duplicate view heading and synchronizes the document title');

console.log('Projects UX checks passed.');
});
