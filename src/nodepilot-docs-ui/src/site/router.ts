import { articles } from './blog.ts'
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
 * German pages retain their established addresses. English adds an en/ prefix. The legal
 * pages exist only in German and keep a single address.
 */
export const SITE_ROUTE_SEGMENTS = ['walkthrough', 'product', 'blog', 'impressum', 'datenschutz', 'powershell-automation', 'scorch-alternative', 'self-hosted-automation'] as const

export const ARTICLE_SLUGS = articles.map(article => article.slug)
export const SOLUTION_SLUGS = ['powershell-automation', 'scorch-alternative', 'self-hosted-automation'] as const
export type SolutionSlug = (typeof SOLUTION_SLUGS)[number]

export type ArticleSlug = (typeof ARTICLE_SLUGS)[number]

export type SitePage = 'experience' | 'home' | 'product' | 'blog' | 'article' | 'solution' | 'impressum' | 'datenschutz' | 'notfound'

export type SiteRoute =
  | { page: 'article'; slug: ArticleSlug }
  | { page: 'solution'; slug: SolutionSlug }
  | { page: Exclude<SitePage, 'article' | 'solution'>; slug?: undefined }

/** Path of every page that is not an article, relative to the site root. */
export const ROUTE_PATHS = {
  home: '',
  experience: 'walkthrough',
  product: 'product',
  blog: 'blog',
  impressum: 'impressum',
  datenschutz: 'datenschutz',
} as const satisfies Record<Exclude<SitePage, 'article' | 'solution' | 'notfound'>, string>

export function isArticleSlug(value: string): value is ArticleSlug {
  return (ARTICLE_SLUGS as readonly string[]).includes(value)
}

/** The path of a route, relative to the site root and without a leading slash. */
export function routePath(route: SiteRoute): string {
  if (route.page === 'article') return `${ROUTE_PATHS.blog}/${route.slug}`
  if (route.page === 'solution') return route.slug
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
  if (clean === 'en') clean = ''
  else if (clean.startsWith('en/')) clean = clean.slice(3)
  if (clean === '') return { page: 'home' }
  if ((SOLUTION_SLUGS as readonly string[]).includes(clean)) return { page: 'solution', slug: clean as SolutionSlug }

  for (const [page, value] of Object.entries(ROUTE_PATHS)) {
    if (value !== '' && value === clean) return { page: page as Exclude<SitePage, 'article' | 'solution' | 'notfound'> }
  }

  const prefix = `${ROUTE_PATHS.blog}/`
  if (clean.startsWith(prefix)) {
    const slug = clean.slice(prefix.length)
    if (isArticleSlug(slug)) return { page: 'article', slug }
  }
  return { page: 'notfound' }
}

/** Keep existing German addresses; English has a stable, crawlable URL of its own. */
export function localizedPath(route: SiteRoute, lang: 'de' | 'en'): string {
  const path = routePath(route)
  if (route.page === 'impressum' || route.page === 'datenschutz' || lang === 'de') return path
  return path ? `en/${path}` : 'en'
}

export function routeLanguage(path: string): 'de' | 'en' {
  return /^\/?en(?:\/|$)/.test(path) ? 'en' : 'de'
}
