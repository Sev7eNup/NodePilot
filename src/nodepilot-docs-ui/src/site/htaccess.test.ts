import { describe, expect, it } from 'vitest'
import { SOURCE_CANONICAL_HOST, rewriteHtaccessHost } from '../../scripts/htaccess.mjs'
import htaccess from './public/.htaccess?raw'
import { PAGES_ORIGIN } from '../../scripts/site-origin.mjs'

describe('htaccess canonical host', () => {
  it('names this project host in the committed file, so it works unbuilt', () => {
    expect(htaccess).toContain(SOURCE_CANONICAL_HOST)
  })

  it('points every canonical-host rule at the build target', () => {
    const rewritten = rewriteHtaccessHost(htaccess, 'https://www.example.com')

    expect(rewritten).not.toContain(SOURCE_CANONICAL_HOST)
    expect(rewritten).toContain('RewriteCond %{HTTP_HOST} !^www\\.example\\.com$ [NC]')
    expect(rewritten).toContain('https://www.example.com/$1')
  })

  it('names no canonical host for an origin under a path, which cannot carry one', () => {
    const rewritten = rewriteHtaccessHost(htaccess, PAGES_ORIGIN)

    expect(rewritten).not.toContain(SOURCE_CANONICAL_HOST)
    expect(rewritten).not.toContain('RewriteCond %{HTTP_HOST}')
    // The HTTPS upgrade still applies, on whichever host answered.
    expect(rewritten).toContain('RewriteRule ^(.*)$ https://%{HTTP_HOST}/$1 [R=301,L]')
  })

  it('keeps the rest of the file in both cases', () => {
    for (const origin of ['https://www.example.com', PAGES_ORIGIN]) {
      const rewritten = rewriteHtaccessHost(htaccess, origin)
      expect(rewritten, origin).toContain('Strict-Transport-Security')
      expect(rewritten, origin).toContain('ErrorDocument 404 /404.html')
      expect(rewritten, origin).toContain('RedirectPermanent /produkt/ /product/')
      expect(rewritten, origin).toContain('RewriteCond %{HTTPS} !=on')
    }
  })

  it('is a no-op when the build targets the same host at its root', () => {
    expect(rewriteHtaccessHost(htaccess, `https://${SOURCE_CANONICAL_HOST}`)).toBe(htaccess)
  })
})
