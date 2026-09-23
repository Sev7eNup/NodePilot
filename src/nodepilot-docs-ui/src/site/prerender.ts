/**
 * Turns the single built shell into one file per website route. The string work itself lives in
 * ../lib/prerender-html.ts, which the documentation's prerenderer shares; what stays here is the
 * website's own vocabulary: its routes, its hidden page sections, its robots.txt.
 *
 * Why at all: with client-side routing every address served the same HTML, so a crawler saw one
 * page with one title for the whole site. Each route now has its own file, its own title,
 * description and canonical URL, and its own entry in the sitemap.
 */
import { pageUrl, setMeta, sitemapXml } from '../lib/prerender-html'
import { ARTICLE_SLUGS, routePath, type SiteRoute } from './router'

export { applyMeta, pageUrl, rewriteRelativeUrls } from '../lib/prerender-html'
export type { PageMeta } from '../lib/prerender-html'

export interface RoutePage {
  /** Path relative to the site root, without a leading slash. '' is the home page. */
  path: string
  /** File inside the build output. */
  file: string
  route: SiteRoute
  /** Left out of the sitemap: the not-found page. */
  listed: boolean
}

export function routePages(): RoutePage[] {
  const pages: SiteRoute[] = [
    { page: 'home' },
    { page: 'product' },
    { page: 'experience' },
    { page: 'blog' },
    ...ARTICLE_SLUGS.map((slug) => ({ page: 'article', slug }) as SiteRoute),
    { page: 'impressum' },
    { page: 'datenschutz' },
    { page: 'notfound' },
  ]
  return pages.map((route) => {
    const path = routePath(route)
    const notFound = route.page === 'notfound'
    return {
      path,
      // A 404 has to be one file at the root: that is what Apache's ErrorDocument and
      // GitHub Pages both serve for an unknown address.
      file: notFound ? '404.html' : path === '' ? 'index.html' : `${path}/index.html`,
      route,
      listed: !notFound,
    }
  })
}

/**
 * Path of the published origin: '/' on its own domain, '/NodePilot/' on GitHub Pages.
 *
 * Every page's URLs are written against it rather than against the file's own place in the
 * tree. A relative URL resolves against the *address*, not the file, and the address changes
 * under History API navigation: reached from the home page, `href="product/"` in that document
 * would resolve to /walkthrough/product/. The not-found page has the same problem for another
 * reason, being handed out for any address at any depth.
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

/** Shows the route's own section, so the file carries its content without running any script. */
export function revealPage(html: string, route: SiteRoute): string {
  if (route.page === 'home') return html
  const id = route.page === 'article' ? 'article-page' : `${route.page}-page`
  return html
    .replace(/(<[a-z]+ id="home-page"[^>]*)>/, '$1 hidden>')
    .replace(new RegExp('(<[a-z]+ id="' + id + '"[^>]*?)\\s+hidden'), '$1')
}

export function sitemap(origin: string, pages: RoutePage[]): string {
  return sitemapXml(
    origin,
    pages.filter((page) => page.listed).map((page) => page.path),
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

/** The page keys the dictionaries carry a title and description for. */
export function metaKey(route: SiteRoute): string {
  return route.page === 'article' ? `article.${route.slug}` : route.page
}
