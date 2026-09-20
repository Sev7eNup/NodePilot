import { existsSync, readFileSync, renameSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const OUT_DIR = 'dist-demo'

// The version the demo reports in its top bar. Read from package.json rather than written by
// hand: it is kept in lockstep with Directory.Build.props, and a hardcoded copy drifts silently.
const VERSION = JSON.parse(readFileSync('package.json', 'utf8')).version

// The entry is `demo.html` so the product config cannot pick it up by the default name, but
// a static host has to serve it as the directory index. Renamed on disk after the write,
// which does not depend on how the bundler keys HTML assets internally.
function emitAsIndexHtml(outDir: string): Plugin {
  return {
    name: 'nodepilot-demo-index',
    enforce: 'post',
    closeBundle() {
      const from = resolve(outDir, 'demo.html')
      if (!existsSync(from)) return
      renameSync(from, resolve(outDir, 'index.html'))
    },
  }
}

// Browser demo: the same SPA, built against an in-memory backend and published as a static
// site. It is a separate config so the product build never names `demo.html` as an input —
// that is what keeps the demo out of `dist/`, rather than relying on tree-shaking.
//
// The docs package uses the same two-builds-one-package pattern (vite.config.ts plus
// vite.site.config.ts); this is the third build in the repository, not a new idea.
export default defineConfig({
  plugins: [react(), tailwindcss(), emitAsIndexHtml(OUT_DIR)],
  // Relative asset URLs plus hash routing (see App.tsx), so the output works under any
  // sub-path without a build-time base and without a 404.html fallback.
  base: './',
  define: {
    __NP_DEMO__: 'true',
    __NP_DEMO_VERSION__: JSON.stringify(VERSION),
  },
  build: {
    outDir: OUT_DIR,
    emptyOutDir: true,
    rollupOptions: {
      input: 'demo.html',
    },
  },
  server: {
    port: 5176,
    strictPort: true,
    fs: {
      // The seed graphs are the workflow JSON the repository already ships, two levels above
      // this package.
      allow: ['.', '../../samples', '../../scripts'],
    },
  },
  preview: {
    port: 5176,
    strictPort: true,
  },
})
