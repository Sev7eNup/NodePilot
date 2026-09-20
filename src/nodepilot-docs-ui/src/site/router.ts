/**
 * Path routes of the project website. Pure, so it can be tested without a DOM.
 *
 * Every route is a real address (`/product`), prerendered as its own file at build time. The
 * site can still live in a subdirectory, so paths here are always relative to the site root;
 * main.ts strips that root before asking, and prefixes it again when it builds a link.
 */

/**
 * First path segments the website owns. Any other `#/<segment>` is an old documentation link,
 * which public/legacy-docs-redirect.js forwards to docs/. That script repeats this list, plus
 * the German segments these replaced.
 *
 * The segments are English because one address serves both languages; the two legal pages keep
 * their German names, because they exist only in German.
 */
export const SITE_ROUTE_SEGMENTS = ['walkthrough', 'product', 'blog', 'impressum', 'datenschutz'] as const

export const ARTICLE_SLUGS = ['why-nodepilot', 'scorch-import'] as const

export type ArticleSlug = (typeof ARTICLE_SLUGS)[number]

export type SitePage = 'experience' | 'home' | 'product' | 'blog' | 'article' | 'impressum' | 'datenschutz' | 'notfound'

export type SiteRoute =
  | { page: 'article'; slug: ArticleSlug }
  | { page: Exclude<SitePage, 'article'>; slug?: undefined }

/** Path of every page that is not an article, relative to the site root. */
export const ROUTE_PATHS = {
  home: '',
  experience: 'walkthrough',
  product: 'product',
  blog: 'blog',
  impressum: 'impressum',
  datenschutz: 'datenschutz',
} as const satisfies Record<Exclude<SitePage, 'article' | 'notfound'>, string>

export function isArticleSlug(value: string): value is ArticleSlug {
  return (ARTICLE_SLUGS as readonly string[]).includes(value)
}

/** The path of a route, relative to the site root and without a leading slash. */
export function routePath(route: SiteRoute): string {
  if (route.page === 'article') return `${ROUTE_PATHS.blog}/${route.slug}`
  if (route.page === 'notfound') return '404'
  return ROUTE_PATHS[route.page]
}

/**
 * Maps a path relative to the site root to a page. Leading and trailing slashes do not matter,
 * so both `product` and `/product/` resolve. An unknown path is the not-found page.
 */
export function resolveRoute(path: string): SiteRoute {
  let clean: string
  try {
    clean = decodeURIComponent(path)
  } catch {
    return { page: 'notfound' }
  }
  clean = clean.replace(/^\/+/, '').replace(/\/+$/, '')
  if (clean === '') return { page: 'home' }

  for (const [page, value] of Object.entries(ROUTE_PATHS)) {
    if (value !== '' && value === clean) return { page: page as Exclude<SitePage, 'article' | 'notfound'> }
  }

  const prefix = `${ROUTE_PATHS.blog}/`
  if (clean.startsWith(prefix)) {
    const slug = clean.slice(prefix.length)
    if (isArticleSlug(slug)) return { page: 'article', slug }
  }
  return { page: 'notfound' }
}
