import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 62 — Designer Status-Banner (lines 3690-3705).
 *
 * Hermetic: page.route() mocks only. The banner is rendered by EditorStatusBanners.tsx, which
 * picks exactly one variant from (roleCanWrite × isLockedByMe × isLockedByOther × isEnabled):
 *
 *   62.1 — A disabled workflow that is NOT locked by anyone shows the yellow "Workflow is
 *          disabled" banner (productive vs disabled is chosen on workflow.isEnabled). The
 *          toolbar offers an "Edit" affordance (lock-by-me path → enable later).
 *
 *   62.2 — A workflow locked by ANOTHER user (checkedOutByUserId != MOCK_USER.id) shows the
 *          amber "Editing in progress — by <user>" read-only banner. As an Admin, the
 *          "Force unlock" button is present and goes through the in-app ConfirmHost modal.
 *
 * The SPA renders ENGLISH under Playwright. MOCK_USER.role = Admin (see fixtures/mockApi).
 */

const WF_ID = 'b6b6b6b6-6262-6262-6262-626262626262';
const OTHER_USER_ID = '00000000-0000-0000-0000-0000000000aa';

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Banner',
    description: '',
    isEnabled: true,
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    ...overrides,
  });
}

test.describe('Designer Status-Banner (Teil 62)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('62.1 — disabled workflow (unlocked) shows the "Workflow is disabled" banner + an Edit affordance', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({ isEnabled: false }), // unlocked + disabled → yellow disabled banner
      }),
    );

    await page.goto(`/workflows/${WF_ID}`);

    // EditorStatusBanners "disabledTitle" copy.
    await expect(page.getByText(/workflow is disabled/i).first()).toBeVisible({ timeout: 15_000 });
    // The disabled banner is NOT the productive one.
    await expect(page.getByText(/running productive/i)).toHaveCount(0);
    // Toolbar exposes the lock-entry "Edit" button (Admin → roleCanWrite, unlocked → can take lock).
    await expect(page.getByRole('button', { name: /bearbeiten|^edit$/i }).first()).toBeVisible();
  });

  test('62.2 — locked-by-other shows the read-only "editing in progress by X" banner with an admin Force-Unlock', async ({ page }) => {
    let forceUnlockHit = false;
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({
          isEnabled: false,
          checkedOutByUserId: OTHER_USER_ID,
          checkedOutByUserName: 'otto-operator',
          checkedOutAt: new Date(Date.now() - 5 * 60_000).toISOString(),
        }),
      }),
    );
    await page.route(`**/api/workflows/${WF_ID}/force-unlock`, (route) => {
      forceUnlockHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: true }) });
    });

    await page.goto(`/workflows/${WF_ID}`);

    // Amber lock-by-other banner: title "Editing in progress" + the owner's name.
    await expect(page.getByText(/editing in progress/i).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/otto-operator/).first()).toBeVisible();

    // Admin sees the Force-Unlock button; clicking it opens the in-app ConfirmHost
    // modal — confirming via OK hits the endpoint.
    const forceUnlock = page.getByRole('button', { name: /force[\s-]?unlock/i });
    await expect(forceUnlock).toBeVisible();
    await forceUnlock.click();
    await page.getByRole('button', { name: 'OK' }).click();
    await expect.poll(() => forceUnlockHit, { timeout: 10_000 }).toBe(true);
  });
});
