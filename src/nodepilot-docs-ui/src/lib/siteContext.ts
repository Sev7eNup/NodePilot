/**
 * Where the docs are being served from.
 *
 * The same bundle is published twice: on GitHub Pages beside the project website and the
 * browser demo, and inside the product under `wwwroot/docs`, where neither exists. Only the
 * Pages copy carries a `np-site-root` meta tag, stamped by `scripts/assemble-site.mjs` — so
 * its absence is what keeps the product's docs free of links that would 404.
 */
import { docsBase } from './docsBase';

const META_NAME = 'np-site-root';

/**
 * The absolute path to the Pages root, or null when the docs stand alone.
 *
 * The tag states the way from the documentation root, so it is resolved against that and not
 * against the current address: a reader two levels in would otherwise get a link back into the
 * documentation tree instead of one to the website.
 */
export function siteRoot(): string | null {
  const content = document.querySelector<HTMLMetaElement>(`meta[name="${META_NAME}"]`)?.content;
  if (!content?.trim()) return null;
  return new URL(content.trim(), new URL(docsBase(), location.origin)).pathname;
}
