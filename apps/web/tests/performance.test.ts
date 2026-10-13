import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'vitest';
import { sourceUrl } from './test-path';

test('screen-level loading contracts', () => {
  const app = readFileSync(new URL('./workspace/Workspace.tsx', sourceUrl), 'utf8');
  const workspaceData = readFileSync(new URL('./workspace/useWorkspaceData.ts', sourceUrl), 'utf8');

  for (const component of ['Projects', 'Issues', 'Rules', 'QualityGates', 'CodeQualityDetails', 'AnalysisProfiles', 'AccountTokens']) {
    assert.match(app, new RegExp(`const ${component} = lazy\\(\\(\\) => import\\(`), `${component} stays outside the initial application bundle`);
  }
  assert.match(app, /<Suspense\s+fallback=\{[\s\S]*?<p className="feedback" role="status">\s*Loading workspace…\s*<\/p>/, 'Lazy screens expose an accessible loading status');
  assert.match(app, /onToggle=\{\(event\) => \{[\s\S]*event\.currentTarget\.open[\s\S]*setProfilesActivated\(true\)/, 'Profile administration is activated by its disclosure');
  assert.match(app, /profilesActivated && <AnalysisProfiles/, 'Profile queries cannot mount before the disclosure is opened');
  assert.doesNotMatch(workspaceData, /\/v1\/gates\/default/, 'The redundant default gate request is removed');
});
