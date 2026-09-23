import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { tmpdir } from 'node:os'
import { afterAll, describe, expect, it } from 'vitest'
import { prerenderDocs } from '../../scripts/prerender-docs.mjs'
import { allPages } from '../data/nav'
import { LANGUAGES } from '../i18n/languages'

/**
 * The documentation is one bundle with one shell; the prerenderer is what turns it into one
 * file per address. Without it every address would serve the same title and description again,
 * which is the whole reason the addresses stopped living in the URL fragment.
 */
const ORIGIN = 'https://x.test'

// A stand-in for the built index.html: only the tags the prerenderer rewrites.
const SHELL = [
  '<!doctype html>',
  '<html lang="en">',
  '  <head>',
  '    <title>NodePilot Documentation</title>',
  '    <meta',
  '      name="description"',
  '      content="shell"',
  '    />',
  '    <meta property="og:title" content="NodePilot Documentation" />',
  '    <meta property="og:description" content="shell" />',
  '    <meta property="og:url" content="https://x.test/docs/" />',
  '    <link rel="canonical" href="https://x.test/docs/" />',
  '    <link rel="alternate" hreflang="en" href="https://x.test/docs/en/" />',
  '    <link rel="alternate" hreflang="de" href="https://x.test/docs/de/" />',
  '    <link rel="alternate" hreflang="x-default" href="https://x.test/docs/en/" />',
  '    <meta name="np-docs-base" content="./" />',
  '    <script src="./legacy-hash-redirect.js"></script>',
  '    <script type="module" src="./assets/index.js"></script>',
  '  </head>',
  '  <body><div id="root"></div></body>',
  '</html>',
].join('\n')

const out = mkdtempSync(join(tmpdir(), 'np-docs-prerender-'))
mkdirSync(out, { recursive: true })
writeFileSync(join(out, 'index.html'), SHELL)
prerenderDocs(out, ORIGIN)

afterAll(() => rmSync(out, { recursive: true, force: true }))

function read(file) {
  return readFileSync(join(out, file), 'utf8')
}

function htmlFiles() {
  const files = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry)
      if (statSync(full).isDirectory()) walk(full)
      else if (entry.endsWith('.html')) files.push(relative(out, full).split('\\').join('/'))
    }
  }
  walk(out)
  return files.sort()
}

describe('prerenderDocs', () => {
  it('writes one directory per page in every language, plus an entry page each', () => {
    const expected = [
      'index.html',
      ...LANGUAGES.flatMap((lang) => [`${lang}/index.html`, ...allPages.map((page) => `${lang}/${page.path}/index.html`)]),
    ].sort()
    expect(htmlFiles()).toEqual(expected)
  })

  it('gives every page its own title and description', () => {
    const cli = read('en/cli/index.html')
    expect(cli).toContain('<title>CLI (np) — NodePilot Docs</title>')
    expect(cli).not.toContain('content="shell"')
    expect(read('de/cli/index.html')).toContain('<title>CLI (np) — NodePilot Dokumentation</title>')
  })

  it('points each page at itself and at its translation', () => {
    const page = read('de/getting-started/quickstart/index.html')
    expect(page).toContain('<link rel="canonical" href="https://x.test/docs/de/getting-started/quickstart/"')
    expect(page).toContain('hreflang="de" href="https://x.test/docs/de/getting-started/quickstart/"')
    expect(page).toContain('hreflang="en" href="https://x.test/docs/en/getting-started/quickstart/"')
    expect(page).toContain('hreflang="x-default" href="https://x.test/docs/en/getting-started/quickstart/"')
    expect(page).toContain('<html lang="de"')
  })

  it('states how far up the documentation root is, so the same bundle works under any prefix', () => {
    expect(read('en/cli/index.html')).toContain('<meta name="np-docs-base" content="../../"')
    expect(read('de/getting-started/quickstart/index.html')).toContain('<meta name="np-docs-base" content="../../../"')
    expect(read('en/index.html')).toContain('<meta name="np-docs-base" content="../"')
  })

  it('reaches its assets from the depth it sits at', () => {
    const page = read('de/getting-started/quickstart/index.html')
    expect(page).toContain('src="../../../assets/index.js"')
    expect(page).toContain('src="../../../legacy-hash-redirect.js"')
    expect(page).not.toContain('src="./')
  })

  it('leaves the entry pages out of the index, because they only forward', () => {
    const entry = read('en/index.html')
    expect(entry).not.toContain('rel="canonical"')
    expect(entry).not.toContain('og:url')
  })

  it('lists every page of every language in the sitemap, and nothing else', () => {
    const xml = read('sitemap.xml')
    const locations = [...xml.matchAll(/<loc>([^<]*)<\/loc>/g)].map((match) => match[1])
    expect(locations).toHaveLength(allPages.length * LANGUAGES.length)
    expect(locations).toContain(`${ORIGIN}/docs/en/cli/`)
    expect(locations).toContain(`${ORIGIN}/docs/de/getting-started/quickstart/`)
    // The entry pages and the shell would compete with the pages they forward to.
    expect(locations).not.toContain(`${ORIGIN}/docs/en/`)
    expect(locations).not.toContain(`${ORIGIN}/docs/`)
  })
})
