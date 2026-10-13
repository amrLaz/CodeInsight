import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('motion contracts', async () => {
const index = readFileSync(new URL('../index.html', sourceUrl), 'utf8');
const main = readFileSync(new URL('./main.tsx', sourceUrl), 'utf8');
const app = readFileSync(new URL('./workspace/Workspace.tsx', sourceUrl), 'utf8');
const styles = ['./styles/shell.css', './styles/shared-details.css']
  .map((path) => readFileSync(new URL(path, sourceUrl), 'utf8'))
  .join('\n');

assert.match(index, /id="startup-splash"[^>]+role="status"/, 'Startup splash exposes a status');
assert.match(index, /id="root" inert aria-hidden="true"/, 'The application stays inaccessible below the splash');
assert.match(main, /SPLASH_MIN_MS = 600/, 'Startup splash remains visible for the agreed minimum');
assert.match(main, /addEventListener\('vite:preloadError',[\s\S]*event\.preventDefault\(\)[\s\S]*window\.location\.reload\(\)/, 'A stale lazy-loaded chunk reloads the current frontend release');
assert.match(main, /root\.removeAttribute\('inert'\)/, 'The application becomes interactive when startup completes');
assert.match(main, /prefers-reduced-motion: reduce/, 'Startup dismissal handles reduced motion without a fade');
assert.match(app, /<div key=\{activeTab\} className="workspace-view" id="workspace-content"/, 'Primary view changes remount the transition surface');
assert.match(styles, /\.workspace-view\s*\{[^}]*animation: workspace-view-enter 220ms/s, 'Primary views use a visible but restrained motion contract');
assert.match(styles, /@keyframes workspace-view-enter\s*\{[\s\S]*opacity: 0\.35;[\s\S]*transform: translateY\(10px\)/, 'Primary views combine fade and short translation');
assert.match(styles, /@media \(prefers-reduced-motion: reduce\)[\s\S]*animation: none !important/, 'The global reduced-motion guard disables animations');

console.log('Startup splash and primary view motion checks passed.');
});
