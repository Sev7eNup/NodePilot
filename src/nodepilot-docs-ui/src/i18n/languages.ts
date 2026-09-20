/**
 * The two languages the documentation ships in.
 *
 * This module imports no i18next code: the router, the content loader and the Vite build
 * all need to reason about languages without an initialised i18n instance.
 */

export const LANGUAGES = ['en', 'de'] as const

export type Lang = (typeof LANGUAGES)[number]

/** Fallback for every lookup and the default for new visitors. */
export const DEFAULT_LANG: Lang = 'en'

export const LANGUAGE_LABELS: Record<Lang, string> = {
  en: 'English',
  de: 'Deutsch',
}

export function isLang(value: string | undefined): value is Lang {
  return value !== undefined && (LANGUAGES as readonly string[]).includes(value)
}

/**
 * localStorage key shared by the detector and the switcher. On GitHub Pages the docs and the
 * project website share an origin and this key, so a language chosen in one applies to both.
 */
export const LANG_STORAGE_KEY = 'nodepilot-docs-lang'

/**
 * Language for a first-time visitor: a stored choice wins, otherwise the browser's
 * preference, otherwise English. Only the primary subtag counts, so `de-AT` and `de-CH`
 * both resolve to `de`.
 */
export function detectLang(): Lang {
  if (typeof window === 'undefined') return DEFAULT_LANG

  const stored = readStoredLang()
  if (stored) return stored

  for (const candidate of window.navigator?.languages ?? []) {
    const primary = candidate.split('-')[0]?.toLowerCase()
    if (isLang(primary)) return primary
  }
  return DEFAULT_LANG
}

/** The stored language, or null when none is stored or storage is blocked. */
function readStoredLang(): Lang | null {
  try {
    // Blocked storage throws from the `localStorage` getter itself, so `?.` alone is not enough.
    const stored = window.localStorage?.getItem(LANG_STORAGE_KEY)
    return isLang(stored ?? undefined) ? (stored as Lang) : null
  } catch {
    return null
  }
}

/**
 * Split `/de/getting-started/introduction` into its language and content path.
 *
 * `lang` is null for a URL without a language segment, which covers `/`, links that carry
 * no language, and hand-typed paths. The router answers all three the same way: redirect
 * to the detected language and keep the page.
 *
 * Lives here rather than in the router so it can be tested without rendering.
 */
export function parseLocation(pathname: string): { lang: Lang | null; current: string } {
  const segments = pathname.replace(/^\/+/, '').replace(/\/+$/, '').split('/').filter(Boolean)
  if (segments.length > 0 && isLang(segments[0])) {
    return { lang: segments[0], current: segments.slice(1).join('/') }
  }
  return { lang: null, current: segments.join('/') }
}
