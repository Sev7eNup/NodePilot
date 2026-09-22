import { readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import { prerenderSite } from './scripts/prerender-plugin.mjs'
import { PAGES_ORIGIN, rewriteHtaccessHost, siteOrigin } from './scripts/site-origin.mjs'

// Project website (src/site). It builds separately from the docs SPA because dist/ is copied
// as-is into the product's wwwroot/docs, so the website gets its own root, public dir and
// output folder. scripts/assemble-site.mjs combines both outputs for GitHub Pages.
//
// Vite resolves `root` against the working directory (npm runs scripts from this package) and
// every other relative path below against `root`.
export default defineConfig({
  plugins: [
    {
      // canonical, og:url and og:image have to be absolute, so they name an origin the source
      // cannot leave relative. Rewriting them here keeps index.html valid on its own.
      name: 'np-site-origin',
      transformIndexHtml: (html: string) => html.replaceAll(PAGES_ORIGIN, siteOrigin()),
    },
    {
      // One file per route, so every address carries its own title, description and canonical
      // URL instead of the home page's. Also writes the 404 page both hosts serve for an
      // unknown address, plus robots.txt and the sitemap.
      name: 'np-site-prerender',
      closeBundle() {
        prerenderSite(undefined, siteOrigin())
      },
    },
    {
      // The .htaccess ships as a static file, so its canonical-host rules would otherwise 301
      // every build to this project's own host. Rewritten after the public dir is copied.
      name: 'np-site-htaccess-host',
      closeBundle() {
        const file = resolve(__dirname, 'dist-site/.htaccess')
        writeFileSync(file, rewriteHtaccessHost(readFileSync(file, 'utf8'), siteOrigin()))
      },
    },
  ],
  root: 'src/site',
  base: './',
  // No SPA fallback: in dev, docs/ does not exist, and a fallback to this page would make the
  // legacy docs redirect loop.
  appType: 'mpa',
  publicDir: 'public',
  // A cache of its own: the docs dev server keeps the default one, and Vite deletes a deps cache
  // built for a different config when it starts.
  cacheDir: '../../node_modules/.vite-site',
  build: {
    outDir: '../../dist-site',
    // Vite only empties an outDir inside root unless told to.
    emptyOutDir: true,
  },
  server: {
    port: 5175,
    strictPort: true,
    fs: {
      // The package (shared i18n module, logo, fonts) and the screenshots in the repo's docs/images.
      allow: ['../..', '../../../../docs/images'],
    },
  },
  preview: {
    port: 5175,
    strictPort: true,
  },
})
