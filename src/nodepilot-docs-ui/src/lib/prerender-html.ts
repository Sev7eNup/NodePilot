/**
 * String work shared by the two prerenderers: the website's (src/site/prerender.ts) and the
 * documentation's (scripts/prerender-docs.mjs). Both turn one built shell into one file per
 * address so that every address carries its own title, description and canonical URL.
 *
 * Pure functions, no DOM and no file system, so both build steps stay thin wrappers.
 */

/** Absolute URL of a page on the published site. */
export function pageUrl(origin: string, path: string, directory = false): string {
  const base = origin.replace(/\/+$/, '')
  if (path === '') return `${base}/`
  return directory ? `${base}/${path}/` : `${base}/${path}`
}

const RELATIVE_URL = /\b(href|src)="(?!https?:|\/\/|\/|#|mailto:|tel:|data:)([^"]*)"/g

/**
 * Puts `prefix` in front of every relative URL in the shell, which is written for the root of
 * its own tree: `src="./assets/app.js"` becomes `src="../../assets/app.js"` two levels in.
 */
export function rewriteRelativeUrls(html: string, prefix: string): string {
  if (prefix === '') return html
  return html.replace(RELATIVE_URL, (_match, attribute: string, url: string) => {
    // `.` is the root written as a relative URL; it becomes the prefix itself.
    const clean = url === '.' ? '' : url.replace(/^\.\//, '')
    return `${attribute}="${prefix}${clean}"`
  })
}

/**
 * Matches an attribute of a tag whose attributes a formatter may have spread over several
 * lines, as the documentation's index.html has them.
 */
function attribute(tag: string, key: string, value: string, target: string): RegExp {
  return new RegExp(`(<${tag}\\s[^>]*${key}="${value}"[^>]*${target}=")[^"]*(")`)
}

/** Replaces the content of a named meta tag. */
export function setMeta(html: string, name: string, value: string): string {
  return html.replace(attribute('meta', 'name', name, 'content'), `$1${escapeAttribute(value)}$2`)
}

/** Replaces the document language, which a screen reader and a crawler both read. */
export function setHtmlLang(html: string, lang: string): string {
  return html.replace(/(<html[^>]*\blang=")[^"]*(")/, `$1${escapeAttribute(lang)}$2`)
}

/**
 * Points each `hreflang` link at this page's address in that language. A language the page has
 * no translation for is dropped, so the pair never promises a page that does not exist.
 */
export function setAlternates(html: string, links: Record<string, string>): string {
  return html.replace(/[ \t]*<link rel="alternate" hreflang="([^"]*)"[^>]*>\r?\n?/g, (match, code: string) => {
    const href = links[code]
    if (href === undefined) return ''
    return match.replace(/(href=")[^"]*(")/, `$1${escapeAttribute(href)}$2`)
  })
}

export interface PageMeta {
  title: string
  description: string
  /** Empty for a page that must not be indexed: canonical and og:url are dropped. */
  url: string
}

/** Replaces the shell's title, description, canonical and Open Graph tags for one page. */
export function applyMeta(html: string, meta: PageMeta): string {
  if (meta.url === '')
    html = html
      .replace(/<link rel="canonical"[^>]*>[\r\n]*/, '')
      .replace(/<meta property="og:url"[^>]*>[\r\n]*/, '')
  const escaped = escapeAttribute(meta.description)
  const title = escapeAttribute(meta.title)
  const url = escapeAttribute(meta.url)
  return html
    .replace(/<title>[\s\S]*?<\/title>/, `<title>${escapeText(meta.title)}</title>`)
    .replace(attribute('meta', 'name', 'description', 'content'), `$1${escaped}$2`)
    .replace(attribute('link', 'rel', 'canonical', 'href'), `$1${url}$2`)
    .replace(attribute('meta', 'property', 'og:title', 'content'), `$1${title}$2`)
    .replace(attribute('meta', 'property', 'og:description', 'content'), `$1${escaped}$2`)
    .replace(attribute('meta', 'property', 'og:url', 'content'), `$1${url}$2`)
}

/** Sitemap over addresses relative to the site root, each written as a directory. */
export function sitemapXml(origin: string, paths: readonly string[]): string {
  const urls = paths.map((path) => `  <url><loc>${escapeText(pageUrl(origin, path, true))}</loc></url>`).join('\n')
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`
}

export function escapeText(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

export function escapeAttribute(value: string): string {
  return escapeText(value).replace(/"/g, '&quot;')
}
