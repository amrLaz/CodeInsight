import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

test('theme contracts', async () => {
const themeSource = readFileSync(new URL('./theme.ts', sourceUrl), 'utf8');
const { outputText } = ts.transpileModule(themeSource, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
});
const theme = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`);

assert.equal(theme.resolveTheme(null, false), 'light');
assert.equal(theme.resolveTheme(null, true), 'dark');
assert.equal(theme.resolveTheme('light', true), 'light');
assert.equal(theme.resolveTheme('dark', false), 'dark');
assert.equal(theme.resolveTheme('invalid', true), 'dark');

assert.equal(theme.readStoredTheme({ getItem: () => 'dark' }), 'dark');
assert.equal(theme.readStoredTheme({ getItem: () => 'invalid' }), null);
assert.equal(theme.readStoredTheme({ getItem: () => { throw new Error('blocked'); } }), null);
let stored;
assert.equal(theme.persistTheme({ setItem: (key, value) => { stored = [key, value]; } }, 'light'), true);
assert.deepEqual(stored, ['codeinsights-theme-v1', 'light']);
assert.equal(theme.persistTheme({ setItem: () => { throw new Error('blocked'); } }, 'dark'), false);
assert.equal(theme.nextTheme('light'), 'dark');
assert.equal(theme.nextTheme('dark'), 'light');

const root = { dataset: {}, style: {} };
const meta = { setAttribute: (name, value) => { meta[name] = value; } };
theme.applyTheme('dark', root, meta);
assert.equal(root.dataset.theme, 'dark');
assert.equal(root.style.colorScheme, 'dark');
assert.equal(meta.content, '#101923');

const index = readFileSync(new URL('../index.html', sourceUrl), 'utf8');
const bootstrap = index.match(/<script>([\s\S]*?)<\/script>/)?.[1];
assert.ok(bootstrap, 'Theme bootstrap runs from the document head');
assert.ok(index.indexOf('<script>') < index.indexOf('id="startup-splash"'), 'Theme resolves before the splash is parsed');
assert.ok(index.indexOf('<script>') < index.indexOf('src="/src/main.tsx"'), 'Theme resolves before React starts');

function runBootstrap({ storedTheme, prefersDark, storageBlocked = false }) {
  const bootstrapRoot = { dataset: {}, style: {} };
  const bootstrapMeta = { content: '' };
  const browser = {
    matchMedia: () => ({ matches: prefersDark }),
  };
  Object.defineProperty(browser, 'localStorage', {
    get: () => {
      if (storageBlocked) throw new Error('blocked');
      return { getItem: () => storedTheme };
    },
  });
  vm.runInNewContext(bootstrap, {
    window: browser,
    document: {
      documentElement: bootstrapRoot,
      querySelector: () => bootstrapMeta,
    },
  });
  return { root: bootstrapRoot, meta: bootstrapMeta };
}

const systemDark = runBootstrap({ storedTheme: null, prefersDark: true });
const storedLight = runBootstrap({ storedTheme: 'light', prefersDark: true });
assert.equal(systemDark.root.dataset.theme, 'dark');
assert.equal(systemDark.meta.content, '#101923');
assert.equal(storedLight.root.dataset.theme, 'light');
assert.equal(storedLight.meta.content, '#005eb8');
assert.equal(runBootstrap({ storedTheme: 'invalid', prefersDark: false }).root.dataset.theme, 'light');
assert.equal(runBootstrap({ storedTheme: null, prefersDark: true, storageBlocked: true }).root.dataset.theme, 'dark');

const pwaManifest = JSON.parse(readFileSync(new URL('../public/site.webmanifest', sourceUrl), 'utf8'));
assert.equal(pwaManifest.theme_color, '#005eb8', 'The installable PWA keeps the static brand color');

const component = readFileSync(new URL('./components/common/ThemeToggle.tsx', sourceUrl), 'utf8');
const authScreens = readFileSync(new URL('./workspace/AuthScreens.tsx', sourceUrl), 'utf8');
const appShell = readFileSync(new URL('./layouts/AppShell.tsx', sourceUrl), 'utf8');
assert.equal((authScreens.match(/<ThemeToggle \/>/g) ?? []).length, 2, 'Sign-in and sign-out expose the theme control');
assert.equal((appShell.match(/<ThemeToggle \/>/g) ?? []).length, 1, 'The application shell exposes the theme control');
assert.match(component, /aria-pressed=\{state\.theme === 'dark'\}/, 'Theme control exposes its pressed state');
assert.match(component, /aria-label=\{label\}[\s\S]*title=\{label\}/, 'Theme control names its next action');
assert.match(component, /<svg viewBox="0 0 24 24" aria-hidden="true">/, 'Theme control uses a decorative icon without duplicating its accessible name');
assert.match(component, /addEventListener\('change', followSystemTheme\)/, 'System changes remain active until a manual choice');

const manifest = readFileSync(new URL('./styles.css', sourceUrl), 'utf8');
const expectedImports = ['theme', 'shell', 'controls', 'workspaces-base', 'administration', 'shared-details', 'rules-quality-gates'];
assert.deepEqual(
  [...manifest.matchAll(/@import '\.\/styles\/([^']+)\.css';/g)].map((match) => match[1]),
  expectedImports,
  'The stylesheet entrypoint keeps the deliberate cascade order',
);

const themeStyles = readFileSync(new URL('./styles/theme.css', sourceUrl), 'utf8');
const lightTokens = themeStyles.match(/^:root\s*\{([\s\S]*?)\n\}/m)?.[1];
const darkTokens = themeStyles.match(/:root\[data-theme='dark'\]\s*\{([\s\S]*?)\n\}/)?.[1];
assert.ok(lightTokens, 'Light theme tokens remain the default');
assert.ok(darkTokens, 'Dark theme tokens are defined at the document root');
for (const token of ['--on-primary', '--on-danger', '--surface-subtle', '--focus-ring', '--backdrop']) {
  assert.ok(themeStyles.includes(token), `${token} is part of the shared theme contract`);
}
assert.match(darkTokens, /color-scheme: dark;/, 'Native controls use the dark browser color scheme');

function hexTokens(source) {
  return Object.fromEntries([...source.matchAll(/(--[\w-]+):\s*(#[0-9a-f]{6});/gi)].map((match) => [match[1], match[2]]));
}

function relativeLuminance(hex) {
  const channels = hex.match(/[0-9a-f]{2}/gi).map((channel) => Number.parseInt(channel, 16) / 255);
  const [red, green, blue] = channels.map((channel) => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function contrast(left, right) {
  const values = [relativeLuminance(left), relativeLuminance(right)].sort((a, b) => b - a);
  return (values[0] + 0.05) / (values[1] + 0.05);
}

const contrastPairs = [
  ['--text-primary', '--bg-light'],
  ['--text-secondary', '--bg-light'],
  ['--text-tertiary', '--bg-light'],
  ['--primary', '--bg-lighter'],
  ['--primary', '--primary-light'],
  ['--on-primary', '--primary'],
  ['--on-danger', '--danger'],
  ['--success', '--success-light'],
  ['--warning', '--warning-light'],
  ['--danger', '--danger-light'],
];
for (const [name, source] of [['light', lightTokens], ['dark', darkTokens]]) {
  const tokens = hexTokens(source);
  for (const [foreground, background] of contrastPairs) {
    assert.ok(contrast(tokens[foreground], tokens[background]) >= 4.5, `${name} ${foreground} stays WCAG AA against ${background}`);
  }
}

console.log('Theme persistence, bootstrap, accessibility and stylesheet checks passed.');
});
