import { allPages } from '../data/nav'
import { DEFAULT_LANG, isLang, type Lang } from '../i18n/languages'

// Eager-import every Markdown file under content/ as a raw string.
// Vite resolves this at build time into a map keyed like "de/getting-started/introduction".
const modules = import.meta.glob('../../content/**/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

/** Per-language content, keyed by nav path (language segment stripped). */
export const contentByLang: Record<Lang, Record<string, string>> = { en: {}, de: {} }

for (const [filePath, raw] of Object.entries(modules)) {
  // Turn ".../content/de/getting-started/introduction.md" into "de/getting-started/introduction".
  const key = filePath.replace(/^.*\/content\//, '').replace(/\.md$/, '')
  const slash = key.indexOf('/')
  if (slash < 0) continue
  const lang = key.slice(0, slash)
  const path = key.slice(slash + 1)
  if (isLang(lang)) contentByLang[lang][path] = raw
}

/**
 * Page content in the requested language, falling back to {@link DEFAULT_LANG}.
 *
 * The fallback keeps a partly translated corpus usable: a page that exists only in the
 * default language still renders instead of returning a 404.
 */
export function getContent(lang: Lang, path: string): string | undefined {
  return contentByLang[lang]?.[path] ?? contentByLang[DEFAULT_LANG]?.[path]
}

/** True when the page exists in `lang` itself rather than only via the fallback. */
export function hasTranslation(lang: Lang, path: string): boolean {
  return contentByLang[lang]?.[path] !== undefined
}

/** Every page that resolves to actual content in at least the fallback language. */
export const availablePages = allPages.filter(
  (p) => contentByLang[DEFAULT_LANG][p.path] !== undefined,
)
