import { describe, expect, it } from 'vitest'
import siteHtml from './index.html?raw'

// Every website source except the tests, which name the forbidden hosts themselves.
const sources = import.meta.glob<string>(['./**/*.{html,css,ts,js}', '!./**/*.test.ts'], {
  query: '?raw',
  import: 'default',
  eager: true,
})

const markup = siteHtml.replace(/<!--[\s\S]*?-->/g, '')
const head = markup.match(/<head[^>]*>([\s\S]*?)<\/head>/i)?.[1] ?? ''

describe('website head', () => {
  it('loads the legacy docs redirect as the first, classic, non-deferred script', () => {
    const first = head.match(/<script\b[^>]*>/i)?.[0]
    expect(first, 'the redirect script must stay the first script in <head>').toMatch(
      /\bsrc=["']\.\/legacy-docs-redirect\.js["']/,
    )
    // defer, async and type="module" run after parsing, so the page would render before forwarding.
    expect(first).not.toMatch(/\bdefer\b/i)
    expect(first).not.toMatch(/\basync\b/i)
    expect(first).not.toMatch(/type\s*=\s*["']module["']/i)
  })

  it('has no inline script', () => {
    // An opening <script> tag without a src attribute.
    const inline = [...markup.matchAll(/<script(?![^>]*\bsrc=)[^>]*>/gi)].map((match) => match[0])
    expect(inline).toEqual([])
  })
})

describe('website sources', () => {
  it('covers the main files', () => {
    for (const file of ['./index.html', './site.css', './main.ts', './i18n/de.ts', './i18n/en.ts']) {
      expect(Object.keys(sources)).toContain(file)
    }
  })

  it.each(['fonts.googleapis', 'fonts.gstatic', 'raw.githubusercontent'])('requests nothing from %s', (host) => {
    for (const [file, text] of Object.entries(sources)) {
      expect(text, file).not.toContain(host)
    }
  })
})
