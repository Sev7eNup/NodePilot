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
