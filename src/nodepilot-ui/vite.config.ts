import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    host: true,
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      // Backend health endpoints (/healthz/live, /healthz/ready) live outside /api.
      // Proxying them keeps the in-app backend-status indicator working in dev, where
      // the SPA is not served by the backend on the same origin.
      '/healthz': 'http://localhost:5000',
      '/hubs': {
        target: 'http://localhost:5000',
        ws: true,
      },
      // The documentation site is a second app (nodepilot-docs-ui, dev server on 5174). In
      // production the API serves it from wwwroot/docs at /docs; without this proxy the dev
      // server answers /docs/ with this SPA's index.html instead, and the header's help button
      // lands on the router's not-found page. No path rewrite: the docs dev server serves under
      // /docs/ itself, so its entry module and HMR client stay inside the proxied prefix.
      '/docs': 'http://localhost:5174',
    },
  },
})
