import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { PAGES_ORIGIN, siteOrigin } from './scripts/site-origin.mjs'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // The content carries one absolute link (the demo lives beside the docs, not under them).
  // `lib/content.ts` rewrites it when the build targets another host.
  define: {
    __NP_PAGES_ORIGIN__: JSON.stringify(PAGES_ORIGIN),
    __NP_SITE_ORIGIN__: JSON.stringify(siteOrigin()),
  },
  // Relative, because the docs build is served under /docs both on the GitHub Pages site
  // (/NodePilot/docs/) and in the product (wwwroot/docs at /docs). The dev server needs an
  // absolute /docs/ instead, so the app dev server can proxy /docs straight through — that is the
  // `--base` in this package's dev script, which Vite honours on the command line but not from
  // this file.
  base: './',
  server: {
    port: 5174,
  },
})
