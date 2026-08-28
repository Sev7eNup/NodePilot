import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * Covers E2ETests.md part 41: rollback with a reason.
 *
 * POST /api/workflows/{id}/rollback/{v} takes an optional { reason } body that ends up in the
 * audit details (WORKFLOW_ROLLED_BACK). The only frontend path to it is WorkflowDiffModal, which
 * always sends a non-empty reason, so these tests assert the reason reaches the POST body and
 * names the selected version. Hermetic mocks only; canRestore needs the workflow locked by the
 * current user, and the ConfirmHost modal is confirmed through its OK button.
 */

const WF_ID = 'd41d41d4-0000-0000-0000-00000000d41d';

const CURRENT_DEF = {
  nodes: [
    { id: 'step-A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
    { id: 'step-B', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'Bravo', activityType: 'delay', config: {} } },
  ],
  edges: [{ id: 'e-AB', source: 'step-A', target: 'step-B', type: 'labeled', data: {} }],
};

// v2 differs from current so the diff has content and the Restore button is meaningful.
const V_DEF = {
  nodes: [
    { id: 'step-A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
    { id: 'step-C', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'Charlie', activityType: 'sql', config: {} } },
  ],
  edges: [{ id: 'e-AC', source: 'step-A', target: 'step-C', type: 'labeled', data: {} }],
};

function workflowJson() {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Rollback',
    description: 'rollback-reason e2e fixture',
    isEnabled: true,
    checkedOutByUserId: MOCK_USER.id, // locked by the current user, so canRestore is true
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify(CURRENT_DEF),
    version: 3,
  });
}

const VERSIONS = [
  { version: 3, isCurrent: true, createdAt: '2026-06-03T10:00:00.000Z', createdBy: 'e2e-admin', changeNote: 'current draft' },
  { version: 2, isCurrent: false, createdAt: '2026-06-02T10:00:00.000Z', createdBy: 'alice', changeNote: 'added Charlie' },
  { version: 1, isCurrent: false, createdAt: '2026-06-01T10:00:00.000Z', createdBy: 'bob', changeNote: 'initial' },
];

async function openDiff(page: Page) {
  await seedExpertMode(page);
  await page.goto(`/workflows/${WF_ID}`);
  // Diff sits inside the Tools menu, so open that first, then click the version row.
  await page.getByTestId('tools-menu-trigger').click();
  const diffBtn = page.getByRole('menuitem', { name: /diff against a previous version/i });
  await expect(diffBtn).toBeVisible({ timeout: 20_000 });
  await diffBtn.click();
  await expect(page.getByRole('heading', { name: /workflow diff/i })).toBeVisible({ timeout: 10_000 });
}

test.describe('Rollback mit Reason (Teil 41)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(VERSIONS) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions/2`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ definition: V_DEF }) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions/1`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ definition: V_DEF }) }),
    );
  });

  test('41.1 — Restore v2 POSTs /rollback/2 with a non-empty { reason } body', async ({ page }) => {
    let rollbackBody: { reason?: string } | null = null;
    let hitVersion: string | null = null;
    await page.route(`**/api/workflows/${WF_ID}/rollback/2`, (route) => {
      hitVersion = '2';
      rollbackBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openDiff(page);
    await page.getByText('Version 2', { exact: true }).click();

    const restore = page.getByRole('button', { name: /restore v2/i });
    await expect(restore).toBeVisible({ timeout: 10_000 });

    await restore.click();
    // ConfirmHost modal "Restore workflow to version 2?" — confirm via OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => hitVersion, { timeout: 10_000 }).toBe('2');
    // Core assertion: the rollback body carries a reason, which becomes the audit detail.
    expect(rollbackBody).not.toBeNull();
    expect(typeof rollbackBody!.reason).toBe('string');
    expect(rollbackBody!.reason!.length).toBeGreaterThan(0);
    expect(rollbackBody!.reason).toMatch(/v2/i);
  });

  test('41.1b — the reason is versioned: Restore v1 sends a reason mentioning v1', async ({ page }) => {
    let rollbackBody: { reason?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}/rollback/1`, (route) => {
      rollbackBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openDiff(page);
    await page.getByText('Version 1', { exact: true }).click();

    const restore = page.getByRole('button', { name: /restore v1/i });
    await expect(restore).toBeVisible({ timeout: 10_000 });

    await restore.click();
    await page.getByRole('button', { name: 'OK' }).click(); // ConfirmHost modal

    await expect.poll(() => rollbackBody, { timeout: 10_000 }).not.toBeNull();
    expect(rollbackBody!.reason).toMatch(/v1/i);
  });

  test('41.x — cancelling the confirm modal does NOT POST a rollback', async ({ page }) => {
    let rollbackHit = false;
    await page.route(`**/api/workflows/${WF_ID}/rollback/2`, (route) => {
      rollbackHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openDiff(page);
    await page.getByText('Version 2', { exact: true }).click();
    const restore = page.getByRole('button', { name: /restore v2/i });
    await expect(restore).toBeVisible({ timeout: 10_000 });

    await restore.click();
    // User cancels the ConfirmHost modal.
    await page.getByRole('button', { name: 'Cancel' }).click();

    // Give any (erroneous) request a moment, then assert none fired.
    await expect.poll(() => rollbackHit, { timeout: 3_000 }).toBe(false);
  });
});
