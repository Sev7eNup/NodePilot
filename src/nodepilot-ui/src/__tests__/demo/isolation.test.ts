/**
 * Guards for the three boundaries the demo depends on.
 *
 * 1. Tab isolation. The app synchronises identity across tabs; opening or reloading a second
 *    tab makes every other tab clear its caches and remount, discarding unsaved editor state.
 *    An in-memory backend does not prevent that, because the channel is purely client-side.
 * 2. Dependency direction. `src/` must never import from `demo/`. That is what makes the
 *    product build unable to reach the demo, and the hub and docs seams are injected for
 *    exactly this reason.
 * 3. Base-path safety. `base: './'` does not rewrite URLs that live in JavaScript strings, so
 *    a root-absolute literal breaks on any deployment below the origin root.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isolateAuthBoundaryTransport,
  publishAuthenticatedIdentity,
  subscribeToAuthBoundaryEvents,
} from '../../security/authBoundary';

const PACKAGE_ROOT = join(__dirname, '..', '..', '..');
const SRC_ROOT = join(PACKAGE_ROOT, 'src');

function sourceFiles(root: string): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(root)) {
    const full = join(root, entry);
    if (statSync(full).isDirectory()) {
      if (entry === '__tests__' || entry === 'node_modules') continue;
      found.push(...sourceFiles(full));
      continue;
    }
    if (/\.tsx?$/.test(entry)) found.push(full);
  }
  return found;
}

describe('cross-tab isolation', () => {
  let postMessage: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    postMessage = vi.fn();
    vi.stubGlobal('BroadcastChannel', class {
      addEventListener() { /* nothing is delivered while isolated */ }
      removeEventListener() { /* nothing is delivered while isolated */ }
      postMessage = postMessage;
      close() { /* no-op */ }
    });
    vi.spyOn(Storage.prototype, 'setItem');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('publishes nothing on either transport once isolated', () => {
    isolateAuthBoundaryTransport();
    const stop = subscribeToAuthBoundaryEvents(() => { /* would remount the tree */ });

    publishAuthenticatedIdentity('user-1');

    expect(postMessage).not.toHaveBeenCalled();
    // The storage-event fallback has to be silent too, or a browser without BroadcastChannel
    // would still cross-talk.
    const calls = (Storage.prototype.setItem as unknown as { mock: { calls: unknown[][] } }).mock.calls;
    expect(calls.filter(([key]) => String(key).includes('auth-boundary'))).toEqual([]);
    stop();
  });
});

describe('dependency direction', () => {
  it('never imports demo code from src', () => {
    const offenders: string[] = [];
    for (const file of sourceFiles(SRC_ROOT)) {
      const text = readFileSync(file, 'utf8');
      if (/from\s+['"][^'"]*\bdemo\//.test(text) || /import\(\s*['"][^'"]*\bdemo\//.test(text)) {
        offenders.push(relative(PACKAGE_ROOT, file));
      }
    }
    // The hub connection and the docs link are injected by the demo entry instead.
    expect(offenders).toEqual([]);
  });

  it('keeps the demo out of the product build inputs', () => {
    const config = readFileSync(join(PACKAGE_ROOT, 'vite.config.ts'), 'utf8');
    expect(config).not.toContain('demo.html');
    expect(config).toContain("__NP_DEMO__: 'false'");
    const indexHtml = readFileSync(join(PACKAGE_ROOT, 'index.html'), 'utf8');
    expect(indexHtml).not.toContain('demo');
  });

  it('defines the demo flag for tests, which import App', () => {
    // Missing this define breaks DbViewerPage.test.tsx with a ReferenceError that looks
    // unrelated to the router.
    const config = readFileSync(join(PACKAGE_ROOT, 'vitest.config.ts'), 'utf8');
    expect(config).toContain("__NP_DEMO__: 'false'");
  });
});

describe('base-path safety', () => {
  it('has no root-absolute URL literals outside the known set', () => {
    // `/api` is handled before the network by the demo's fetch patch, so it needs no rewrite.
    // `docsLink.ts` holds the server default in one place and exists to be replaced; that it
    // is the only other entry here is the property this test protects.
    const allowed = new Set(['src/api/client.ts', 'src/api/adminSettings.ts', 'src/lib/docsLink.ts']);
    const offenders: string[] = [];

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(PACKAGE_ROOT, file).replaceAll('\\', '/');
      if (allowed.has(rel)) continue;
      const text = readFileSync(file, 'utf8');
      // Static asset and page references written as origin-absolute paths.
      if (/['"]\/(appicon|assets|docs|favicon|icons)[^'"]*['"]/.test(text)) offenders.push(rel);
    }

    expect(offenders).toEqual([]);
  });
});
