import { useEffect, useState } from 'react';

/**
 * Subscribe to a CSS media query and re-render when it flips.
 *
 * Used only where rendering both responsive branches would be expensive or stateful
 * (the app-shell off-canvas drawer and the table↔card switch on list pages). Everything
 * purely presentational stays on Tailwind utilities (`hidden lg:block` / `lg:hidden`).
 *
 * Test note: the jsdom setup (`src/__tests__/setup.ts`) stubs `matchMedia` to always
 * report `{ matches: false }`, so this resolves to the desktop branch for the whole
 * existing suite. Mobile tests opt in by overriding `window.matchMedia` per test — the
 * post-mount re-sync below picks up that override.
 */
export function useMediaQuery(query: string): boolean {
  const getMatches = () =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia(query).matches
      : false;

  const [matches, setMatches] = useState(getMatches);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mql = window.matchMedia(query);
    const onChange = () => setMatches(mql.matches);
    onChange(); // re-sync after mount (covers SSR-mismatch and per-test overrides)
    mql.addEventListener('change', onChange);
    return () => mql.removeEventListener('change', onChange);
  }, [query]);

  return matches;
}

/**
 * Below this width the app switches to its mobile layout: the sidebar becomes an
 * off-canvas drawer and list-page tables collapse into cards. `1023px` = one pixel
 * under Tailwind's `lg` (1024px), so the JS hook and the CSS `lg:` utilities flip at
 * the exact same breakpoint — the hamburger and the drawer can never disagree.
 */
export const MOBILE_BREAKPOINT = '(max-width: 1023px)';

/** True on phones and portrait tablets (< Tailwind `lg`). */
export const useIsMobile = (): boolean => useMediaQuery(MOBILE_BREAKPOINT);
