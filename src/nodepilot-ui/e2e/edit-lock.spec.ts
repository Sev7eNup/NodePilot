import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Edit-lock lifecycle. Button labels and endpoint dispatch depend on the combination of
 * `IsEnabled` and `CheckedOutByUserId`, so this covers the state toggle described in CLAUDE.md
 * "Edit-Lifecycle" and [E2ETests.md](../../../docs/testing/E2ETests.md) section "Teil 27":
 * state A (no lock), state B (locked by the current user and disabled), and state D (locked by
 * another user, which leaves the designer read-only).
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

    // After locking, Save, Publish and the end-editing button are the visible action set.
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

    // Some lock-owner indicator must be present. Either a banner or a toolbar hint is accepted:
    // the assertion targets the text, not a specific element shape.
    await expect(page.getByText(/gesperrt|locked?|read-only|bearbeitung läuft|checked\s*out/i).first()).toBeVisible({
      timeout: 10_000,
    });
    // Force-Unlock is admin-only, so the Operator used by this test must not see it.
    await expect(page.getByRole('button', { name: /force[\s-]?unlock/i })).toHaveCount(0);
  });
});
