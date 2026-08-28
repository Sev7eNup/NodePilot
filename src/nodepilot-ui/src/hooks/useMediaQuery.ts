import { useEffect, useState } from 'react';

/**
 * Subscribe to a CSS media query and re-render when it flips. Used only where rendering both
 * responsive branches would be expensive or stateful, such as the off-canvas drawer and the
 * table-to-card switch on list pages; purely presentational cases stay on Tailwind utilities.
 * The jsdom setup (`src/__tests__/setup.ts`) stubs `matchMedia` as never matching, so tests get
 * the desktop branch unless they override `window.matchMedia` themselves.
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
    onChange(); // re-sync after mount, covering SSR mismatch and per-test overrides
    mql.addEventListener('change', onChange);
    return () => mql.removeEventListener('change', onChange);
  }, [query]);

  return matches;
}

/**
 * Below this width the app switches to its mobile layout: the sidebar becomes an off-canvas
 * drawer and list-page tables collapse into cards. `1023px` is one pixel under Tailwind's `lg`
 * (1024px), so this hook and the CSS `lg:` utilities flip at the same breakpoint.
 */
export const MOBILE_BREAKPOINT = '(max-width: 1023px)';

/** True on phones and portrait tablets (< Tailwind `lg`). */
export const useIsMobile = (): boolean => useMediaQuery(MOBILE_BREAKPOINT);
