/**
 * Where the documentation lives, relative to the running app.
 *
 * The server serves it at `/docs`, so that is the default. Hosts that place the app somewhere
 * else relative to the docs replace it — the browser demo sits next to them rather than above
 * them. Injected rather than read from a build flag so `src/` keeps no knowledge of them.
 */
const DEFAULT_DOCS_HREF = '/docs/';

let href = DEFAULT_DOCS_HREF;

export function docsHref(): string {
  return href;
}

/** Replaces the documentation location. Restores the default when called with no argument. */
export function setDocsHref(next?: string): void {
  href = next ?? DEFAULT_DOCS_HREF;
}
