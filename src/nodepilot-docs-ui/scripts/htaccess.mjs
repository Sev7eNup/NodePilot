// Rewrites the canonical-host rules of the site's .htaccess after the Vite build.
//
// Lives here and not in vite.site.config.ts because that file is type-checked as part of
// tsconfig.node.json, which knows neither Node's built-ins nor the sources under src/.
import { readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { siteOrigin } from './site-origin.mjs'

/** The canonical host the committed `.htaccess` names, rewritten per build target. */
export const SOURCE_CANONICAL_HOST = 'www.nodepilot.run'

/** The bare-domain redirect, matched with either line ending. */
const BARE_DOMAIN_RULE = /^# The bare domain[\s\S]*?\[R=301,L\]\r?\n\r?\n/m

/**
 * Points the canonical-host rules at the build target. The committed file names this project's
 * host so it reads as a working file; a build for another origin must not 301 that origin's
 * visitors here, and a 301 outlives the mistake because browsers cache it.
 *
 * An origin under a path (a GitHub Pages project site) names no canonical host, so the
 * bare-domain rule is dropped and the HTTPS upgrade keeps whichever host answered.
 */
export function rewriteHtaccessHost(text, origin) {
  const url = new URL(origin)

  if (url.pathname !== '/') {
    return text
      .replace(BARE_DOMAIN_RULE, '')
      .replaceAll(`https://${SOURCE_CANONICAL_HOST}/$1`, 'https://%{HTTP_HOST}/$1')
  }

  const escape = (host) => host.replaceAll('.', String.raw`\.`)
  return text
    .replaceAll(escape(SOURCE_CANONICAL_HOST), escape(url.host))
    .replaceAll(SOURCE_CANONICAL_HOST, url.host)
}

/** Rewrites the `.htaccess` Vite copied into `outDir`. */
export function writeSiteHtaccess(outDir, origin = siteOrigin()) {
  const root = outDir ?? resolve(fileURLToPath(new URL('..', import.meta.url)), 'dist-site')
  const file = join(root, '.htaccess')
  writeFileSync(file, rewriteHtaccessHost(readFileSync(file, 'utf8'), origin))
}
