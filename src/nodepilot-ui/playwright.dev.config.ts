import { defineConfig, devices } from '@playwright/test';
// Fast iteration config: runs the hermetic e2e specs against the already-running Vite dev
// server (:5173), no build. The specs are backend-independent (page.route mocks + the
// predicate catch-all in fixtures/mockApi.ts), so they behave identically here and under the
// real preview-build config (playwright.config.ts) used by the nightly. THROWAWAY — not committed.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: 0,
  reporter: [['list']],
  use: { baseURL: 'http://localhost:5173', trace: 'off', viewport: { width: 1440, height: 900 } },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
