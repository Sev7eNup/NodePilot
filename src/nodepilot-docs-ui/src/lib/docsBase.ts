/**
 * Where the documentation root is, as an absolute path.
 *
 * The same bundle is served at /docs/ (the product and the website) and at /NodePilot/docs/
 * (GitHub Pages), so the prefix cannot be baked in at build time. Each prerendered file states
 * its own depth in a meta tag; this resolves it against the address the document was loaded
 * from. Resolving it later would be wrong: the router changes the address without loading
 * another document, which is what a relative reference would then resolve against.
 */
const META_NAME = 'np-docs-base'

/** The pure half, so the rule is testable without a document. */
export function resolveDocsBase(meta: string | null | undefined, href: string): string {
  return new URL(meta?.trim() || './', href).pathname.replace(/\/*$/, '/')
}

let cached: string | undefined

export function docsBase(): string {
  cached ??= resolveDocsBase(document.querySelector<HTMLMetaElement>(`meta[name="${META_NAME}"]`)?.content, location.href)
  return cached
}
