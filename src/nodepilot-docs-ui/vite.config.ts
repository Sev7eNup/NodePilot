import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
