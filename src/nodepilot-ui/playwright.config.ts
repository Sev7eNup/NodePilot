import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for NodePilot UI E2E tests.
 *
 * Scenario catalogue: [E2ETests.md](../../docs/testing/E2ETests.md) is the source of truth
 * for what each E2E test covers, the happy-path tour, and the manual checklists run through
 * the Playwright MCP browser. The test files in this folder are the automated subset.
 *
 * The webServer block builds the SPA and serves it with `vite preview`, which is faster than
 * `vite dev` for CI and matches what is deployed. Every API request is intercepted per test
 * via `page.route()`, so no backend runs and backend cold start cannot cause flake.
 *
 * To run locally:
 *   1. `npx playwright install --with-deps chromium`  (one-time)
 *   2. `npm run test:e2e`
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? [['html', { open: 'never' }], ['github']] : 'html',
  use: {
    baseURL: 'http://localhost:4173',
    trace: 'on-first-retry',
    // The designer relies on DOMMatrix and ResizeObserver. Both are real in headless
    // Chromium, so the jsdom shims used by the unit-test suite are not needed here.
    viewport: { width: 1440, height: 900 },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command: 'npm run build && npm run preview -- --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
