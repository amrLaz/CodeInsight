import { describe, expect, it } from 'vitest';
import { appendLicense, licenseOverlap, parseLicenseLines } from './licenses';

describe('licence lists', () => {
  it('keeps one exact string per line, case included', () => {
    expect(parseLicenseLines(' MIT \n\nMIT\nmit\nMIT OR Apache-2.0\n')).toEqual(['MIT', 'mit', 'MIT OR Apache-2.0']);
  });

  it('finds licences listed on both sides', () => {
    expect(licenseOverlap(['MIT', 'GPL-3.0-only'], ['GPL-3.0-only'])).toEqual(['GPL-3.0-only']);
    expect(licenseOverlap(['MIT'], ['mit'])).toEqual([]);
  });

  it('appends a licence once', () => {
    expect(appendLicense('MIT\nISC', 'Zlib')).toBe('MIT\nISC\nZlib');
    expect(appendLicense('MIT\nISC', 'MIT')).toBe('MIT\nISC');
    expect(appendLicense('', 'MIT')).toBe('MIT');
  });
});
