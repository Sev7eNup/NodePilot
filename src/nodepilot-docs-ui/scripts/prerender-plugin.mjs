import { articleBody } from './blog-content.mjs'
import { articles } from '../src/site/blog.ts'
// Build and client navigation share the same content, links and metadata renderer.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseHTML } from 'linkedom'
import { keepPage, llmsTxt, originPrefix, rewriteRelativeUrls, robots, routePages, setBaseMeta, sitemap } from '../src/site/prerender.ts'
import { renderSiteContent, renderSiteHead } from '../src/site/seo.ts'
import { resolveRoute } from '../src/site/router.ts'
import { experienceMarkup } from '../src/site/experience/markup.ts'

export function prerenderSite(outDir, origin, preview = process.env.NP_BLOG_PREVIEW === '1') {
  for (const article of articles) {
    if (!['draft', 'published'].includes(article.status)) throw new Error(`Invalid publication state: ${article.slug}`)
    for (const key of ['publishedAt', 'modifiedAt']) {
      const date = article[key]
      if (date && (!/^\d{4}-\d{2}-\d{2}$/.test(date) || new Date(date).toISOString().slice(0, 10) !== date)) throw new Error(`Invalid ${key}: ${article.slug}`)
    }
    if (article.status === 'published' && !article.publishedAt) throw new Error(`Published article needs its actual publication date: ${article.slug}`)
    if (article.status === 'draft' && article.publishedAt) throw new Error(`Draft cannot have a publication date: ${article.slug}`)
    if (article.modifiedAt && (!article.publishedAt || article.modifiedAt < article.publishedAt)) throw new Error(`Invalid modification date: ${article.slug}`)
  }
  const root = outDir ?? resolve(fileURLToPath(new URL('..', import.meta.url)), 'dist-site')
  const shell = readFileSync(join(root, 'index.html'), 'utf8')
  const pages = routePages(preview)
  const prefix = originPrefix(origin)
  for (const page of pages) {
    const { document } = parseHTML(setBaseMeta(rewriteRelativeUrls(shell, prefix), prefix))
    keepPage(document, page.route)
    document.querySelector('meta[name="np-site-origin"]').setAttribute('content', origin)
    for (const element of document.querySelectorAll('[data-legal]')) {
      element.innerHTML = readFileSync(new URL(`../src/site/legal/${element.getAttribute('data-legal')}.de.html`, import.meta.url), 'utf8')
    }
    const experienceRoot = document.getElementById('experience-root')
    if (experienceRoot) experienceRoot.innerHTML = experienceMarkup(page.lang, prefix)
    const previewMeta = document.createElement('meta')
    previewMeta.setAttribute('name', 'np-blog-preview')
    previewMeta.setAttribute('content', preview ? '1' : '0')
    document.head.appendChild(previewMeta)
    renderSiteContent(document, page.route, page.lang, prefix, page.route.page === 'article' ? articleBody(page.route.slug, page.lang) : undefined)
    renderSiteHead(document, page.route, page.lang, origin)
    const target = join(root, page.file)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, document.toString())
  }
  writeFileSync(join(root, 'sitemap.xml'), sitemap(origin, pages))
  writeFileSync(join(root, 'robots.txt'), robots(origin))
  writeFileSync(join(root, 'llms.txt'), llmsTxt(origin, pages))
  return pages.length
}

/** Dev server: every route address gets the one shell, cut down to that route's section as in the build. */
export function siteDevRoutes() {
  return {
    name: 'np-site-dev-routes',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use((request, _response, next) => {
        const { pathname } = new URL(request.url ?? '/', 'http://localhost')
        if (pathname.endsWith('/') && resolveRoute(pathname.slice(1)).page !== 'notfound') request.url = '/index.html'
        next()
      })
    },
    transformIndexHtml(html, { originalUrl }) {
      const { document } = parseHTML(setBaseMeta(html, '/'))
      keepPage(document, resolveRoute(new URL(originalUrl ?? '/', 'http://localhost').pathname.slice(1)))
      return document.toString()
    },
  }
}
