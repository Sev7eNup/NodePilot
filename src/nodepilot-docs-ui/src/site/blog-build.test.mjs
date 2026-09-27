import { describe, it, expect } from 'vitest'
import { readFileSync, writeFileSync, mkdtempSync, rmSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { parseHTML } from 'linkedom'
import { prerenderSite } from '../../scripts/prerender-plugin.mjs'
import { articleBody } from '../../scripts/blog-content.mjs'
import { articles } from './blog.ts'
import { allPages } from '../data/nav.ts'

describe('article publication and preview boundary', () => {
  for (const preview of [false, true]) {
    it(`${preview ? 'preview' : 'production'} emits complete articles and respects publication state`, () => {
      const root = mkdtempSync(join(tmpdir(), 'nodepilot-blog-'))
      try {
        writeFileSync(join(root, 'index.html'), readFileSync(new URL('./index.html', import.meta.url)))
        prerenderSite(root, 'https://example.test/subpath', preview)
        const sitemap = readFileSync(join(root, 'sitemap.xml'), 'utf8')
        const index = parseHTML(readFileSync(join(root, 'blog/index.html'), 'utf8')).document
        expect(index.querySelectorAll('.blog-index-row').length).toBe(preview ? 15 : 3)
        expect(index.querySelectorAll('.blog-index-row[data-category="hintergrund"]').length).toBe(preview ? 3 : 1)
        for (const article of articles) {
          expect(allPages.some(page => page.path === article.docs), article.docs).toBe(true)
          for (const related of article.related) expect(articles.some(candidate => candidate.slug === related)).toBe(true)
          for (const lang of ['de', 'en']) {
            const route = `${lang === 'en' ? 'en/' : ''}blog/${article.slug}`
            const file = join(root, route, 'index.html')
            expect(existsSync(file), route).toBe(preview || article.status === 'published')
            if (article.status === 'draft') expect(sitemap).not.toContain(`${route}/`)
            else expect(sitemap).toContain(`${route}/`)
            if (!existsSync(file)) continue
            const doc = parseHTML(readFileSync(file, 'utf8')).document
            const body = doc.querySelector('#article-body')
            expect(body.textContent.length, route).toBeGreaterThan(800)
            expect(body.querySelector('h1')).toBeNull()
            expect(body.querySelector('link[rel="preload"]'), 'Markdown source image paths must not leak into preload hints').toBeNull()
            expect(doc.querySelector('#article-title').textContent).toBe(article.text[lang].title)
            const graph = JSON.parse(doc.querySelector('[data-site-schema]').textContent)['@graph']
            const posting = graph.find(item => item['@type'] === 'BlogPosting')
            expect(posting.author.name).toBe('NodePilot')
            if (article.status === 'draft') {
              expect(doc.querySelector('meta[name="robots"]').content).toContain('noindex')
              expect(posting.datePublished).toBeUndefined()
            } else expect(posting.datePublished).toBe(article.publishedAt)
            expect(posting.dateModified).toBe(article.modifiedAt ?? undefined)
            for (const image of body.querySelectorAll('img')) {
              expect(image.getAttribute('src')).toMatch(/^\/subpath\/site-images\/.*\.webp$/)
              expect(image.getAttribute('srcset')).toContain('480w')
              expect(image.getAttribute('sizes')).toContain('100vw')
              expect(image.getAttribute('alt')).toBeTruthy()
            }
            for (const link of doc.querySelectorAll('[data-site-path]')) {
              const target = link.getAttribute('href')
              if (!preview) for (const draft of articles.filter(a => a.status === 'draft')) expect(target).not.toContain(`/blog/${draft.slug}/`)
            }
          }
        }
      } finally { rmSync(root, { recursive: true, force: true }) }
    })
  }

  it('preserves the tutorial and fictional migration disclosures in both languages', () => {
    for (const lang of ['de', 'en']) {
      expect(articleBody('first-workflow', lang)).toContain('Greeting Probe')
      expect(articleBody('scorch-migration', lang)).toMatch(/konstruiert|fictional/i)
    }
  })
})
