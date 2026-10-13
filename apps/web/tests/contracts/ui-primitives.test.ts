import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test } from 'vitest';
import { sourceUrl } from '../test-path';

function componentFiles(directory: URL): URL[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.name === 'ui' || entry.name.endsWith('.test.tsx')) return [];
    const url = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
    return entry.isDirectory() ? componentFiles(url) : entry.name.endsWith('.tsx') ? [url] : [];
  });
}

test('application screens use the shared UI primitives', () => {
  const files = [
    new URL('App.tsx', sourceUrl),
    ...['components/common/', 'features/', 'layouts/', 'workspace/'].flatMap((path) => componentFiles(new URL(path, sourceUrl))),
  ];
  const nativeControl = /<(input|select|textarea|button|table)\b/;

  for (const file of files) {
    assert.doesNotMatch(readFileSync(file, 'utf8'), nativeControl, `${fileURLToPath(file)} must use components/ui`);
  }
});
