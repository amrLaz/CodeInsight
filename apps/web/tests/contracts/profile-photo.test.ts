import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('profile photo contracts', async () => {
const hook = readFileSync(new URL('./workspace/useWorkspaceData.ts', sourceUrl), 'utf8');
const app = readFileSync(new URL('./workspace/Workspace.tsx', sourceUrl), 'utf8');
const appShell = readFileSync(new URL('./layouts/AppShell.tsx', sourceUrl), 'utf8');
const shell = readFileSync(new URL('./styles/shell.css', sourceUrl), 'utf8');
const theme = readFileSync(new URL('./styles/theme.css', sourceUrl), 'utf8');
assert.match(hook, /\/v1\/auth\/me\/photo/, 'Profile photo is loaded from the authenticated backend proxy');
assert.match(hook, /\.blob\(\)/, 'Photo response is consumed as a Blob');
assert.match(app, /URL\.createObjectURL\(photoQuery\.data\)/);
assert.match(app, /URL\.revokeObjectURL\(url\)/, 'Blob URL is revoked when the session/photo changes');
assert.match(appShell, /<img src=\{photoUrl\} alt=""/, 'The photo remains decorative inside the named account button');
assert.match(appShell, /photoUrl && !photoFailed/, 'Photo errors fall back to initials');
assert.match(
  shell,
  /\.user-avatar \{[\s\S]*?width: var\(--control-height\);[\s\S]*?height: var\(--control-height\);/,
  'Photo keeps the account control square on the shared control height',
);
assert.match(theme, /--control-height: 36px;/, 'The shared control height stays at the 36px account control size');
console.log('Authenticated profile photo checks passed.');
});
