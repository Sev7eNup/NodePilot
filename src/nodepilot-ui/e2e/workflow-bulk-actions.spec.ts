import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 18 / 52 companion — bulk selection on /workflows.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * EN locale under Playwright; selectors are data-testid based.
 *
 * Unlike the row→folder drag (HTML5 DnD, unsynthesizable — see workflow-organisation.spec.ts),
 * every bulk affordance here is a real click target, so these specs drive the actual UI end to
 * end: tick the checkboxes, press the button, assert the requests that leave the browser.
 *
 * Covered:
 *   - selection: row checkboxes, select-all, the bar appearing/disappearing, clear.
 *   - delete: one confirm for the batch, one DELETE per selected workflow.
 *   - move: dialog → destination folder → one POST /move-folder per selected workflow.
 *   - disable: one POST /disable per selected workflow.
 *   - RBAC: a row without canDelete disables the bulk Delete button.
 */

const ME = MOCK_USER; // Admin
const ROOT_ID = '00000000-0000-0000-0000-000000000001';
const PROD_ID = 'f0000000-0000-0000-0000-0000000000a1';

const WF_A = 'a1111111-1111-1111-1111-111111111111';
const WF_B = 'b2222222-2222-2222-2222-222222222222';
const WF_C = 'c3333333-3333-3333-3333-333333333333';

function folder(overrides: Record<string, unknown> = {}) {
  return {
    id: PROD_ID,
    parentFolderId: ROOT_ID,
    name: 'Production',
    path: '/Production',
    depth: 1,
    createdAt: '2026-01-01T00:00:00.000Z',
    createdByUserId: ME.id,
    workflowCount: 0,
    capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    ...overrides,
  };
}

const rootFolder = (overrides: Record<string, unknown> = {}) =>
  folder({ id: ROOT_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0, workflowCount: 3, ...overrides });

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_A,
    name: 'Alpha',
    description: '',
    isEnabled: true,
    version: 1,
    activityCount: 0,
    triggerTypes: [] as string[],
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    folderId: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    successCount: 0,
    totalCount: 0,
    avgDurationMs: null,
    lastExecution: null,
    capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
    ...overrides,
  };
}

const THREE = [
  workflow({ id: WF_A, name: 'Alpha' }),
  workflow({ id: WF_B, name: 'Beta' }),
  workflow({ id: WF_C, name: 'Gamma' }),
];

async function openList(page: Page, workflows: unknown[] = THREE) {
  await page.route('**/api/shared-workflow-folders', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify([rootFolder(), folder()]),
        })
      : route.continue(),
  );
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflows) }),
  );
  await page.goto('/workflows');
  await expect(page.getByRole('button', { name: 'Alpha' })).toBeVisible({ timeout: 15_000 });
}

test.describe('Workflow bulk selection & actions', () => {
  test.beforeEach(async ({ page }) => {
    // Desktop viewport: the table branch (with the checkbox column) instead of the mobile cards.
    await page.setViewportSize({ width: 1440, height: 900 });
    await installDefaultMocks(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME) }),
    );
  });

  test('the bulk bar appears with the first selected row and clears again', async ({ page }) => {
    await openList(page);
    const bar = page.getByTestId('workflow-bulk-bar');
    await expect(bar).toBeHidden();

    await page.getByTestId(`workflow-select-${WF_A}`).check();
    await expect(bar).toBeVisible();
    await expect(bar).toContainText('1 selected');

    await page.getByTestId(`workflow-select-${WF_B}`).check();
    await expect(bar).toContainText('2 selected');

    await page.getByTestId('bulk-clear').click();
    await expect(bar).toBeHidden();
  });

  test('select-all ticks every row and the header box reflects the state', async ({ page }) => {
    await openList(page);

    await page.getByTestId('workflow-select-all').check();
    await expect(page.getByTestId('workflow-bulk-bar')).toContainText('3 selected');
    for (const id of [WF_A, WF_B, WF_C]) {
      await expect(page.getByTestId(`workflow-select-${id}`)).toBeChecked();
    }

    await page.getByTestId('workflow-select-all').uncheck();
    await expect(page.getByTestId('workflow-bulk-bar')).toBeHidden();
  });

  test('bulk delete confirms once and issues one DELETE per selected workflow', async ({ page }) => {
    const deleted: string[] = [];
    for (const id of [WF_A, WF_B, WF_C]) {
      await page.route(`**/api/workflows/${id}`, (route) => {
        if (route.request().method() !== 'DELETE') return route.continue();
        deleted.push(id);
        return route.fulfill({ status: 204, body: '' });
      });
    }

    await openList(page);
    await page.getByTestId(`workflow-select-${WF_A}`).check();
    await page.getByTestId(`workflow-select-${WF_C}`).check();

    await page.getByTestId('bulk-delete').click();

    // Store-driven confirm dialog (ConfirmHost), not window.confirm — it confirms with OK.
    // One dialog for the whole batch is the behaviour under test: the count appears in it.
    await expect(page.getByText(/2 selected workflows/i)).toBeVisible();
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => [...deleted].sort(), { timeout: 10_000 }).toEqual([WF_A, WF_C].sort());
    // Beta was never selected and must be untouched.
    expect(deleted).not.toContain(WF_B);
  });

  test('bulk move sends one POST /move-folder per selected workflow', async ({ page }) => {
    const moves: { id: string; target: string }[] = [];
    for (const id of [WF_A, WF_B, WF_C]) {
      await page.route(`**/api/workflows/${id}/move-folder`, (route) => {
        moves.push({ id, target: route.request().postDataJSON()?.targetFolderId });
        return route.fulfill({ status: 204, body: '' });
      });
    }

    await openList(page);
    await page.getByTestId('workflow-select-all').check();

    await page.getByTestId('bulk-move').click();
    await expect(page.getByTestId('bulk-move-dialog')).toBeVisible();
    await page.getByTestId(`bulk-move-target-${PROD_ID}`).click();
    await page.getByTestId('bulk-move-confirm').click();

    await expect.poll(() => moves.length, { timeout: 10_000 }).toBe(3);
    expect(moves.every((m) => m.target === PROD_ID)).toBe(true);
  });

  test('bulk disable posts /disable for every enabled workflow in the selection', async ({ page }) => {
    const disabled: string[] = [];
    for (const id of [WF_A, WF_B, WF_C]) {
      await page.route(`**/api/workflows/${id}/disable`, (route) => {
        disabled.push(id);
        return route.fulfill({ status: 204, body: '' });
      });
    }

    await openList(page);
    await page.getByTestId(`workflow-select-${WF_A}`).check();
    await page.getByTestId(`workflow-select-${WF_B}`).check();

    await page.getByTestId('bulk-disable').click();

    await expect.poll(() => [...disabled].sort(), { timeout: 10_000 }).toEqual([WF_A, WF_B].sort());
  });

  test('a selected row without canDelete disables the bulk Delete button', async ({ page }) => {
    await openList(page, [
      workflow({ id: WF_A, name: 'Alpha' }),
      workflow({
        id: WF_B, name: 'Beta',
        capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: false, canAdmin: false },
      }),
    ]);

    await page.getByTestId(`workflow-select-${WF_A}`).check();
    await expect(page.getByTestId('bulk-delete')).toBeEnabled();

    await page.getByTestId(`workflow-select-${WF_B}`).check();
    await expect(page.getByTestId('bulk-delete')).toBeDisabled();
    // Move stays available — Beta may still be edited, just not deleted.
    await expect(page.getByTestId('bulk-move')).toBeEnabled();
  });

  test('a checked-out workflow in the selection blocks bulk Enable but not Disable', async ({ page }) => {
    await openList(page, [
      workflow({ id: WF_A, name: 'Alpha', isEnabled: false }),
      workflow({
        id: WF_B, name: 'Beta', isEnabled: false,
        checkedOutByUserId: 'ffffffff-ffff-ffff-ffff-ffffffffffff', checkedOutByUserName: 'otto',
        checkedOutAt: '2026-01-02T00:00:00.000Z',
      }),
    ]);

    await page.getByTestId(`workflow-select-${WF_A}`).check();
    await expect(page.getByTestId('bulk-enable')).toBeEnabled();

    await page.getByTestId(`workflow-select-${WF_B}`).check();
    await expect(page.getByTestId('bulk-enable')).toBeDisabled();
    await expect(page.getByTestId('bulk-disable')).toBeEnabled();
  });
});
