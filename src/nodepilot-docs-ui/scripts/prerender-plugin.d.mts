import type { Plugin } from 'vite'

/** Writes one file per route into `outDir`, plus the sitemap and robots.txt. Returns the count. */
export function prerenderSite(outDir: string | undefined, origin: string): number

/** Dev server: serves every route address with the shell cut down to that route's section. */
export function siteDevRoutes(): Plugin
