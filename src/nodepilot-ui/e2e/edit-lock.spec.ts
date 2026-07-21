import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Edit-Lock-Lifecycle E2E. The 4-states-toggle (CLAUDE.md "Edit-Lifecycle") is a
 * common-regression area because the button labels + endpoint dispatch change with
 * `IsEnabled` × `CheckedOutByUserId` combinations.
 *
 * Maps to scenarios in [E2ETests.md](../../../E2ETests.md) Teil 27:
 *   - Test 27.1 — State A: Productive (no lock) → "Bearbeiten" + "Disable" visible.
 *   - Test 27.2 — Bearbeiten → State B: lock-by-me + IsEnabled=false.
 *   - Test 27.7 — State D: lock-by-other → Designer read-only + lock-banner.
 */

const WORKFLOW_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const ME_ID = '00000000-0000-0000-0000-000000000001';
const OTHER_USER_ID = '00000000-0000-0000-0000-0000000000aa';

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WORKFLOW_ID,
    name: 'WF-LockTest',
    description: 'edit-lock e2e fixture',
    isEnabled: true,
    checkedOutByUserId: null,
    checkedOutAt: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    ...overrides,
  });
}

test.describe('Edit-Lock-Lifecycle', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // /me with a stable userId so the SPA can compare against checkedOutByUserId.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        // authStore reads `me.id` for the lock-owner comparison — send `id`, not `userId`.
        body: JSON.stringify({ id: ME_ID, username: 'me', role: 'Operator' }),
      }),
    );
  });

  test('27.1 — State A: productive workflow shows Bearbeiten + Disable', async ({ page }) => {
    await page.route(`**/api/workflows/${WORKFLOW_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await page.goto(`/workflows/${WORKFLOW_ID}`);

    // Toolbar should show the lock-entry button + the disable toggle. Save is hidden.
    await expect(page.getByRole('button', { name: /bearbeiten|edit/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /deaktivieren|disable/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /zwischen-speichern|speichern|save/i })).toHaveCount(0);
  });

  test('27.2 — Bearbeiten transitions to State B (locked-by-me + disabled)', async ({ page }) => {
    let locked = false;
    await page.route(`**/api/workflows/${WORKFLOW_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: locked
          ? workflowJson({ isEnabled: false, checkedOutByUserId: ME_ID, checkedOutAt: new Date().toISOString() })
          : workflowJson(),
      }),
    );
    await page.route(`**/api/workflows/${WORKFLOW_ID}/lock`, (route) => {
      locked = true;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ok: true }),
      });
    });

    await page.goto(`/workflows/${WORKFLOW_ID}`);
    await page.getByRole('button', { name: /bearbeiten|edit/i }).first().click();

    // After lock, Save + Publish + Beenden should be the visible action set.
    await expect(page.getByRole('button', { name: /zwischen-speichern|speichern|save/i }).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button', { name: /publish|veröffentlichen/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /beenden|end editing|finish editing|cancel edit/i })).toBeVisible();
  });

  test('27.7 — State D: locked-by-other renders read-only banner', async ({ page }) => {
    await page.route(`**/api/workflows/${WORKFLOW_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({
          isEnabled: false,
          checkedOutByUserId: OTHER_USER_ID,
          checkedOutAt: new Date(Date.now() - 5 * 60_000).toISOString(),
        }),
      }),
    );

    await page.goto(`/workflows/${WORKFLOW_ID}`);

    // Some lock-owner indicator must be present. Allow either a banner or a toolbar
    // hint — the assertion targets the data, not a specific element shape.
    await expect(page.getByText(/gesperrt|locked?|read-only|bearbeitung läuft|checked\s*out/i).first()).toBeVisible({
      timeout: 10_000,
    });
    // Force-Unlock is admin-only; an Operator (this test's user) must NOT see it.
    await expect(page.getByRole('button', { name: /force[\s-]?unlock/i })).toHaveCount(0);
  });
});
