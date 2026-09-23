import { describe, expect, it } from 'vitest'
import indexHtml from '../../index.html?raw'
import { PAGES_ORIGIN } from '../../scripts/site-origin.mjs'
import themeInit from '../../public/theme-init.js?raw'

/**
 * Guards the one constraint the API adds to this bundle.
 *
 * Besides GitHub Pages, the docs site is served by NodePilot itself at /docs, under a
 * `script-src 'self'` CSP with no nonce. An inline <script> is blocked there — the theme would
 * not resolve before first paint and dark-mode readers would get a white flash. Pages has no
 * CSP at all and the API disables its security headers in Development, so a regression is
 * invisible in both places a developer normally looks. Hence a source-level check.
 *
 * Both files are pulled in as raw text, so a missing theme-init.js fails the build outright
 * rather than leaving index.html pointing at nothing.
 */
describe('index.html script policy', () => {
  it('has no inline script', () => {
    // Matches an opening <script> tag that carries no src attribute.
    const inline = [...indexHtml.matchAll(/<script(?![^>]*\bsrc=)[^>]*>/gi)].map((m) => m[0])
    expect(
      inline,
      "inline script is blocked by the API's `script-src 'self'` CSP at /docs. Put the code in " +
        'public/ and reference it with a classic, non-deferred <script src>.',
    ).toEqual([])
  })

  it('forwards the addresses from before the documentation had real ones', () => {
    // First script and classic, so an old #/de/cli link is replaced before anything is painted.
    const first = indexHtml.match(/<script\b[^>]*>/i)?.[0]
    expect(first, 'the redirect must stay the first script').toMatch(/\bsrc=["']\/legacy-hash-redirect\.js["']/)
    expect(first).not.toMatch(/\b(defer|async)\b/i)
    expect(first).not.toMatch(/type\s*=\s*["']module["']/i)
  })

  it('states where the documentation root is, for the router and the redirect', () => {
    // The prerenderer overwrites this per page; the value here is the root's own.
    expect(indexHtml).toMatch(/<meta name="np-docs-base" content="\.\/"/)
  })

  it('names both languages, so a shared link reaches the right one', () => {
    for (const code of ['en', 'de', 'x-default'])
      expect(indexHtml).toContain(`<link rel="alternate" hreflang="${code}"`)
  })

  it('loads the theme resolver as a classic, non-deferred script', () => {
    const tag = indexHtml.match(/<script[^>]*\btheme-init\.js[^>]*>/i)?.[0]
    expect(tag, 'the theme resolver must stay in index.html').toBeTruthy()
    // defer, async and type="module" all postpone execution until after parsing, which brings
    // the white flash back — the whole reason this script exists.
    expect(tag).not.toMatch(/\bdefer\b/i)
    expect(tag).not.toMatch(/\basync\b/i)
    expect(tag).not.toMatch(/type\s*=\s*["']module["']/i)
  })

  it('ships a theme resolver that sets the class before paint', () => {
    expect(themeInit).toContain('document.documentElement.classList')
  })
})

describe('index.html absolute URLs', () => {
  // vite.config.ts retargets this one origin when the build is for another host. An absolute URL
  // written any other way would keep pointing at Pages, which is a foreign origin there.
  it('writes every absolute URL against the Pages origin', () => {
    const urls = [...indexHtml.matchAll(/(?:href|content)="(https?:\/\/[^"]*)"/g)].map((m) => m[1])
    expect(urls.length).toBeGreaterThan(0)
    for (const url of urls) expect(url.startsWith(PAGES_ORIGIN), url).toBe(true)
  })
})
