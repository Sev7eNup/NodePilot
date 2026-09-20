import { describe, expect, it } from 'vitest'
import { allPages } from '../data/nav'
import { LANGUAGES } from '../i18n/languages'
import { docsHref, format, lookup, messages } from './i18n'
import { de } from './i18n/de'
import { en } from './i18n/en'
import siteHtml from './index.html?raw'
import { ARTICLE_SLUGS } from './router'

function leaves(node: unknown, prefix = ''): Array<[string, unknown]> {
  if (node === null || typeof node !== 'object') return [[prefix, node]]
  return Object.entries(node).flatMap(([key, value]) => leaves(value, prefix ? `${prefix}.${key}` : key))
}

function decodeEntities(text: string): string {
  return text.replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&')
}

const markup = siteHtml.replace(/<!--[\s\S]*?-->/g, '')
const usedKeys = [...markup.matchAll(/data-i18n(?:-html|-aria-label|-title|-placeholder)?="([^"]*)"/g)].map(
  (match) => match[1],
)

describe('website dictionaries', () => {
  it('provide a text for every key the markup uses, in both languages', () => {
    expect(usedKeys.length).toBeGreaterThan(100)
    for (const lang of LANGUAGES) {
      for (const key of usedKeys) expect(lookup(messages[lang], key), `${lang}: ${key}`).toBeTypeOf('string')
    }
  })

  it('define the same keys in German and English', () => {
    const keys = (dictionary: object) => leaves(dictionary).map(([key]) => key).sort()
    expect(keys(en)).toEqual(keys(de))
  })

  it('contain only non-empty texts', () => {
    for (const [key, value] of [...leaves(de), ...leaves(en)]) {
      expect(typeof value === 'string' && value.trim() !== '', key).toBe(true)
    }
  })

  it('define both articles in both languages', () => {
    for (const lang of LANGUAGES) {
      expect(Object.keys(messages[lang].articles).sort()).toEqual([...ARTICLE_SLUGS].sort())
    }
  })

  it('link article bodies to the docs in their own language', () => {
    for (const lang of LANGUAGES) {
      for (const slug of ARTICLE_SLUGS) {
        const linked = [...messages[lang].articles[slug].body.matchAll(/href="docs\/#\/([^/"]*)/g)].map((m) => m[1])
        for (const linkLang of linked) expect(linkLang, `${lang}: ${slug}`).toBe(lang)
      }
    }
  })

  it('keep the placeholders the script fills', () => {
    for (const lang of LANGUAGES) {
      expect(messages[lang].blog.foundMany).toContain('{count}')
      expect(messages[lang].gallery.caption).toContain('{title}')
    }
  })

  it('carry no draft or prototype wording', () => {
    const texts = [markup, ...leaves(de).map(([, value]) => String(value)), ...leaves(en).map(([, value]) => String(value))]
    for (const text of texts) expect(text).not.toMatch(/entwurf|entwürfe|prototyp|draft/i)
  })
})

describe('static markup', () => {
  // index.html carries the German texts for readers without JavaScript; they must match de.ts.
  it('matches the German dictionary', () => {
    const mismatches: string[] = []
    const check = (key: string, actual: string) => {
      if (lookup(de, key) !== decodeEntities(actual)) mismatches.push(`${key}: ${actual}`)
    }
    for (const [tag] of markup.matchAll(/<[a-z][^>]*>/gi)) {
      for (const attribute of ['aria-label', 'title', 'placeholder']) {
        const key = tag.match(new RegExp(`data-i18n-${attribute}="([^"]*)"`))?.[1]
        if (key) check(key, tag.match(new RegExp(`(?<![-\\w])${attribute}="([^"]*)"`))?.[1] ?? '')
      }
    }
    for (const [, key, text] of markup.matchAll(/data-i18n="([^"]*)"[^>]*>([^<]*)</g)) check(key, text)
    for (const [, , key, html] of markup.matchAll(/<(\w+)\b[^>]*\bdata-i18n-html="([^"]*)"[^>]*>([\s\S]*?)<\/\1>/g)) {
      check(key, html)
    }
    expect(mismatches).toEqual([])
  })

  it('points docs links at existing docs pages', () => {
    const paths = [...markup.matchAll(/data-docs-path="([^"]*)"/g)].map((match) => match[1])
    expect(paths).toContain('')
    expect(paths).toContain('getting-started/installation')
    for (const path of paths.filter(Boolean)) expect(allPages.map((page) => page.path)).toContain(path)
    for (const [tag] of markup.matchAll(/<a\b[^>]*data-docs-path="[^"]*"[^>]*>/g)) {
      const path = tag.match(/data-docs-path="([^"]*)"/)?.[1] ?? ''
      // The static href is the German link that applyLanguage would set.
      expect(tag).toContain(`href="${docsHref('de', path)}"`)
    }
  })
})

describe('lookup and format', () => {
  it('resolves nested keys and rejects missing or non-text keys', () => {
    expect(lookup(de, 'demo.nodes.check.name')).toBe('Dienst prüfen')
    expect(lookup(de, 'articles.scorch-import.category')).toBe('PRAXIS')
    expect(lookup(de, 'demo.nodes')).toBeUndefined()
    expect(lookup(de, 'demo.missing')).toBeUndefined()
    expect(lookup(de, '')).toBeUndefined()
  })

  it('fills known placeholders and leaves unknown ones', () => {
    expect(format('{count} posts found.', { count: 2 })).toBe('2 posts found.')
    expect(format('{title} · {other}', { title: 'Live Ops' })).toBe('Live Ops · {other}')
  })

  it('builds relative docs links', () => {
    expect(docsHref('en')).toBe('docs/#/en/')
    expect(docsHref('de', 'getting-started/installation')).toBe('docs/#/de/getting-started/installation')
  })
})
