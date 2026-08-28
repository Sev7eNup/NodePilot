import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DEFAULT_LANG,
  detectLang,
  isLang,
  LANG_STORAGE_KEY,
  LANGUAGES,
  parseLocation,
} from './languages'

/** Minimal `window` stand-in: `detectLang` touches only these two members. */
function stubWindow(options: { stored?: string | null; languages?: string[] }) {
  vi.stubGlobal('window', {
    localStorage: {
      getItem: (key: string) => (key === LANG_STORAGE_KEY ? (options.stored ?? null) : null),
    },
    navigator: { languages: options.languages ?? [] },
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('isLang', () => {
  it('accepts the shipped languages and nothing else', () => {
    expect(LANGUAGES.every(isLang)).toBe(true)
    expect(isLang('fr')).toBe(false)
    expect(isLang('DE')).toBe(false) // callers lower-case first; the guard stays strict
    expect(isLang(undefined)).toBe(false)
    expect(isLang('')).toBe(false)
  })
})

describe('parseLocation', () => {
  it('splits a language-prefixed path', () => {
    expect(parseLocation('/de/getting-started/introduction')).toEqual({
      lang: 'de',
      current: 'getting-started/introduction',
    })
    expect(parseLocation('/en/cli')).toEqual({ lang: 'en', current: 'cli' })
  })

  it('reports a bare language as the language with no page', () => {
    // `/de` is what the back-to-start link produces; App fills in the home page.
    expect(parseLocation('/de')).toEqual({ lang: 'de', current: '' })
  })

  it('returns lang=null for the root so the router redirects', () => {
    expect(parseLocation('/')).toEqual({ lang: null, current: '' })
    expect(parseLocation('')).toEqual({ lang: null, current: '' })
  })

  it('keeps the page of a language-less deep link', () => {
    // A link without a language keeps its page; only the language is filled in. Losing
    // `current` here would send every such link to the start page.
    expect(parseLocation('/security/hardening')).toEqual({
      lang: null,
      current: 'security/hardening',
    })
  })

  it('does not mistake a content segment for a language', () => {
    // A short first segment must not be taken for a language code. No real top-level
    // segment collides with one, and this pins that.
    expect(parseLocation('/enterprise/folder-rbac')).toEqual({
      lang: null,
      current: 'enterprise/folder-rbac',
    })
  })

  it('tolerates duplicate and trailing slashes', () => {
    expect(parseLocation('//de//cli//')).toEqual({ lang: 'de', current: 'cli' })
    expect(parseLocation('/de/')).toEqual({ lang: 'de', current: '' })
  })
})

describe('detectLang', () => {
  it('prefers a stored choice over the browser language', () => {
    stubWindow({ stored: 'de', languages: ['en-US'] })
    expect(detectLang()).toBe('de')
  })

  it('falls back to the browser language when nothing is stored', () => {
    stubWindow({ stored: null, languages: ['de-AT', 'en'] })
    expect(detectLang()).toBe('de')
  })

  it('matches on the primary subtag only', () => {
    stubWindow({ stored: null, languages: ['de-CH'] })
    expect(detectLang()).toBe('de')
  })

  it('skips unsupported browser languages and takes the first supported one', () => {
    stubWindow({ stored: null, languages: ['fr-FR', 'it', 'en-GB'] })
    expect(detectLang()).toBe('en')
  })

  it('defaults to English for an unsupported browser language', () => {
    stubWindow({ stored: null, languages: ['fr-FR'] })
    expect(detectLang()).toBe(DEFAULT_LANG)
    expect(DEFAULT_LANG).toBe('en')
  })

  it('ignores a stored value that is no longer a shipped language', () => {
    stubWindow({ stored: 'fr', languages: ['de'] })
    expect(detectLang()).toBe('de')
  })
})
