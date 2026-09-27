import { mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { ARTICLE_SLUGS, ROUTE_PATHS, SOLUTION_SLUGS } from '../src/site/router.ts'
import { pagesRedirectTarget } from './pages-redirect.mjs'

const PACKAGE_ROOT = fileURLToPath(new URL('..', import.meta.url))

export function buildPagesRedirects(packageRoot = PACKAGE_ROOT) {
  const root = resolve(packageRoot)
  // A dedicated artifact: never replace the full webspace build or any source directory.
  const out = join(root, '_pages-redirects')
  rmSync(out, { recursive: true, force: true })
  mkdirSync(out, { recursive: true })
  const paths = new Set(['', 'demo', 'docs', 'produkt', 'erleben', 'blog/warum-nodepilot', ...Object.values(ROUTE_PATHS), ...SOLUTION_SLUGS])
  for (const slug of ARTICLE_SLUGS) paths.add(`blog/${slug}`)
  for (const path of [...paths]) {
    if (path === '' || [...Object.values(ROUTE_PATHS), ...SOLUTION_SLUGS].includes(path) || path.startsWith('blog/')) {
      if (!['impressum', 'datenschutz'].includes(path)) paths.add(`en${path ? '/' + path : ''}`)
    }
  }
  for (const language of ['en', 'de']) {
    paths.add(`docs/${language}`)
    const corpus = join(root, 'content', language)
    for (const file of readdirSync(corpus, { recursive: true })) {
      if (file.endsWith('.md')) paths.add(`docs/${language}/${file.slice(0, -3).replaceAll('\\', '/')}`)
    }
  }
  const html = (target) => `<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>NodePilot has moved</title>
<link rel="canonical" href="${target}">
<script src="/NodePilot/redirect.js"></script>
<noscript><meta http-equiv="refresh" content="0;url=${target}"></noscript>
</head><body><p>NodePilot has moved. <a href="${target}">Continue to NodePilot</a>.</p></body></html>
`
  for (const path of paths) {
    const target = pagesRedirectTarget(`https://sev7enup.github.io/NodePilot/${path}${path ? '/' : ''}`)
    const file = join(out, path, 'index.html')
    mkdirSync(dirname(file), { recursive: true })
    writeFileSync(file, html(target))
  }
  writeFileSync(join(out, '404.html'), html('https://www.nodepilot.run/'))
  writeFileSync(join(out, 'redirect.js'), `location.replace((${pagesRedirectTarget.toString()})(location.href));\n`)
  writeFileSync(join(out, '.nojekyll'), '')
  return out
}

if (import.meta.main) console.log(`Pages redirects: ${buildPagesRedirects()}`)
