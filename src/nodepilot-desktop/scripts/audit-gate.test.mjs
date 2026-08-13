import { describe, expect, it } from 'vitest';

import { ALLOWLIST, collectAdvisories, evaluate } from './audit-gate.mjs';

/**
 * The gate decides whether a dependency advisory fails the build. Its whole reason for
 * existing is that one advisory is excused — so the tests that matter are the ones proving
 * it excuses exactly that one and nothing else, and that it tells us when the excuse expires.
 */

const advisory = (overrides = {}) => ({
  source: 1,
  name: 'extract-zip',
  dependency: 'extract-zip',
  title: 'extract-zip unvalidated symlink path traversal',
  url: 'https://github.com/advisories/GHSA-jmr9-qjv8-65gv',
  severity: 'high',
  ...overrides,
});

const report = (vulnerabilities) => ({ vulnerabilities });

describe('collectAdvisories', () => {
  it('folds one advisory reported against many packages into a single entry', () => {
    // This is the real shape: npm repeats the extract-zip finding once per package in the
    // Electron Forge chain — 15 entries in CI for one underlying problem.
    const result = collectAdvisories(
      report({
        'extract-zip': { via: [advisory()] },
        '@electron/packager': { via: [advisory({ name: '@electron/packager' })] },
        '@electron-forge/core': { via: [advisory({ name: '@electron-forge/core' })] },
      }),
    );

    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('GHSA-jmr9-qjv8-65gv');
    expect(result[0].packages).toEqual([
      '@electron-forge/core',
      '@electron/packager',
      'extract-zip',
    ]);
  });

  it('ignores the plain-string via entries npm uses for transitive hits', () => {
    const result = collectAdvisories(
      report({
        'extract-zip': { via: [advisory()] },
        '@electron/packager': { via: ['extract-zip'] },
      }),
    );

    expect(result).toHaveLength(1);
    expect(result[0].packages).toEqual(['extract-zip']);
  });

  it('skips entries whose url is not a GitHub advisory', () => {
    expect(collectAdvisories(report({ weird: { via: [advisory({ url: 'https://example.test' })] } }))).toEqual([]);
  });

  it('returns nothing for a clean report', () => {
    expect(collectAdvisories(report({}))).toEqual([]);
    expect(collectAdvisories({})).toEqual([]);
    expect(collectAdvisories(undefined)).toEqual([]);
  });
});

describe('evaluate', () => {
  const allowlist = [{ id: 'GHSA-jmr9-qjv8-65gv', package: 'extract-zip', reason: 'test' }];

  it('blocks an advisory that is not on the allowlist', () => {
    const result = evaluate(report({ tar: { via: [advisory({ name: 'tar', url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc' })] } }), { allowlist });

    expect(result.blocking.map((a) => a.id)).toEqual(['GHSA-aaaa-bbbb-cccc']);
    expect(result.excused).toEqual([]);
  });

  it('excuses an advisory that is on the allowlist', () => {
    const result = evaluate(report({ 'extract-zip': { via: [advisory()] } }), { allowlist });

    expect(result.blocking).toEqual([]);
    expect(result.excused.map((a) => a.id)).toEqual(['GHSA-jmr9-qjv8-65gv']);
  });

  it('excuses only the listed advisory when both kinds are present', () => {
    const result = evaluate(
      report({
        'extract-zip': { via: [advisory()] },
        tar: { via: [advisory({ name: 'tar', url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc' })] },
      }),
      { allowlist },
    );

    expect(result.blocking.map((a) => a.id)).toEqual(['GHSA-aaaa-bbbb-cccc']);
    expect(result.excused.map((a) => a.id)).toEqual(['GHSA-jmr9-qjv8-65gv']);
  });

  it('ignores advisories below the severity floor', () => {
    const low = report({ x: { via: [advisory({ name: 'x', severity: 'low', url: 'https://github.com/advisories/GHSA-dddd-eeee-ffff' })] } });

    expect(evaluate(low, { allowlist }).blocking).toEqual([]);
    expect(evaluate(low, { allowlist, minSeverity: 'low' }).blocking).toHaveLength(1);
  });

  it('rejects an unknown severity floor rather than silently passing everything', () => {
    expect(() => evaluate(report({}), { minSeverity: 'catastrophic' })).toThrow(/unknown severity/);
  });

  it('reports an allowlist entry that no longer matches so it can be deleted', () => {
    const result = evaluate(report({}), { allowlist });

    expect(result.stale.map((e) => e.id)).toEqual(['GHSA-jmr9-qjv8-65gv']);
  });

  it('does not call a still-present entry stale', () => {
    expect(evaluate(report({ 'extract-zip': { via: [advisory()] } }), { allowlist }).stale).toEqual([]);
  });
});

describe('the shipped ALLOWLIST', () => {
  it('excuses the extract-zip advisory that CI actually reports', () => {
    // Guards against a typo in the id: the entry would then quietly excuse nothing and the
    // desktop job would go red again for a reason nobody expects.
    const result = evaluate(report({ 'extract-zip': { via: [advisory()] } }));

    expect(result.blocking).toEqual([]);
    expect(result.excused).toHaveLength(1);
    expect(result.stale).toEqual([]);
  });

  it('carries a reason for every entry, so no exception is undocumented', () => {
    for (const entry of ALLOWLIST) {
      expect(entry.id).toMatch(/^GHSA-[a-z0-9-]+$/i);
      expect(entry.package).toBeTruthy();
      expect(entry.reason.length).toBeGreaterThan(60);
    }
  });
});
