import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 19 — Workflow-Diff / Version-Compare.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * EN locale under Playwright.
 *
 * The version diff lives in the workflow designer (WorkflowDiffModal, opened from the editor
 * toolbar's GitCompare button — title "Diff against a previous version"). It:
 *   - lists historical versions from GET /api/workflows/{id}/versions (filtering out isCurrent),
 *   - fetches a chosen version's definition from GET /api/workflows/{id}/versions/{v}
 *     (shape { definition: { nodes, edges } }),
 *   - computes the diff CLIENT-SIDE between that base and the current editor draft
 *     (the workflow's definitionJson), rendering Added (green) / Removed (red) / Changed (amber)
 *     stats + per-node lists,
 *   - and offers "Restore vN" → POST /api/workflows/{id}/rollback/{v} when the caller canWrite.
 *
 * Covers:
 *   - 19.1 — version history list (chronological rows with version + timestamp + author).
 *   - 19.2 — diff modal: added/removed/changed stats + per-node lists, modal closeable.
 *   - 19.3 — rollback fires POST /rollback/{v} after the confirm dialog.
 */

const WF_ID = 'eeeeeeee-1111-2222-3333-444444444444';
const ME = MOCK_USER; // Admin → canWrite (Restore enabled)

// Current draft (what the editor loads from definitionJson): nodes step-A + step-B.
const CURRENT_DEF = {
  nodes: [
    { id: 'step-A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
    { id: 'step-B', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'Bravo', activityType: 'delay', config: {} } },
  ],
  edges: [{ id: 'e-AB', source: 'step-A', target: 'step-B', type: 'labeled', data: {} }],
};

// Version 2 (historical base): nodes step-A + step-C. So vs. current:
//   step-B is ADDED (in current, not in v2), step-C is REMOVED (in v2, not in current).
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
    // locked-by-me so the editor opens editable and Restore (canWrite) is enabled.
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
  // Diff now lives inside the "Werkzeuge" (Tools) menu — open it first, then click the row.
  await page.getByTestId('tools-menu-trigger').click();
  // Toolbar diff row (role=menuitem) — title resolves to "Diff against a previous version" (EN).
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

    // Scope every assertion to the diff modal panel — "Bravo"/"Charlie" also appear on the
    // React Flow canvas underneath, so unscoped text matches are ambiguous. The modal is the
    // fixed full-screen overlay that contains the "Workflow Diff" heading.
    const modal = page.locator('div.fixed.inset-0').filter({ hasText: 'Workflow Diff' });

    // Pick version 2 on the left → diff computes against the current draft.
    await modal.getByText('Version 2', { exact: true }).click();

    // Stats grid: vs. current (A+B) the base v2 (A+C) yields step-B added, step-C removed.
    // Each stat card is "LABEL" + a count. Assert the section labels render.
    await expect(modal.getByText('Added', { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(modal.getByText('Removed', { exact: true })).toBeVisible();
    await expect(modal.getByText('Changed', { exact: true })).toBeVisible();

    // Per-node lists: "Nodes added" → Bravo (step-B), "Nodes removed" → Charlie (step-C).
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

    // The "Restore v2" button appears once a version is selected (canWrite=true → enabled).
    const restore = page.getByRole('button', { name: /restore v2/i });
    await expect(restore).toBeVisible({ timeout: 10_000 });

    await restore.click();
    // ConfirmHost modal "Restore workflow to version 2?" — confirm via OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => rollbackVersion, { timeout: 10_000 }).toBe('2');
    expect(rollbackBody?.reason).toMatch(/v2/i);
  });
});
