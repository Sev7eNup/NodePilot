import { describe, expect, it } from 'vitest'
import { ARTICLE_SLUGS, resolveRoute, SITE_ROUTE_SEGMENTS } from './router'

describe('resolveRoute', () => {
  it('maps the site root and the empty hash to the home page', () => {
    expect(resolveRoute('#/')).toEqual({ page: 'home' })
    // The back button lands on the start URL, which has no hash at all.
    expect(resolveRoute('')).toEqual({ page: 'home' })
    expect(resolveRoute('#')).toEqual({ page: 'home' })
  })

  it('maps every page route', () => {
    expect(resolveRoute('#/erleben')).toEqual({ page: 'experience' })
    expect(resolveRoute('#/produkt')).toEqual({ page: 'product' })
    expect(resolveRoute('#/blog')).toEqual({ page: 'blog' })
    expect(resolveRoute('#/impressum')).toEqual({ page: 'impressum' })
    expect(resolveRoute('#/datenschutz')).toEqual({ page: 'datenschutz' })
  })

  it('maps article slugs to the article page', () => {
    for (const slug of ARTICLE_SLUGS) {
      expect(resolveRoute(`#/blog/${slug}`)).toEqual({ page: 'article', slug })
    }
  })

  it('tolerates trailing slashes', () => {
    expect(resolveRoute('#/erleben/')).toEqual({ page: 'experience' })
    expect(resolveRoute('#//')).toEqual({ page: 'home' })
    expect(resolveRoute('#/produkt/')).toEqual({ page: 'product' })
    expect(resolveRoute('#/blog//')).toEqual({ page: 'blog' })
    expect(resolveRoute('#/datenschutz/')).toEqual({ page: 'datenschutz' })
    expect(resolveRoute('#/blog/scorch-import/')).toEqual({ page: 'article', slug: 'scorch-import' })
  })

  it('decodes percent-encoded paths', () => {
    expect(resolveRoute('#/blog/scorch%2Dimport')).toEqual({ page: 'article', slug: 'scorch-import' })
  })

  it('answers unknown routes and unknown articles with the not-found page', () => {
    expect(resolveRoute('#/unbekannt')).toEqual({ page: 'notfound' })
    expect(resolveRoute('#/blog/unknown-post')).toEqual({ page: 'notfound' })
    expect(resolveRoute('#/blog/warum-nodepilot/extra')).toEqual({ page: 'notfound' })
    expect(resolveRoute('#/Produkt')).toEqual({ page: 'notfound' })
  })

  it('answers a malformed escape with the not-found page instead of throwing', () => {
    expect(resolveRoute('#/%E0%A4%A')).toEqual({ page: 'notfound' })
    expect(resolveRoute('#/blog/%zz')).toEqual({ page: 'notfound' })
  })

  it('returns null for a hash that is not a route, so the current page stays', () => {
    // The skip link targets #main-content; it must not turn the page into a 404.
    expect(resolveRoute('#main-content')).toBeNull()
    expect(resolveRoute('#top')).toBeNull()
  })

  it('resolves every owned first segment to a real page', () => {
    for (const segment of SITE_ROUTE_SEGMENTS) {
      const route = resolveRoute(`#/${segment}`)
      expect(route, segment).not.toBeNull()
      expect(route?.page, segment).not.toBe('notfound')
    }
  })
})
