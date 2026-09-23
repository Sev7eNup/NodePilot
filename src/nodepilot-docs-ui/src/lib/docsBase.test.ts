import { describe, expect, it } from 'vitest'
import { resolveDocsBase } from './docsBase'

/**
 * The same bundle is served under /docs/ and under /NodePilot/docs/, so the documentation root
 * cannot be baked in: every prerendered file states how far up it is, and this resolves it.
 */
describe('resolveDocsBase', () => {
  it('resolves the depth a prerendered page states', () => {
    expect(resolveDocsBase('../../', 'https://x.test/docs/de/cli/')).toBe('/docs/')
    expect(resolveDocsBase('../../../', 'https://x.test/docs/de/getting-started/quickstart/')).toBe('/docs/')
    expect(resolveDocsBase('./', 'https://x.test/docs/')).toBe('/docs/')
  })

  it('works the same below a project path, which is where GitHub Pages serves it', () => {
    expect(resolveDocsBase('../../', 'https://x.test/NodePilot/docs/en/cli/')).toBe('/NodePilot/docs/')
    expect(resolveDocsBase('./', 'https://x.test/NodePilot/docs/')).toBe('/NodePilot/docs/')
  })

  it('falls back to the directory of the document when the tag is missing or empty', () => {
    expect(resolveDocsBase(undefined, 'https://x.test/docs/')).toBe('/docs/')
    expect(resolveDocsBase('  ', 'https://x.test/docs/')).toBe('/docs/')
    expect(resolveDocsBase(null, 'https://x.test/docs/')).toBe('/docs/')
  })

  it('always ends in a slash, so a link can be appended to it', () => {
    expect(resolveDocsBase('../..', 'https://x.test/docs/en/cli/')).toBe('/docs/')
  })
})
