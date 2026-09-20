import { defineConfig } from 'vite'

// Project website (src/site). It builds separately from the docs SPA because dist/ is copied
// as-is into the product's wwwroot/docs, so the website gets its own root, public dir and
// output folder. scripts/assemble-site.mjs combines both outputs for GitHub Pages.
//
// Vite resolves `root` against the working directory (npm runs scripts from this package) and
// every other relative path below against `root`.
export default defineConfig({
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
