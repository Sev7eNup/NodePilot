import { articleBody } from '../../scripts/blog-content.mjs'
import shell from './index.html?raw'
import { parseHTML } from 'linkedom'
import { describe, expect, it } from 'vitest'
import { keepPage, routePages } from './prerender'
import { localizedPath, resolveRoute, routeLanguage } from './router'
import { renderSiteContent, renderSiteHead } from './seo'
import { videoEpisodes } from './videos'

const origin = 'https://example.test/preview'

describe('website SEO contract', () => {
  it('encodes DOM-provided path segments without changing directory separators', () => {
    const { document } = parseHTML(shell)
    const image = document.createElement('img')
    image.setAttribute('data-site-image', 'blog-images/a b<test>.png')
    const link = document.createElement('a')
    link.setAttribute('data-docs-path', 'guide/a?b#c')
    document.body.append(image, link)
    renderSiteContent(document, { page: 'home' }, 'de', '/preview/')
    expect(image.getAttribute('src')).toBe('/preview/blog-images/a%20b%3Ctest%3E.png')
    expect(link.getAttribute('href')).toBe('/preview/docs/de/guide/a%3Fb%23c/')
  })

  it('preserves German URLs and explicitly distinguishes English, including articles', () => {
    for (const page of routePages().filter(page => page.listed)) {
      expect(resolveRoute(page.path)).toEqual(page.route)
      expect(routeLanguage(page.path)).toBe(page.lang)
      expect(localizedPath(page.route, page.lang)).toBe(page.path)
    }
  })

  it('renders real article/solution bodies, valid schemas and localized links under a subpath', () => {
    const titles = new Set<string>()
    for (const page of routePages().filter(page => page.listed)) {
      const { document } = parseHTML(shell)
      keepPage(document, page.route)
      renderSiteContent(document, page.route, page.lang, '/preview/', page.route.page === 'article' ? articleBody(page.route.slug, page.lang) : undefined)
      renderSiteHead(document, page.route, page.lang, origin)
      const expected = `${origin}/${page.path}${page.path ? '/' : ''}`
      expect(document.querySelector('link[rel="canonical"]')?.getAttribute('href')).toBe(expected)
      for (const holder of document.querySelectorAll('[data-topic-links], [data-related-articles]')) expect(holder.closest('[id$="-page"]'), 'link sections belong to a route, not the global footer').not.toBeNull()
      expect(titles.has(document.title), document.title).toBe(false)
      titles.add(document.title)
      const schema = JSON.parse(document.querySelector('[data-site-schema]')!.textContent!)
      expect(schema['@graph'].find((item: Record<string, string>) => item['@id'] === `${expected}#page`)).toBeDefined()
      if (page.route.page === 'article' || page.route.page === 'solution') {
        const body = document.getElementById(`${page.route.page}-body`)!
        expect(body.textContent!.length).toBeGreaterThan(800)
        expect(body.querySelectorAll('h2[id]').length).toBeGreaterThan(2)
      }
      for (const link of document.querySelectorAll('[data-site-path], [data-docs-path]')) {
        expect(link.getAttribute('href')).toMatch(/^\/preview\//)
        expect(link.getAttribute('href')).not.toContain('/en/docs/')
      }
      for (const image of document.querySelectorAll('[data-site-image]')) expect(image.getAttribute('src')).toMatch(/^\/preview\/blog-images\//)
      if (['impressum', 'datenschutz'].includes(page.route.page)) expect(document.querySelectorAll('link[hreflang]')).toHaveLength(0)
      else expect(document.querySelectorAll('link[hreflang]')).toHaveLength(3)
    }
  })

  it('sends visitors of other languages to English and names the project as publisher', () => {
    const { document } = parseHTML(shell)
    renderSiteHead(document, { page: 'solution', slug: 'scorch-alternative' }, 'de', origin)
    expect(document.querySelector('link[hreflang="x-default"]')?.getAttribute('href')).toBe(`${origin}/en/scorch-alternative/`)
    const graph = JSON.parse(document.querySelector('[data-site-schema]')!.textContent!)['@graph']
    const organization = graph.find((item: Record<string, string>) => item['@type'] === 'Organization')
    expect(organization.logo).toBe(`${origin}/logo.png`)
    expect(graph.find((item: Record<string, string>) => item['@type'] === 'WebSite').publisher['@id']).toBe(organization['@id'])
  })

  it('marks exactly the open use case in the navigation', () => {
    const { document } = parseHTML(shell)
    renderSiteContent(document, { page: 'solution', slug: 'scorch-alternative' }, 'en', '/')
    const active = [...document.querySelectorAll('[data-nav][aria-current="page"]')]
    expect(active.map(link => link.getAttribute('data-nav'))).toEqual(['solution:scorch-alternative'])
    expect(active[0].getAttribute('href')).toBe('/en/scorch-alternative/')
  })

  it('updates all head data after navigation and gives unknown URLs no canonical', () => {
    const { document } = parseHTML(shell)
    renderSiteHead(document, { page: 'article', slug: 'first-workflow' }, 'en', origin)
    renderSiteHead(document, { page: 'product' }, 'de', origin)
    expect(document.querySelectorAll('[data-site-schema]')).toHaveLength(1)
    expect(document.querySelector('meta[property="og:type"]')?.getAttribute('content')).toBe('website')
    expect(document.querySelector('[data-site-schema]')!.textContent).not.toContain('BlogPosting')
    renderSiteHead(document, { page: 'notfound' }, 'de', origin)
    expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe('noindex, follow')
    expect(document.querySelectorAll('link[rel="canonical"], link[hreflang], [data-site-schema]')).toHaveLength(0)
  })
})

describe('training video gallery', () => {
  const render = (lang: 'de' | 'en', base: string) => {
    const { document } = parseHTML(shell)
    keepPage(document, { page: 'videos' })
    renderSiteContent(document, { page: 'videos' }, lang, base)
    return document
  }

  it('renders one card per episode with media URLs from the site root, in both languages and under a subpath', () => {
    for (const lang of ['de', 'en'] as const) {
      for (const base of ['/', '/NodePilot/']) {
        const cards = [...render(lang, base).querySelectorAll('#video-grid a.video-card')]
        expect(cards).toHaveLength(videoEpisodes.length)
        for (const [i, card] of cards.entries()) {
          const text = videoEpisodes[i].text[lang]
          expect(card.getAttribute('href')).toBe(`${base}${text.video}`)
          expect(card.getAttribute('href')).toMatch(new RegExp(`^${base}media/training/[a-z0-9-]+-${lang}\\.[0-9a-f]{10}\\.mp4$`))
          expect(card.querySelector('img')!.getAttribute('src')).toBe(`${base}${text.poster}`)
          expect(card.querySelector('.video-title')!.textContent).toBe(text.title)
        }
      }
    }
  })

  it('labels episode and duration and links YouTube only where the catalog has a URL', () => {
    const episode = videoEpisodes[4]
    const document = render('en', '/')
    const card = document.querySelector(`a.video-card[data-video="${episode.slug}"]`)!
    expect(card.querySelector('.video-episode')!.textContent).toBe('Episode 04')
    expect(card.querySelector('.video-duration')!.textContent).toMatch(/^\d:\d\d$/)
    expect(card.getAttribute('aria-label')).toBe(`Play video: ${episode.text.en.title}`)
    expect(card.hasAttribute('data-youtube')).toBe(Boolean(episode.text.en.youtube))

    const text = episode.text.en
    const saved = text.youtube
    try {
      text.youtube = 'https://youtu.be/abc123'
      expect(render('en', '/').querySelector(`a.video-card[data-video="${episode.slug}"]`)!.getAttribute('data-youtube')).toBe('https://youtu.be/abc123')
    } finally {
      if (saved) text.youtube = saved
      else delete text.youtube
    }
  })
})
