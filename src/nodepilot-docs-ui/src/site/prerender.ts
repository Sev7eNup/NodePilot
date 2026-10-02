/**
 * Turns the single built shell into one file per website route. The string work itself lives in
 * ../lib/prerender-html.ts, which the documentation's prerenderer shares; what stays here is the
 * website's own vocabulary: its routes, its page sections, its robots.txt.
 *
 * Each route gets its own file with only its own page section, title, description, canonical URL
 * and sitemap entry, so a crawler sees one topic per address.
 */
import { articleBySlug, visibleArticles } from './blog'
import { pageUrl, setMeta, sitemapXml } from '../lib/prerender-html'
import { SOLUTION_SLUGS, localizedPath, type SiteRoute } from './router'

export { applyMeta, pageUrl, rewriteRelativeUrls } from '../lib/prerender-html'
export type { PageMeta } from '../lib/prerender-html'

export interface RoutePage {
  /** Path relative to the site root, without a leading slash. '' is the home page. */
  path: string
  lang: 'de' | 'en'
  /** File inside the build output. */
  file: string
  route: SiteRoute
  /** Left out of the sitemap: the not-found page. */
  listed: boolean
}

export function routePages(preview = false): RoutePage[] {
  const pages: SiteRoute[] = [
    { page: 'home' },
    { page: 'product' },
    { page: 'experience' },
    { page: 'videos' },
    { page: 'blog' },
    ...visibleArticles(preview).map(({ slug }) => ({ page: 'article', slug }) as SiteRoute),
    ...SOLUTION_SLUGS.map((slug) => ({ page: 'solution', slug }) as SiteRoute),
    { page: 'impressum' },
    { page: 'datenschutz' },
    { page: 'notfound' },
  ]
  return pages.flatMap((route) => {
    const languages: Array<'de' | 'en'> = ['impressum', 'datenschutz', 'notfound'].includes(route.page) ? ['de'] : ['de', 'en']
    return languages.map((lang) => {
      const path = localizedPath(route, lang)
      const notFound = route.page === 'notfound'
      return {
        path,
        lang,
        // A 404 has to be one file at the root: that is what Apache's ErrorDocument and
        // GitHub Pages both serve for an unknown address.
        file: notFound ? '404.html' : path === '' ? 'index.html' : `${path}/index.html`,
        route,
        listed: !notFound && (route.page !== 'article' || articleBySlug[route.slug].status === 'published'),
      }
    })
  })
}

/**
 * Path of the published origin: '/' on its own domain, '/NodePilot/' on GitHub Pages.
 *
 * Every page's URLs are written against it rather than against the file's own place in the
 * tree, because the not-found page is handed out for any address at any depth.
 */
export function originPrefix(origin: string): string {
  return `${new URL(origin).pathname.replace(/\/+$/, '')}/`
}

/**
 * Tells the shell where the site root is. main.ts reads it to turn the address into a route and
 * to build the links it sets from script; without it every subdirectory would look like the root.
 */
export function setBaseMeta(html: string, prefix: string): string {
  return setMeta(html, 'np-site-base', prefix || './')
}

/**
 * Keeps the route's own page section, visible, and removes every other one, so a file carries
 * only its own topic. `[id]` spares the footer wrapper, which is a `.page` too.
 */
export function keepPage(doc: Document, route: SiteRoute): void {
  for (const section of doc.querySelectorAll('main > .page[id]')) {
    if (section.id === `${route.page}-page`) section.removeAttribute('hidden')
    else section.remove()
  }
}

/** Articles carry their real publication or update date; other pages get none rather than a guessed one. */
export function sitemap(origin: string, pages: RoutePage[]): string {
  return sitemapXml(
    origin,
    pages.filter((page) => page.listed).map((page) => {
      const entry = page.route.page === 'article' ? articleBySlug[page.route.slug] : undefined
      return { path: page.path, lastmod: entry?.modifiedAt ?? entry?.publishedAt ?? undefined }
    }),
  )
}

/**
 * The documentation keeps its own sitemap, which has to be named here: a robots.txt only counts
 * at the origin root, so /docs/robots.txt would never be read.
 */
export function robots(origin: string, sitemaps: readonly string[] = ['sitemap.xml', 'docs/sitemap.xml']): string {
  const lines = sitemaps.map((path) => `Sitemap: ${pageUrl(origin, path)}`).join('\n')
  return `User-agent: *\nAllow: /\n\n${lines}\n`
}

/** llms.txt: a short English index of the site and the documentation for AI assistants. */
export function llmsTxt(origin: string, pages: RoutePage[]): string {
  const links = pages
    .filter((page) => page.listed && page.lang === 'en')
    .map((page) => `- [${page.route.page === 'home' ? 'Home' : page.path.replace(/^en\//, '')}](${pageUrl(origin, page.path)})`)
    .join('\n')
  const docs = `- [Documentation](${pageUrl(origin, 'docs/en/getting-started/introduction/')})\n- [Documentation sitemap](${pageUrl(origin, 'docs/sitemap.xml')})`
  return (
    '# NodePilot\n\n' +
    '> Self-hosted, agentless visual workflow automation for Windows and PowerShell, open source and free of charge.\n\n' +
    `## Site\n\n${links}\n\n## Documentation\n\n${docs}\n`
  )
}

/** The page keys the dictionaries carry a title and description for. */
export function metaKey(route: SiteRoute): string {
  return route.page === 'article' ? `article.${route.slug}` : route.page
}
