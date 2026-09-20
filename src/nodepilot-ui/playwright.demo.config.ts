import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for the browser demo.
 *
 * Separate from the main config because the demo is a different artifact served differently:
 * its own build output, and a sub-path rather than the origin root. The sub-path is the
 * point — `base: './'` leaves URLs inside JavaScript strings alone, so a root-absolute
 * literal passes at the root and breaks on the published site.
 *
 * To run locally:
 *   npx playwright test --config=playwright.demo.config.ts
 */
export default defineConfig({
  testDir: './e2e-demo',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: process.env.CI ? [['html', { open: 'never' }], ['github']] : 'list',
  use: {
    baseURL: 'http://127.0.0.1:4180/NodePilot/demo/',
    trace: 'on-first-retry',
    viewport: { width: 1440, height: 900 },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm run build:demo && node e2e/demo-server.mjs',
    url: 'http://127.0.0.1:4180/NodePilot/demo/',
    // Never reuse: reusing a server someone left running serves a stale `dist-demo`, and the
    // suite then reports green for a bundle that is not the one under test. The build takes
    // seconds; a false pass costs far more.
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
