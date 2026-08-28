import { defineConfig, devices } from '@playwright/test';
// Fast iteration config: runs the hermetic e2e specs against an already-running Vite dev
// server (:5173) with no build step. The specs need no backend (page.route mocks plus the
// predicate catch-all in fixtures/mockApi.ts), so they behave the same here as under the
// preview-build config (playwright.config.ts) used by the nightly run.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: 0,
  reporter: [['list']],
  use: { baseURL: 'http://localhost:5173', trace: 'off', viewport: { width: 1440, height: 900 } },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
