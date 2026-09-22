// The public origin the site is served from. Both builds import this so the value exists once.
//
// The website's canonical and Open Graph tags and the docs' link to the demo are absolute URLs:
// relative ones cannot be used there. They are written for GitHub Pages, and a build for another
// host rewrites them through NP_SITE_ORIGIN. Unset, the rewrite is a no-op.

/** Where the site is published from this repository. Also the literal used in the sources. */
export const PAGES_ORIGIN = 'https://sev7enup.github.io/NodePilot'

/** The origin this build targets, without a trailing slash. */
export function siteOrigin() {
  const configured = process.env.NP_SITE_ORIGIN?.trim()
  return configured ? configured.replace(/\/+$/, '') : PAGES_ORIGIN
}

/** The canonical host written into the committed `.htaccess`, rewritten per build target. */
export const SOURCE_CANONICAL_HOST = 'www.nodepilot.run'

/**
 * Points the `.htaccess` canonical-host rules at the build target. The committed file names
 * this project's host so it reads as a working file; a build for another origin must not 301
 * that origin's visitors here, and a 301 outlives the mistake because browsers cache it.
 *
 * An origin that is not a host root (a GitHub Pages project site lives under a path) names no
 * canonical host, so the bare-domain rule is dropped and the HTTPS upgrade keeps whichever host
 * answered.
 */
export function rewriteHtaccessHost(text, origin = siteOrigin()) {
  const url = new URL(origin)

  if (url.pathname !== '/') {
    return text
      .replace(/^# The bare domain[\s\S]*?\[R=301,L\]\r?\n\r?\n/m, '')
      .replaceAll(`https://${SOURCE_CANONICAL_HOST}/$1`, 'https://%{HTTP_HOST}/$1')
  }

  const escape = (host) => host.replaceAll('.', String.raw`\.`)
  return text
    .replaceAll(escape(SOURCE_CANONICAL_HOST), escape(url.host))
    .replaceAll(SOURCE_CANONICAL_HOST, url.host)
}
