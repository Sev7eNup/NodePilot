/**
 * Turns the single built shell into one file per route. Pure string work, so the build step in
 * vite.site.config.ts stays a thin wrapper and the rules below are testable.
 *
 * Why at all: with client-side routing every address served the same HTML, so a crawler saw one
 * page with one title for the whole site. Each route now has its own file, its own title,
 * description and canonical URL, and its own entry in the sitemap.
 */
import { ARTICLE_SLUGS, routePath, type SiteRoute } from './router'

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

/** Absolute URL of a route on the published site. */
export function pageUrl(origin: string, path: string, directory = false): string {
  const base = origin.replace(/\/+$/, '')
  if (path === '') return `${base}/`
  return directory ? `${base}/${path}/` : `${base}/${path}`
}

const RELATIVE_URL = /\b(href|src)="(?!https?:|\/\/|\/|#|mailto:|tel:|data:)([^"]*)"/g

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
 * Puts `prefix` in front of every relative URL in the shell, which is written for the site
 * root: `href="product/"` becomes `href="/product/"` — for assets, the documentation and the
 * demo just as much as for the site's own routes.
 */
export function rewriteRelativeUrls(html: string, prefix: string): string {
  if (prefix === '') return html
  return html.replace(RELATIVE_URL, (_match, attribute: string, url: string) => {
    // `.` is the site root written as a relative URL; it becomes the prefix itself.
    const clean = url === '.' ? '' : url.replace(/^\.\//, '')
    return `${attribute}="${prefix}${clean}"`
  })
}

/**
 * Tells the shell where the site root is. main.ts reads it to turn the address into a route and
 * to build the links it sets from script; without it every subdirectory would look like the root.
 */
export function setBaseMeta(html: string, prefix: string): string {
  return html.replace(/(<meta name="np-site-base" content=")[^"]*(")/, `$1${prefix || './'}$2`)
}

export interface PageMeta {
  title: string
  description: string
  url: string
}

/** Replaces the shell's title, description, canonical and Open Graph tags for one route. */
export function applyMeta(html: string, meta: PageMeta): string {
  // The not-found page gets no address of its own: a canonical URL would invite a crawler
  // to index it.
  if (meta.url === '')
    html = html
      .replace(/<link rel="canonical"[^>]*>[\r\n]*/, '')
      .replace(/<meta property="og:url"[^>]*>[\r\n]*/, '')
  const escaped = escapeAttribute(meta.description)
  const title = escapeAttribute(meta.title)
  return html
    .replace(/<title>[\s\S]*?<\/title>/, `<title>${escapeText(meta.title)}</title>`)
    .replace(/(<meta name="description" content=")[^"]*(")/, `$1${escaped}$2`)
    .replace(/(<link rel="canonical" href=")[^"]*(")/, `$1${escapeAttribute(meta.url)}$2`)
    .replace(/(<meta property="og:title" content=")[^"]*(")/, `$1${title}$2`)
    .replace(/(<meta property="og:description" content=")[^"]*(")/, `$1${escaped}$2`)
    .replace(/(<meta property="og:url" content=")[^"]*(")/, `$1${escapeAttribute(meta.url)}$2`)
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
  const urls = pages
    .filter((page) => page.listed)
    .map((page) => `  <url><loc>${escapeText(pageUrl(origin, page.path, true))}</loc></url>`)
    .join('\n')
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`
}

export function robots(origin: string): string {
  return `User-agent: *\nAllow: /\n\nSitemap: ${pageUrl(origin, 'sitemap.xml')}\n`
}

/** The page keys the dictionaries carry a title and description for. */
export function metaKey(route: SiteRoute): string {
  return route.page === 'article' ? `article.${route.slug}` : route.page
}

function escapeText(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function escapeAttribute(value: string): string {
  return escapeText(value).replace(/"/g, '&quot;')
}
