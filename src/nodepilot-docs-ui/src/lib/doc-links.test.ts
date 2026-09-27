import { describe, expect, it } from 'vitest'
import { resolveDocLink } from '../components/DocMarkdown'

describe('documentation cross-links', () => {
  it('keeps section anchors and parameters when converting source paths to page URLs', () => {
    expect(resolveDocLink('./installation#server', 'getting-started/quickstart', 'de')).toBe('/de/getting-started/installation/#server')
    expect(resolveDocLink('../security/hardening.md?ref=example#winrm', 'getting-started/quickstart', 'en')).toBe('/en/security/hardening/?ref=example#winrm')
  })
  it('leaves fragment, external and mail links to the browser', () => {
    for (const href of ['#example', 'https://example.test/page#anchor', '//example.test/page', 'mailto:info@example.test']) expect(resolveDocLink(href, 'cli', 'en')).toBeNull()
  })
})
