import { describe, expect, it } from 'vitest'
import { contentByLang, getContent, hasTranslation } from './content'
import { allPages, navGroups } from '../data/nav'
import de from '../i18n/locales/de.json'
import en from '../i18n/locales/en.json'
import { DEFAULT_LANG, LANGUAGES, type Lang } from '../i18n/languages'

const localeFiles: Record<Lang, typeof en> = { en, de }

/**
 * Parity guards for the bilingual corpus.
 *
 * A second language adds exactly one failure mode that nothing else catches: the two trees
 * drift. A page added in English only 404s for German readers; a nav entry without a title
 * in one locale renders its raw translation key. Both look fine to whoever is working in
 * their own language, which is why these are machine-checked.
 */
describe('content / nav parity', () => {
  it('ships every nav page in every language', () => {
    const missing: string[] = []
    for (const lang of LANGUAGES) {
      for (const page of allPages) {
        if (!hasTranslation(lang, page.path)) missing.push(`${lang}/${page.path}`)
      }
    }
    expect(missing).toEqual([])
  })

  it('has no content file that the nav does not link', () => {
    const known = new Set(allPages.map((p) => p.path))
    const orphans: string[] = []
    for (const lang of LANGUAGES) {
      for (const path of Object.keys(contentByLang[lang])) {
        if (!known.has(path)) orphans.push(`${lang}/${path}`)
      }
    }
    expect(orphans).toEqual([])
  })

  it('keeps both language trees on the same set of paths', () => {
    const [first, ...rest] = LANGUAGES.map((lang) => Object.keys(contentByLang[lang]).sort())
    for (const other of rest) expect(other).toEqual(first)
  })

  it('titles every nav page and group in every locale', () => {
    const missing: string[] = []
    for (const lang of LANGUAGES) {
      const locale = localeFiles[lang]
      for (const group of navGroups) {
        if (!locale.nav.groups[group.id as keyof typeof locale.nav.groups]) {
          missing.push(`${lang}: nav.groups.${group.id}`)
        }
      }
      for (const page of allPages) {
        if (!locale.nav.pages[page.path as keyof typeof locale.nav.pages]) {
          missing.push(`${lang}: nav.pages.${page.path}`)
        }
      }
    }
    expect(missing).toEqual([])
  })

  it('carries no stale nav title for a page that no longer exists', () => {
    const known = new Set(allPages.map((p) => p.path))
    const stale: string[] = []
    for (const lang of LANGUAGES) {
      for (const path of Object.keys(localeFiles[lang].nav.pages)) {
        if (!known.has(path)) stale.push(`${lang}: ${path}`)
      }
    }
    expect(stale).toEqual([])
  })

  it('exposes the same UI string keys in both locales', () => {
    // A key present in one locale only renders as the raw key for the other language.
    const keys = (o: Record<string, unknown>) => Object.keys(o).sort()
    expect(keys(de.ui)).toEqual(keys(en.ui))
  })

  it('uses page paths free of dots, which the i18next key separator would split', () => {
    // `navTitleKey` builds `nav.pages.<path>`; i18next splits keys on `.`, so a dotted page
    // path would silently resolve to nothing.
    expect(allPages.filter((p) => p.path.includes('.'))).toEqual([])
  })
})

describe('getContent', () => {
  it('returns the requested language', () => {
    const path = allPages[0].path
    expect(getContent('de', path)).toBe(contentByLang.de[path])
    expect(getContent('en', path)).toBe(contentByLang.en[path])
    expect(getContent('de', path)).not.toBe(getContent('en', path))
  })

  it('falls back to the default language for an untranslated page', () => {
    // Simulated rather than fixtured: the corpus is complete today (asserted above), and
    // this pins the behaviour that keeps a future half-translated page readable.
    const path = allPages[0].path
    const original = contentByLang.de[path]
    try {
      delete contentByLang.de[path]
      expect(hasTranslation('de', path)).toBe(false)
      expect(getContent('de', path)).toBe(contentByLang[DEFAULT_LANG][path])
    } finally {
      contentByLang.de[path] = original
    }
  })

  it('returns undefined for an unknown page in every language', () => {
    for (const lang of LANGUAGES) expect(getContent(lang, 'does/not/exist')).toBeUndefined()
  })
})
