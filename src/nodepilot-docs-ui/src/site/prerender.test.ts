import { describe, expect, it } from 'vitest'
import {
  applyMeta,
  depthPrefix,
  originPrefix,
  pageUrl,
  revealPage,
  rewriteRelativeUrls,
  robots,
  routePages,
  setBaseMeta,
  sitemap,
} from './prerender'
import { ARTICLE_SLUGS, resolveRoute } from './router'

const pages = routePages()

describe('routePages', () => {
  it('gives every route its own file and the not-found page the name both hosts serve', () => {
    const files = Object.fromEntries(pages.map((page) => [page.path, page.file]))
    expect(files['']).toBe('index.html')
    expect(files['product']).toBe('product/index.html')
    expect(files['blog/scorch-import']).toBe('blog/scorch-import/index.html')
    expect(files['404']).toBe('404.html')
  })

  it('covers every article and leaves only the not-found page out of the sitemap', () => {
    const listed = pages.filter((page) => page.listed).map((page) => page.path)
    for (const slug of ARTICLE_SLUGS) expect(listed).toContain(`blog/${slug}`)
    expect(listed).not.toContain('404')
  })

  it('states the depth its relative URLs have to bridge', () => {
    expect(pages.find((page) => page.path === '')?.depth).toBe(0)
    expect(pages.find((page) => page.path === 'product')?.depth).toBe(1)
    expect(pages.find((page) => page.path === 'blog/scorch-import')?.depth).toBe(2)
    // The 404 is served from the root for any address, so it must not reach upwards.
    expect(pages.find((page) => page.path === '404')?.depth).toBe(0)
  })

  it('writes files the router resolves back to the same page', () => {
    for (const page of pages.filter((entry) => entry.listed)) {
      expect(resolveRoute(page.path), page.path).toEqual(page.route)
    }
  })
})

describe('rewriteRelativeUrls', () => {
  const html = '<script src="./legacy.js"></script><a href="product">P</a><a href="docs/#/de/">D</a>'

  it('leaves the root document untouched', () => {
    expect(rewriteRelativeUrls(html, depthPrefix(0))).toBe(html)
  })

  it('lifts every relative URL by the depth of the file', () => {
    expect(rewriteRelativeUrls(html, depthPrefix(1))).toBe(
      '<script src="../legacy.js"></script><a href="../product">P</a><a href="../docs/#/de/">D</a>',
    )
    expect(rewriteRelativeUrls(html, depthPrefix(2))).toContain('href="../../product"')
  })

  it('leaves absolute, anchor and mail URLs alone', () => {
    const fixed = '<a href="https://example.test/x">x</a><a href="#main">m</a><a href="mailto:a@b.test">a</a><img src="/og.png">'
    expect(rewriteRelativeUrls(fixed, depthPrefix(2))).toBe(fixed)
  })
})

describe('applyMeta', () => {
  const shell =
    '<title>Old</title><meta name="description" content="old">' +
    '<link rel="canonical" href="https://old.test/">' +
    '<meta property="og:title" content="old"><meta property="og:description" content="old">' +
    '<meta property="og:url" content="https://old.test/">'

  it('replaces title, description, canonical and Open Graph tags', () => {
    const html = applyMeta(shell, { title: 'Neu', description: 'Text', url: 'https://x.test/product' })
    expect(html).toContain('<title>Neu</title>')
    expect(html).toContain('<meta name="description" content="Text">')
    expect(html).toContain('<link rel="canonical" href="https://x.test/product">')
    expect(html).toContain('<meta property="og:url" content="https://x.test/product">')
  })

  it('escapes quotes and angle brackets instead of breaking the tag', () => {
    const html = applyMeta(shell, { title: 'a "b" <c>', description: '"q"', url: 'https://x.test/' })
    expect(html).toContain('<title>a "b" &lt;c&gt;</title>')
    expect(html).toContain('content="&quot;q&quot;"')
  })

  it('drops the canonical URL of the not-found page, so it is not offered for indexing', () => {
    const html = applyMeta(shell, { title: '404', description: 'weg', url: '' })
    expect(html).not.toContain('rel="canonical"')
    expect(html).not.toContain('og:url')
  })
})

describe('revealPage', () => {
  const shell =
    '<div id="home-page" class="page">home</div><div id="product-page" class="page" hidden>p</div>' +
    '<article id="article-page" class="page article-page" hidden>a</article>'

  it('leaves the home page as the visible one', () => {
    expect(revealPage(shell, { page: 'home' })).toBe(shell)
  })

  it('shows the route and hides the home page', () => {
    const html = revealPage(shell, { page: 'product' })
    expect(html).toContain('<div id="home-page" class="page" hidden>')
    expect(html).toContain('<div id="product-page" class="page">')
  })

  it('finds a section whatever element carries it', () => {
    const html = revealPage(shell, { page: 'article', slug: 'scorch-import' })
    expect(html).toContain('<article id="article-page" class="page article-page">')
  })
})

describe('sitemap and robots', () => {
  it('lists every indexable route as an absolute URL', () => {
    const xml = sitemap('https://x.test', pages)
    expect(xml).toContain('<loc>https://x.test/</loc>')
    expect(xml).toContain('<loc>https://x.test/blog/scorch-import/</loc>')
    expect(xml).not.toContain('404')
    expect(xml.match(/<loc>/g)).toHaveLength(pages.filter((page) => page.listed).length)
  })

  it('points robots.txt at the sitemap of the same origin', () => {
    expect(robots('https://x.test/')).toContain('Sitemap: https://x.test/sitemap.xml')
  })

  it('builds the address the server answers on', () => {
    // A directory route answers under its trailing slash; without it Apache redirects first.
    expect(pageUrl('https://x.test/', 'product', true)).toBe('https://x.test/product/')
    expect(pageUrl('https://x.test/', 'product')).toBe('https://x.test/product')
    expect(pageUrl('https://x.test', '')).toBe('https://x.test/')
  })
})

describe('setBaseMeta', () => {
  const shell = '<meta name="np-site-base" content="./">'

  it('leaves the root file pointing at itself', () => {
    expect(setBaseMeta(shell, depthPrefix(0))).toBe('<meta name="np-site-base" content="./">')
  })

  it('reaches up once per directory level', () => {
    expect(setBaseMeta(shell, depthPrefix(1))).toBe('<meta name="np-site-base" content="../">')
    expect(setBaseMeta(shell, depthPrefix(2))).toBe('<meta name="np-site-base" content="../../">')
  })
})

describe('originPrefix', () => {
  it('is an absolute path, because the not-found page is served for any address', () => {
    expect(originPrefix('https://www.nodepilot.run')).toBe('/')
    expect(originPrefix('https://www.nodepilot.run/')).toBe('/')
    expect(originPrefix('https://sev7enup.github.io/NodePilot')).toBe('/NodePilot/')
  })

  it('reaches the assets of a page that was never there', () => {
    const shell = '<link rel="stylesheet" href="assets/site.css"><a href="blog/">b</a>'
    expect(rewriteRelativeUrls(shell, originPrefix('https://www.nodepilot.run'))).toBe(
      '<link rel="stylesheet" href="/assets/site.css"><a href="/blog/">b</a>',
    )
  })
})
