import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for NodePilot UI E2E tests.
 *
 * Scenario catalogue: see [E2ETests.md](../../E2ETests.md) at the repo root —
 * that document is the source of truth for what each E2E test covers, the
 * happy-path tour, and the manual checklists that are executed via the
 * Playwright MCP browser. Test files in this folder are the automated subset
 * of those scenarios.
 *
 * The webServer block builds the SPA and serves it via `vite preview` — this is
 * faster than `vite dev` for CI and matches what users actually deploy. All API
 * requests are intercepted per-test via `page.route()`; we never spin up a real
 * backend in E2E tests, so flake from backend cold-start / migrations is impossible.
 *
 * To run locally:
 *   1. `npx playwright install --with-deps chromium`  (one-time, ~200 MB)
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
    // Designer relies on DOMMatrix + ResizeObserver — both are real in headless
    // Chromium so we don't need the jsdom shims that the unit-test suite carries.
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
