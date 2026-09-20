/**
 * Where the docs are being served from.
 *
 * The same bundle is published twice: on GitHub Pages beside the project website and the
 * browser demo, and inside the product under `wwwroot/docs`, where neither exists. Only the
 * Pages copy carries a `np-site-root` meta tag, stamped by `scripts/assemble-site.mjs` — so
 * its absence is what keeps the product's docs free of links that would 404.
 */
const META_NAME = 'np-site-root';

/** The relative path to the Pages root, or null when the docs stand alone. */
export function siteRoot(): string | null {
  const content = document.querySelector<HTMLMetaElement>(`meta[name="${META_NAME}"]`)?.content;
  return content?.trim() ? content.trim() : null;
}
