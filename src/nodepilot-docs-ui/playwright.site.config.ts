import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e-site',
  fullyParallel: true,
  workers: 2,
  use: { baseURL: 'http://127.0.0.1:5176', browserName: 'chromium', locale: 'de-DE', viewport: { width: 1440, height: 1080 }, trace: 'retain-on-failure' },
  webServer: {
    command: 'npm run build:site && npm exec vite -- preview --config vite.site.config.ts --port 5176 --host 127.0.0.1',
    // The prerendered pages link against the path of this origin. Unset, the build would write
    // the Pages sub-path, which the preview server does not serve.
    env: { NP_SITE_ORIGIN: 'http://127.0.0.1:5176' },
    url: 'http://127.0.0.1:5176',
    reuseExistingServer: false,
    timeout: 60000,
  },
})
