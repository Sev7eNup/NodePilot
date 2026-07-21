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
      // Proxy them so the in-app backend-status indicator works in dev exactly as in
      // prod (where the backend serves the SPA same-origin).
      '/healthz': 'http://localhost:5000',
      '/hubs': {
        target: 'http://localhost:5000',
        ws: true,
      },
    },
  },
})
