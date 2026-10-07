import { afterEach, describe, expect, it } from 'vitest'
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pagesRedirectTarget } from '../../scripts/pages-redirect.mjs'
import { buildPagesRedirects } from '../../scripts/build-pages-redirects.mjs'
import { ARTICLE_SLUGS, ROUTE_PATHS, SITE_ROUTE_SEGMENTS } from './router.ts'

const origin = 'https://www.nodepilot.run'
const source = 'https://sev7enup.github.io/NodePilot'

describe('old public links', () => {
  it.each([
    ['', '/'],
    ['/', '/'],
    ['/docs/de/cli/', '/docs/de/cli/'],
    ['/docs/en/deployment/production/?ref=reddit#install', '/docs/en/deployment/production/?ref=reddit#install'],
    ['/#/de/cli', '/docs/de/cli/'],
    ['/#/security/hardening', '/docs/en/security/hardening/'],
    ['/docs/#/de/cli', '/docs/de/cli/'],
    ['/docs/#/cli', '/docs/en/cli/'],
    ['/#/produkt', '/product/'],
    ['/#/blog/warum-nodepilot', '/blog/why-nodepilot/'],
    ['/produkt/', '/product/'],
    ['/demo/?lang=de&tour=file#/workflows/abc', '/demo/workflows/abc?lang=de&tour=file'],
    ['/demo/?lang=de&id=old#/executions?id=new', '/demo/executions?lang=de&id=new'],
    ['/demo/#/', '/demo/'],
    ['/demo/workflows/abc', '/demo/workflows/abc'],
    ['/unknown/path?ref=reddit#section', '/unknown/path?ref=reddit#section'],
    ['/media/nodepilot-product-tour.mp4', '/media/nodepilot-product-tour.mp4'],
  ])('%s goes to its matching destination', (old, expected) => {
    expect(pagesRedirectTarget(source + old)).toBe(origin + expected)
  })

  it.each(['/#//evil.example/x', '/demo/#//evil.example', '/demo/#/\\evil.example', '//evil.example/a', '/%2f%2fevil.example', '/?next=https://evil.example', '/#//%'])('never leaves the fixed origin: %s', path => {
    expect(new URL(pagesRedirectTarget(source + path)).origin).toBe(origin)
  })

  it('covers every current website route and article in old hash links', () => {
    for (const path of [...SITE_ROUTE_SEGMENTS, ...ARTICLE_SLUGS.map(slug => `blog/${slug}`)]) {
      expect(pagesRedirectTarget(`${source}/#/${path}`)).toBe(`${origin}/${path}/`)
    }
  })
})

let temporary
afterEach(() => { if (temporary) rmSync(temporary, { recursive: true, force: true }) })

it('builds redirects from routes and both corpora without touching the webspace artifact', () => {
  temporary = mkdtempSync(join(tmpdir(), 'np-redirects-'))
  for (const lang of ['de', 'en']) {
    mkdirSync(join(temporary, 'content', lang), { recursive: true })
    writeFileSync(join(temporary, 'content', lang, 'cli.md'), '# CLI')
  }
  mkdirSync(join(temporary, '_site'))
  writeFileSync(join(temporary, '_site', 'index.html'), 'full website')
  const output = buildPagesRedirects(temporary)
  for (const path of [...Object.values(ROUTE_PATHS), 'docs/de/cli', 'docs/en/cli', 'demo']) {
    const html = readFileSync(join(output, path, 'index.html'), 'utf8')
    expect(html).toContain(`href="${origin}/${path}${path ? '/' : ''}"`)
    expect(html).toContain('<noscript><meta http-equiv="refresh"')
    expect(html).toContain('src="/NodePilot/redirect.js"')
  }
  expect(readFileSync(join(output, '404.html'), 'utf8')).toContain('/NodePilot/redirect.js')
  expect(readFileSync(join(temporary, '_site', 'index.html'), 'utf8')).toBe('full website')
})
