import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

test.describe('SPA smoke', () => {
  test('unauthenticated visit redirects to /login', async ({ page }) => {
    // Override the default authenticated mock with a 401 to simulate a fresh browser without
    // the np_auth cookie. The router must then redirect to /login.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 401, contentType: 'application/json', body: '{"error":"unauthenticated"}' }),
    );

    await page.goto('/');

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('heading', { name: /NodePilot/i }).first()).toBeVisible();
  });

  test('authenticated visit lands on the dashboard', async ({ page }) => {
    // installDefaultMocks() returns a successful /api/auth/me, so the router should
    // pick the dashboard as the default landing page.
    await installDefaultMocks(page);

    await page.goto('/');

    // The dashboard shows the username badge once auth resolves. Asserting on it avoids
    // depending on a label that may change between releases.
    await expect(page.getByText(/e2e-admin/i)).toBeVisible({ timeout: 15_000 });
  });
});
