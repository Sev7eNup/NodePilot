import { describe, expect, it } from 'vitest'
import { ARTICLE_SLUGS, resolveRoute, routePath, SITE_ROUTE_SEGMENTS } from './router'

describe('resolveRoute', () => {
  it('maps the site root to the home page', () => {
    expect(resolveRoute('')).toEqual({ page: 'home' })
    expect(resolveRoute('/')).toEqual({ page: 'home' })
  })

  it('maps every page route', () => {
    expect(resolveRoute('walkthrough')).toEqual({ page: 'experience' })
    expect(resolveRoute('product')).toEqual({ page: 'product' })
    expect(resolveRoute('blog')).toEqual({ page: 'blog' })
    expect(resolveRoute('impressum')).toEqual({ page: 'impressum' })
    expect(resolveRoute('datenschutz')).toEqual({ page: 'datenschutz' })
  })

  it('maps article slugs to the article page', () => {
    for (const slug of ARTICLE_SLUGS) {
      expect(resolveRoute(`blog/${slug}`)).toEqual({ page: 'article', slug })
    }
  })

  it('tolerates leading and trailing slashes', () => {
    expect(resolveRoute('/walkthrough')).toEqual({ page: 'experience' })
    expect(resolveRoute('product/')).toEqual({ page: 'product' })
    expect(resolveRoute('/blog//')).toEqual({ page: 'blog' })
    expect(resolveRoute('/blog/scorch-import/')).toEqual({ page: 'article', slug: 'scorch-import' })
  })

  it('decodes percent-encoded paths', () => {
    expect(resolveRoute('blog/scorch%2Dimport')).toEqual({ page: 'article', slug: 'scorch-import' })
  })

  it('answers unknown routes and unknown articles with the not-found page', () => {
    expect(resolveRoute('unbekannt')).toEqual({ page: 'notfound' })
    expect(resolveRoute('blog/unknown-post')).toEqual({ page: 'notfound' })
    expect(resolveRoute('blog/why-nodepilot/extra')).toEqual({ page: 'notfound' })
    expect(resolveRoute('Product')).toEqual({ page: 'notfound' })
    // The documentation and the demo are neighbours, not routes of this app.
    expect(resolveRoute('docs')).toEqual({ page: 'notfound' })
    expect(resolveRoute('demo')).toEqual({ page: 'notfound' })
  })

  it('answers a malformed escape with the not-found page instead of throwing', () => {
    expect(resolveRoute('%E0%A4%A')).toEqual({ page: 'notfound' })
    expect(resolveRoute('blog/%zz')).toEqual({ page: 'notfound' })
  })

  it('resolves every owned first segment to a real page', () => {
    for (const segment of SITE_ROUTE_SEGMENTS) {
      expect(resolveRoute(segment).page, segment).not.toBe('notfound')
    }
  })
})

describe('routePath', () => {
  it('is the inverse of resolveRoute for every page', () => {
    for (const segment of SITE_ROUTE_SEGMENTS) {
      const route = resolveRoute(segment)
      expect(routePath(route)).toBe(segment)
    }
    expect(routePath({ page: 'home' })).toBe('')
    for (const slug of ARTICLE_SLUGS) expect(routePath({ page: 'article', slug })).toBe(`blog/${slug}`)
  })
})
