// Writes one file per website route after the Vite build: its own title, description and
// canonical URL, plus 404.html, sitemap.xml and robots.txt.
//
// Lives here and not in vite.site.config.ts because that file is type-checked as part of
// tsconfig.node.json, which knows neither Node's built-ins nor the sources under src/. Vite
// bundles the config with esbuild, so the TypeScript imports below resolve at build time.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { de } from '../src/site/i18n/de.ts'
import {
  applyMeta,
  metaKey,
  originPrefix,
  pageUrl,
  revealPage,
  rewriteRelativeUrls,
  robots,
  routePages,
  setBaseMeta,
  sitemap,
} from '../src/site/prerender.ts'

/** German title and description of a route, from the same dictionary the page renders from. */
function textsFor(route) {
  const key = metaKey(route)
  if (key.startsWith('article.')) {
    const article = de.articles[key.slice('article.'.length)]
    return { title: `${article.title} — NodePilot Blog`, description: article.summary }
  }
  const title = de.titles[key]
  return {
    title: key === 'home' ? `NodePilot — ${title}` : `${title} — NodePilot`,
    description: de.meta.descriptions[key],
  }
}

export function prerenderSite(outDir, origin) {
  const root = outDir ?? resolve(fileURLToPath(new URL('..', import.meta.url)), 'dist-site')
  const shell = readFileSync(join(root, 'index.html'), 'utf8')
  const pages = routePages()

  for (const page of pages) {
    const { title, description } = textsFor(page.route)
    // Root-absolute, so a link keeps pointing at the same place after the address has changed
    // under History API navigation.
    const prefix = originPrefix(origin)
    const html = setBaseMeta(
      rewriteRelativeUrls(
        applyMeta(revealPage(shell, page.route), {
          title,
          description,
          // The not-found page gets no address of its own.
          url: page.listed ? pageUrl(origin, page.path, true) : '',
        }),
        prefix,
      ),
      prefix,
    )
    const target = join(root, page.file)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, html)
  }
  writeFileSync(join(root, 'sitemap.xml'), sitemap(origin, pages))
  writeFileSync(join(root, 'robots.txt'), robots(origin))
  return pages.length
}
