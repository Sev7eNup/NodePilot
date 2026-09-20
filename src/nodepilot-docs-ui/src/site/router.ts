/**
 * Hash routes of the project website. Pure, so it can be tested without a DOM.
 */

/**
 * First hash segments the website owns. Any other `#/<segment>` is an old documentation link,
 * which public/legacy-docs-redirect.js forwards to docs/. That script repeats this list.
 */
export const SITE_ROUTE_SEGMENTS = ['erleben', 'produkt', 'blog', 'impressum', 'datenschutz'] as const

export const ARTICLE_SLUGS = ['warum-nodepilot', 'scorch-import'] as const

export type ArticleSlug = (typeof ARTICLE_SLUGS)[number]

export type SitePage = 'experience' | 'home' | 'product' | 'blog' | 'article' | 'impressum' | 'datenschutz' | 'notfound'

export type SiteRoute =
  | { page: 'article'; slug: ArticleSlug }
  | { page: Exclude<SitePage, 'article'>; slug?: undefined }

export function isArticleSlug(value: string): value is ArticleSlug {
  return (ARTICLE_SLUGS as readonly string[]).includes(value)
}

/**
 * Maps `location.hash` to a page. Returns null for a hash that is not a route, such as the skip
 * link's `#main-content`, so the caller keeps the current page. An empty hash is the home page,
 * which is where the back button lands on the start URL.
 */
export function resolveRoute(hash: string): SiteRoute | null {
  if (hash === '' || hash === '#') return { page: 'home' }
  if (!hash.startsWith('#/')) return null

  let path: string
  try {
    path = decodeURIComponent(hash.slice(1))
  } catch {
    return { page: 'notfound' }
  }
  path = path.replace(/\/+$/, '') || '/'

  switch (path) {
    case '/':
      return { page: 'home' }
    case '/erleben':
      return { page: 'experience' }
    case '/produkt':
      return { page: 'product' }
    case '/blog':
      return { page: 'blog' }
    case '/impressum':
      return { page: 'impressum' }
    case '/datenschutz':
      return { page: 'datenschutz' }
  }

  if (path.startsWith('/blog/')) {
    const slug = path.slice('/blog/'.length)
    if (isArticleSlug(slug)) return { page: 'article', slug }
  }
  return { page: 'notfound' }
}
