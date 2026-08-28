import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md part 19: workflow diff and version compare.
 *
 * WorkflowDiffModal opens from the designer toolbar, lists historical versions from
 * GET /api/workflows/{id}/versions, fetches a chosen version from /versions/{v}, computes the
 * diff against the current editor draft on the client, and offers a restore that posts to
 * /api/workflows/{id}/rollback/{v} when the caller can write.
 * Hermetic: page.route mocks only, EN locale under Playwright.
 */

const WF_ID = 'eeeeeeee-1111-2222-3333-444444444444';
const ME = MOCK_USER; // Admin, so canWrite is true and Restore is enabled

// Current draft (what the editor loads from definitionJson): nodes step-A + step-B.
const CURRENT_DEF = {
  nodes: [
    { id: 'step-A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
    { id: 'step-B', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'Bravo', activityType: 'delay', config: {} } },
  ],
  edges: [{ id: 'e-AB', source: 'step-A', target: 'step-B', type: 'labeled', data: {} }],
};

// Version 2 (historical base): nodes step-A + step-C. Against the current draft that makes
// step-B an addition and step-C a removal.
const V2_DEF = {
  nodes: [
    { id: 'step-A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
    { id: 'step-C', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'Charlie', activityType: 'sql', config: {} } },
  ],
  edges: [{ id: 'e-AC', source: 'step-A', target: 'step-C', type: 'labeled', data: {} }],
};

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Versioned',
    description: 'version-diff e2e fixture',
    isEnabled: true,
    // Locked by the current user so the editor opens editable and Restore is enabled.
    checkedOutByUserId: ME.id,
    checkedOutByUserName: ME.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify(CURRENT_DEF),
    version: 3,
    ...overrides,
  });
}

const VERSIONS = [
  { version: 3, isCurrent: true, createdAt: '2026-06-03T10:00:00.000Z', createdBy: 'e2e-admin', changeNote: 'current draft' },
  { version: 2, isCurrent: false, createdAt: '2026-06-02T10:00:00.000Z', createdBy: 'alice', changeNote: 'added Charlie' },
  { version: 1, isCurrent: false, createdAt: '2026-06-01T10:00:00.000Z', createdBy: 'bob', changeNote: 'initial' },
];

async function openDiffModal(page: import('@playwright/test').Page) {
  await seedExpertMode(page);
  await page.goto(`/workflows/${WF_ID}`);
  // The diff entry sits inside the Tools menu, so open that menu first.
  await page.getByTestId('tools-menu-trigger').click();
  // Toolbar diff row (role=menuitem); its title reads "Diff against a previous version" in EN.
  const diffBtn = page.getByRole('menuitem', { name: /diff against a previous version|diff gegen vorherige version/i });
  await expect(diffBtn).toBeVisible({ timeout: 20_000 });
  await diffBtn.click();
  await expect(page.getByRole('heading', { name: /workflow diff/i })).toBeVisible({ timeout: 10_000 });
}

test.describe('Workflow-Diff / Version-Compare (Teil 19)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME) }),
    );
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(VERSIONS) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions/2`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ definition: V2_DEF }) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions/1`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ definition: V2_DEF }) }),
    );
  });

  // ---------- 19.1 — version history list ----------
  test('19.1 — diff modal lists historical versions (current excluded) with metadata', async ({ page }) => {
    await openDiffModal(page);

    // The left timeline lists the non-current versions (v2, v1). v3 (isCurrent) is filtered out.
    await expect(page.getByText('Version 2', { exact: true })).toBeVisible();
    await expect(page.getByText('Version 1', { exact: true })).toBeVisible();
    await expect(page.getByText('Version 3', { exact: true })).toHaveCount(0);

    // Per-version metadata: author + change note are surfaced.
    await expect(page.getByText(/alice/)).toBeVisible();
    await expect(page.getByText('added Charlie')).toBeVisible();
  });

  // ---------- 19.2 — diff modal stats + per-node lists ----------
  test('19.2 — picking v2 renders added/removed stats and per-node lists', async ({ page }) => {
    await openDiffModal(page);

    // Scope every assertion to the diff modal panel: "Bravo" and "Charlie" also appear on the
    // React Flow canvas underneath, so unscoped text matches are ambiguous. The modal is the
    // fixed full-screen overlay that contains the "Workflow Diff" heading.
    const modal = page.locator('div.fixed.inset-0').filter({ hasText: 'Workflow Diff' });

    // Pick version 2 on the left; the diff is computed against the current draft.
    await modal.getByText('Version 2', { exact: true }).click();

    // Stats grid: base v2 (A+C) against the current draft (A+B) yields one addition and one
    // removal. Each stat card is a label plus a count, so assert the labels render.
    await expect(modal.getByText('Added', { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(modal.getByText('Removed', { exact: true })).toBeVisible();
    await expect(modal.getByText('Changed', { exact: true })).toBeVisible();

    // Per-node lists: "Nodes added" holds Bravo (step-B), "Nodes removed" holds Charlie (step-C).
    await expect(modal.getByText(/nodes added/i)).toBeVisible();
    await expect(modal.getByText('Bravo', { exact: true })).toBeVisible();
    await expect(modal.getByText(/nodes removed/i)).toBeVisible();
    await expect(modal.getByText('Charlie', { exact: true })).toBeVisible();

    // Modal is closeable via the Close (X) button.
    await page.getByRole('button', { name: /^close$/i }).click();
    await expect(page.getByRole('heading', { name: /workflow diff/i })).toHaveCount(0);
  });

  // ---------- 19.3 — rollback ----------
  test('19.3 — Restore fires POST /api/workflows/{id}/rollback/{v} after confirm', async ({ page }) => {
    let rollbackVersion: string | null = null;
    let rollbackBody: { reason?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}/rollback/2`, (route) => {
      rollbackVersion = '2';
      rollbackBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openDiffModal(page);
    await page.getByText('Version 2', { exact: true }).click();

    // The "Restore v2" button appears once a version is selected, enabled because canWrite.
    const restore = page.getByRole('button', { name: /restore v2/i });
    await expect(restore).toBeVisible({ timeout: 10_000 });

    await restore.click();
    // Confirm the ConfirmHost modal "Restore workflow to version 2?" via OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => rollbackVersion, { timeout: 10_000 }).toBe('2');
    expect(rollbackBody?.reason).toMatch(/v2/i);
  });
});
