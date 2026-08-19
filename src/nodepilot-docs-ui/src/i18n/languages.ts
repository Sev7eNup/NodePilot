/**
 * The two languages the documentation ships in.
 *
 * This module is deliberately free of i18next imports: the router, the content loader
 * and the Vite build all need to reason about languages before (and without) an
 * initialised i18n instance.
 */

export const LANGUAGES = ['en', 'de'] as const

export type Lang = (typeof LANGUAGES)[number]

/** Fallback for every lookup — English is the documented default for new visitors. */
export const DEFAULT_LANG: Lang = 'en'

export const LANGUAGE_LABELS: Record<Lang, string> = {
  en: 'English',
  de: 'Deutsch',
}

export function isLang(value: string | undefined): value is Lang {
  return value !== undefined && (LANGUAGES as readonly string[]).includes(value)
}

/** localStorage key — shared by the detector and the switcher. */
export const LANG_STORAGE_KEY = 'nodepilot-docs-lang'

/**
 * Language for a first-time visitor: an explicit earlier choice wins, otherwise the
 * browser's preference, otherwise English. Only the *primary* subtag is honoured, so
 * `de-AT` and `de-CH` both resolve to `de`.
 */
export function detectLang(): Lang {
  if (typeof window === 'undefined') return DEFAULT_LANG

  const stored = window.localStorage?.getItem(LANG_STORAGE_KEY)
  if (isLang(stored ?? undefined)) return stored as Lang

  for (const candidate of window.navigator?.languages ?? []) {
    const primary = candidate.split('-')[0]?.toLowerCase()
    if (isLang(primary)) return primary
  }
  return DEFAULT_LANG
}

/**
 * Split `/de/getting-started/introduction` into its language and content path.
 *
 * `lang` is null for a URL without a language segment — that covers `/`, deep links minted
 * before the site became bilingual, and anything hand-typed. The router answers all three
 * the same way: redirect to the detected language, keeping the page.
 *
 * Lives here rather than in the router so it can be tested without rendering: getting this
 * split wrong serves the wrong language, or eats the first path segment, and neither is
 * loud enough to notice while clicking around.
 */
export function parseLocation(pathname: string): { lang: Lang | null; current: string } {
  const segments = pathname.replace(/^\/+/, '').replace(/\/+$/, '').split('/').filter(Boolean)
  if (segments.length > 0 && isLang(segments[0])) {
    return { lang: segments[0], current: segments.slice(1).join('/') }
  }
  return { lang: null, current: segments.join('/') }
}
