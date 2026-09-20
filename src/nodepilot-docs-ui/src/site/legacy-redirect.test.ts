import { describe, expect, it } from 'vitest'
import { allPages } from '../data/nav'
import { LANGUAGES } from '../i18n/languages'
import redirectScript from './public/legacy-docs-redirect.js?raw'
import { SITE_ROUTE_SEGMENTS } from './router'

/** Runs the classic script against a fake `location` and returns the forward target, if any. */
function forwardTarget(hash: string): string | null {
  const calls: string[] = []
  const location = {
    hash,
    replace: (url: string) => {
      calls.push(url)
    },
  }
  new Function('location', redirectScript)(location)
  expect(calls.length).toBeLessThanOrEqual(1)
  return calls[0] ?? null
}

describe('legacy docs redirect', () => {
  it.each(['#/en/deployment/logs', '#/de', '#/de/getting-started/installation', '#/security/hardening'])(
    'forwards the old docs link %s to docs/',
    (hash) => {
      expect(forwardTarget(hash)).toBe(`docs/${hash}`)
    },
  )

  it.each([
    ['#/', '.'],
    ['#/produkt', 'product/'],
    ['#/produkt/', 'product/'],
    ['#/erleben', 'walkthrough/'],
    ['#/product', 'product/'],
    ['#/blog', 'blog/'],
    ['#/blog/warum-nodepilot', 'blog/why-nodepilot/'],
    ['#/blog/scorch-import', 'blog/scorch-import/'],
    ['#/impressum', 'impressum/'],
    ['#/datenschutz', 'datenschutz/'],
  ])('forwards the old website hash %j to its address %j', (hash, path) => {
    expect(forwardTarget(hash)).toBe(path)
  })

  it.each(['', '#main-content', '#top'])('leaves %j alone, so an in-page anchor still works', (hash) => {
    expect(forwardTarget(hash)).toBeNull()
  })

  it('lists exactly the router segments', () => {
    const literal = redirectScript.match(/\[([^\]]*)\]/)?.[1] ?? ''
    const listed = [...literal.matchAll(/'([^']*)'/g)].map((match) => match[1])
    expect(listed).toEqual([...SITE_ROUTE_SEGMENTS])
  })

  it('forwards every docs page, with and without language', () => {
    for (const page of allPages) {
      expect(forwardTarget(`#/${page.path}`), page.path).toBe(`docs/#/${page.path}`)
      for (const lang of LANGUAGES) {
        expect(forwardTarget(`#/${lang}/${page.path}`)).toBe(`docs/#/${lang}/${page.path}`)
      }
    }
  })

  it('owns no segment that the docs use', () => {
    const segments: readonly string[] = SITE_ROUTE_SEGMENTS
    for (const page of allPages) {
      expect(segments, page.path).not.toContain(page.path.split('/')[0])
    }
    for (const lang of LANGUAGES) {
      expect(segments).not.toContain(lang)
    }
  })
})
