import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // Relative, because the bundle is served both from wwwroot/docs at /docs and from GitHub Pages
  // under a project path. The dev server needs an absolute /docs/ instead, so the app dev server
  // can proxy /docs straight through — that is the `--base` in this package's dev script, which
  // Vite honours on the command line but not from this file.
  base: './',
  server: {
    port: 5174,
  },
})
