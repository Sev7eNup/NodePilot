import { describe, it, expect, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useMediaQuery, useIsMobile, MOBILE_BREAKPOINT } from '../../hooks/useMediaQuery';

/**
 * The global jsdom setup stubs `window.matchMedia` to always report `{ matches: false }`.
 * These tests install a controllable mock per test and restore the original afterwards so
 * the rest of the suite keeps its "always desktop" default.
 */
const originalMatchMedia = window.matchMedia;

function installMatchMedia(initial: boolean) {
  let matches = initial;
  const listeners = new Set<() => void>();
  const mql = {
    get matches() { return matches; },
    media: '',
    onchange: null,
    addEventListener: (_type: string, cb: () => void) => { listeners.add(cb); },
    removeEventListener: (_type: string, cb: () => void) => { listeners.delete(cb); },
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  };
  const fn = vi.fn((q: string) => { mql.media = q; return mql; });
  window.matchMedia = fn as unknown as typeof window.matchMedia;
  return {
    fn,
    listeners,
    setMatches(v: boolean) { act(() => { matches = v; listeners.forEach((l) => l()); }); },
  };
}

afterEach(() => {
  window.matchMedia = originalMatchMedia;
  vi.restoreAllMocks();
});

describe('useMediaQuery', () => {
  it('returns false when the query does not match', () => {
    installMatchMedia(false);
    const { result } = renderHook(() => useMediaQuery('(max-width: 1023px)'));
    expect(result.current).toBe(false);
  });

  it('returns true when the query matches', () => {
    installMatchMedia(true);
    const { result } = renderHook(() => useMediaQuery('(max-width: 1023px)'));
    expect(result.current).toBe(true);
  });

  it('updates when the media query flips via a change event', () => {
    const mm = installMatchMedia(false);
    const { result } = renderHook(() => useMediaQuery('(max-width: 1023px)'));
    expect(result.current).toBe(false);
    mm.setMatches(true);
    expect(result.current).toBe(true);
  });

  it('subscribes on mount and unsubscribes on unmount', () => {
    const mm = installMatchMedia(true);
    const { unmount } = renderHook(() => useMediaQuery('(max-width: 1023px)'));
    expect(mm.listeners.size).toBe(1);
    unmount();
    expect(mm.listeners.size).toBe(0);
  });

  it('does not throw and returns false when matchMedia is unavailable', () => {
    // @ts-expect-error — simulate an environment without matchMedia
    window.matchMedia = undefined;
    const { result } = renderHook(() => useMediaQuery('(max-width: 1023px)'));
    expect(result.current).toBe(false);
  });
});

describe('useIsMobile', () => {
  it('queries the shared mobile breakpoint', () => {
    const mm = installMatchMedia(true);
    const { result } = renderHook(() => useIsMobile());
    expect(result.current).toBe(true);
    expect(mm.fn).toHaveBeenCalledWith(MOBILE_BREAKPOINT);
  });
});
