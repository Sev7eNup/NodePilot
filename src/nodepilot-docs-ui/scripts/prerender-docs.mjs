// Writes one file per documentation address after the Vite build: its own title, description,
// canonical URL and hreflang pair, plus the sitemap.
//
// Lives here and not in vite.config.ts because that file is type-checked as part of
// tsconfig.node.json, which knows neither Node's built-ins nor the sources under src/. Vite
// bundles the config with esbuild, so the TypeScript import below resolves at build time.
//
// The address set comes from the markdown corpus rather than src/data/nav.ts: that module pulls
// in the icon library, which has no business in a Node-side build step. content.test.ts pins
// that the corpus and the navigation describe the same pages.
import { mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router'
import DocMarkdown from '../src/components/DocMarkdown.tsx'
import { SOURCE_ORIGIN } from './site-origin.mjs'
import { summarize } from '../src/lib/markdown-summary.ts'
import {
  applyMeta,
  escapeText,
  pageUrl,
  rewriteRelativeUrls,
  setAlternates,
  setHtmlLang,
  setMeta,
  sitemapXml,
} from '../src/lib/prerender-html.ts'

const PACKAGE_ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)))
const LANGUAGES = ['en', 'de']
const DEFAULT_LANG = 'en'
/** Where the documentation sits below the site root; the language segment follows it. */
const DOCS_PREFIX = 'docs'

/** Page paths of a language corpus, as `getting-started/introduction`. */
function corpusPaths(contentDir, lang) {
  const root = join(contentDir, lang)
  const paths = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry)
      if (statSync(full).isDirectory()) walk(full)
      else if (entry.endsWith('.md')) paths.push(full.slice(root.length + 1, -3).split('\\').join('/'))
    }
  }
  walk(root)
  return paths.sort()
}

/**
 * One prerendered file. `listed` is false for the entry pages, which only forward to a real
 * page: indexing them would compete with the page they forward to.
 */
function docsPages(contentDir) {
  const pages = []
  for (const lang of LANGUAGES) {
    pages.push({ lang, path: '', file: join(lang, 'index.html'), listed: false })
    for (const path of corpusPaths(contentDir, lang)) {
      pages.push({ lang, path, file: join(lang, ...path.split('/'), 'index.html'), listed: true })
    }
  }
  return pages
}

/** Address of a documentation page relative to the site root. */
function address(lang, path) {
  return path === '' ? `${DOCS_PREFIX}/${lang}` : `${DOCS_PREFIX}/${lang}/${path}`
}

export function prerenderDocs(outDir, origin) {
  const root = outDir ?? join(PACKAGE_ROOT, 'dist')
  const contentDir = join(PACKAGE_ROOT, 'content')
  const shell = readFileSync(join(root, 'index.html'), 'utf8')
  const locales = Object.fromEntries(
    LANGUAGES.map((lang) => [lang, JSON.parse(readFileSync(join(PACKAGE_ROOT, 'src/i18n/locales', `${lang}.json`), 'utf8'))]),
  )
  const translated = Object.fromEntries(LANGUAGES.map((lang) => [lang, new Set(corpusPaths(contentDir, lang))]))
  const pages = docsPages(contentDir)

  for (const page of pages) {
    const locale = locales[page.lang]
    const navTitle = locale.nav.pages[page.path]
    if (page.listed && !navTitle) throw new Error(`No navigation title for ${page.lang}/${page.path}.`)
    const markdown = page.listed ? readFileSync(join(contentDir, page.lang, `${page.path}.md`), 'utf8').replaceAll(SOURCE_ORIGIN, origin) : ''
    // One level for the language segment, one per path segment; the shell is written for the
    // documentation root. Consumed while the document parses and once more at boot, never after
    // a navigation, so it cannot go stale the way a relative href would.
    const prefix = '../'.repeat(1 + (page.path === '' ? 0 : page.path.split('/').length))
    // Only the languages that carry this page, so the pair never promises a translation that
    // does not exist.
    const alternates = {}
    for (const lang of LANGUAGES) {
      if (translated[lang].has(page.path)) alternates[lang] = pageUrl(origin, address(lang, page.path), true)
    }
    if (alternates[DEFAULT_LANG]) alternates['x-default'] = alternates[DEFAULT_LANG]

    let html = applyMeta(shell, {
      title: page.listed ? `${navTitle}${locale.meta.titleSuffix}` : locale.meta.title,
      description: markdown ? summarize(markdown) : locale.ui.tagline,
      // An entry page only forwards to the first chapter; indexing it would compete with the
      // page it forwards to.
      url: page.listed ? pageUrl(origin, address(page.lang, page.path), true) : '',
    })
    html = setAlternates(html, page.listed ? alternates : {})
    html = setHtmlLang(html, page.lang)
    html = setMeta(html, 'np-docs-base', prefix)
    html = rewriteRelativeUrls(html, prefix)

    // The actual chapter is readable before React loads, using the exact same Markdown
    // renderer as DocPage (tables, code highlighting, heading anchors and cross-links).
    if (page.listed) {
      let content = renderToStaticMarkup(createElement(MemoryRouter, null,
        createElement(DocMarkdown, { markdown, lang: page.lang, path: page.path })))
      content = content.replace(/href="\/(en|de)\//g, `href="${prefix}$1/`)
      const home = `${prefix}${page.lang}/getting-started/introduction/`
      const navigation = corpusPaths(contentDir, page.lang).map(path =>
        `<li><a href="${prefix}${page.lang}/${path}/">${escapeText(locale.nav.pages[path])}</a></li>`).join('')
      const body = `<div class="np-shell min-h-screen text-on-surface"><main class="mx-auto max-w-3xl px-6 py-8"><nav aria-label="Breadcrumb"><a href="${home}">NodePilot ${page.lang === 'de' ? 'Dokumentation' : 'Documentation'}</a></nav><article class="np-prose">${content}</article><details><summary>${page.lang === 'de' ? 'Alle Kapitel' : 'All chapters'}</summary><ul>${navigation}</ul></details></main></div>`
      html = html.replace('<div id="root"></div>', `<div id="root">${body}</div>`)
      const url = pageUrl(origin, address(page.lang, page.path), true)
      const schema = JSON.stringify({ '@context': 'https://schema.org', '@type': 'TechArticle', headline: navTitle, description: summarize(markdown), inLanguage: page.lang, url, mainEntityOfPage: url }).replace(/</g, '\\u003c')
      html = html.replace('</head>', `<script type="application/ld+json" data-docs-schema>${schema}</script></head>`)
    } else {
      html = html.replace('</head>', '<meta name="robots" content="noindex, follow"></head>')
      html = html.replace('<div id="root"></div>', `<div id="root"><a href="${prefix}${page.lang}/getting-started/introduction/">NodePilot ${page.lang === 'de' ? 'Dokumentation' : 'Documentation'}</a></div>`)
    }

    const target = join(root, page.file)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, html)
  }

  writeFileSync(
    join(root, 'sitemap.xml'),
    sitemapXml(
      origin,
      pages.filter((page) => page.listed).map((page) => address(page.lang, page.path)),
    ),
  )
  // The documentation entry is a language chooser without JavaScript, and the app redirects
  // it when enhanced. Do not offer it as a duplicate of the introduction chapter.
  let entry = applyMeta(shell, { title: 'NodePilot Documentation', description: 'NodePilot documentation in English and German.', url: '' })
  entry = setAlternates(entry, {})
  entry = entry.replace('</head>', '<meta name="robots" content="noindex, follow"></head>')
  entry = entry.replace('<div id="root"></div>', '<div id="root"><main><h1>NodePilot Documentation</h1><a href="en/getting-started/introduction/" lang="en">English</a> · <a href="de/getting-started/introduction/" lang="de">Deutsch</a></main></div>')
  writeFileSync(join(root, 'index.html'), entry)
  return pages.length
}
