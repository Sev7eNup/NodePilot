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
    },
  },
})
