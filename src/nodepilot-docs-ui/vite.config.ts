import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { prerenderDocs } from './scripts/prerender-docs.mjs'
import { PAGES_ORIGIN, siteOrigin } from './scripts/site-origin.mjs'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    {
      // The head's canonical and Open Graph URLs are absolute and written for Pages. A build for
      // another host has to retarget them, or every docs page points a crawler at a foreign origin.
      name: 'np-docs-origin',
      transformIndexHtml: (html: string) => html.replaceAll(PAGES_ORIGIN, siteOrigin()),
    },
    {
      // In dev the history fallback serves the root index.html at every depth, so the depth
      // written into the file cannot be relative there. The configured base is the whole truth.
      name: 'np-docs-base-dev',
      apply: 'serve',
      transformIndexHtml: (html: string) =>
        html.replace(/(<meta name="np-docs-base" content=")[^"]*/, '$1/docs/'),
    },
    {
      // One file per documentation address, so every page carries its own title, description
      // and canonical URL instead of the shell's. Last in the list: it reads the built
      // index.html, which the plugins above have already rewritten.
      name: 'np-docs-prerender',
      apply: 'build',
      closeBundle() {
        prerenderDocs(undefined, siteOrigin())
      },
    },
  ],
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
