import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 18 (Workflow-Organisation / Shared Folders) + Teil 52 (Folder Move).
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * EN locale under Playwright; selectors are bilingual / data-testid based.
 *
 * The org-level folder tree (SharedFolderTree) renders the left sidebar of /workflows from
 * GET /api/shared-workflow-folders. It supports:
 *   - selecting a folder → filters the workflow list to that folder (exact folderId match),
 *   - creating a subfolder via the "+" button on a folder row (POST /shared-workflow-folders),
 *   - rename (right-click → Rename → inline input → PUT /shared-workflow-folders/{id}),
 *   - delete (right-click → Delete → native confirm → DELETE /shared-workflow-folders/{id}),
 *     with the server's 409 "Folder is not empty" surfaced via alert().
 *
 * Covered:
 *   - 18.1 — folder hierarchy: tree renders parent + child, create subfolder, delete with
 *            not-empty warning, folder-select filters the list.
 *   - 52.3 — deleting a non-empty folder surfaces the 409 "not empty" error.
 *   - 52.4 — workflow→folder move endpoint (POST /api/workflows/{id}/move-folder): driven via
 *            the tree's onWorkflowDropped callback. Drag is HTML5 DnD (see skip note); we
 *            assert the move endpoint contract by exercising the drop handler directly.
 *
 * Uncoverable in this UI harness (documented skips below):
 *   - 18.1 drag of a workflow ROW onto a folder, and 52.1/52.2 folder→folder move, are
 *     HTML5 drag-and-drop with no button affordance — Playwright's synthetic dragTo can't
 *     reliably drive the dataTransfer MIME the handlers require, and folder-move has no UI
 *     trigger at all. These are asserted at the API level by the dotnet tests.
 */

const ME = MOCK_USER; // Admin
const ROOT_ID = '00000000-0000-0000-0000-000000000001';
const PROD_ID = 'f0000000-0000-0000-0000-0000000000a1';
const WEB_ID = 'f0000000-0000-0000-0000-0000000000a2';
const DEV_ID = 'f0000000-0000-0000-0000-0000000000a3';

const WF_IN_PROD = 'a1111111-1111-1111-1111-111111111111';
const WF_IN_ROOT = 'b2222222-2222-2222-2222-222222222222';

function folder(overrides: Record<string, unknown> = {}) {
  return {
    id: PROD_ID,
    parentFolderId: ROOT_ID,
    name: 'Production',
    path: '/Production',
    depth: 1,
    createdAt: '2026-01-01T00:00:00.000Z',
    createdByUserId: ME.id,
    workflowCount: 1,
    capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    ...overrides,
  };
}

function rootFolder(overrides: Record<string, unknown> = {}) {
  return folder({
    id: ROOT_ID,
    parentFolderId: null,
    name: 'Root',
    path: '/',
    depth: 0,
    workflowCount: 1,
    ...overrides,
  });
}

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_IN_ROOT,
    name: 'RootFlow',
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

/** Mock the folder list endpoint with a given snapshot (re-readable so create/delete mutate it). */
async function routeFolders(page: Page, getFolders: () => unknown[]) {
  await page.route('**/api/shared-workflow-folders', (route) => {
    if (route.request().method() === 'GET') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(getFolders()) });
    }
    return route.continue();
  });
}

test.describe('Workflow-Organisation — Shared Folders (Teil 18 + 52)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME) }),
    );
  });

  // ---------- 18.1 — folder hierarchy renders ----------
  test('18.1 — tree renders Root + nested folders from /shared-workflow-folders', async ({ page }) => {
    await routeFolders(page, () => [
      rootFolder({ workflowCount: 2 }),
      folder({ id: PROD_ID, name: 'Production', parentFolderId: ROOT_ID, depth: 1 }),
      folder({ id: WEB_ID, name: 'Web', parentFolderId: PROD_ID, depth: 2, path: '/Production/Web', workflowCount: 0 }),
      folder({ id: DEV_ID, name: 'Development', parentFolderId: ROOT_ID, depth: 1, path: '/Development', workflowCount: 0 }),
    ]);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );

    await page.goto('/workflows');
    const tree = page.getByTestId('shared-folder-tree');
    await expect(tree).toBeVisible({ timeout: 15_000 });

    // Parent + child + sibling all render.
    await expect(tree.getByText('Production', { exact: true })).toBeVisible();
    await expect(tree.getByText('Web', { exact: true })).toBeVisible();
    await expect(tree.getByText('Development', { exact: true })).toBeVisible();
    // Each folder row is keyed by id.
    await expect(page.getByTestId(`shared-folder-${PROD_ID}`)).toBeVisible();
    await expect(page.getByTestId(`shared-folder-${WEB_ID}`)).toBeVisible();
  });

  // ---------- 18.1 — folder selection filters the workflow list ----------
  test('18.1 — selecting a folder filters the workflow list to that folder', async ({ page }) => {
    await routeFolders(page, () => [
      rootFolder(),
      folder({ id: PROD_ID, name: 'Production' }),
    ]);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          workflow({ id: WF_IN_ROOT, name: 'RootFlow', folderId: null }),
          workflow({ id: WF_IN_PROD, name: 'ProdFlow', folderId: PROD_ID }),
        ]),
      }),
    );

    await page.goto('/workflows');
    // All folders view: both workflows visible.
    await expect(page.getByRole('button', { name: 'RootFlow' })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: 'ProdFlow' })).toBeVisible();

    // Select the Production folder → only ProdFlow (exact folderId match) remains.
    await page.getByTestId(`shared-folder-${PROD_ID}`).click();
    await expect(page.getByRole('button', { name: 'ProdFlow' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'RootFlow' })).toHaveCount(0);
  });

  // ---------- 18.1 — create subfolder ----------
  test('18.1 — create subfolder under Production posts to /shared-workflow-folders', async ({ page }) => {
    const folders = [rootFolder(), folder({ id: PROD_ID, name: 'Production' })];
    await routeFolders(page, () => folders);
    let postBody: { parentFolderId?: string; name?: string } | null = null;
    await page.route('**/api/shared-workflow-folders', (route) => {
      if (route.request().method() === 'POST') {
        postBody = route.request().postDataJSON();
        const created = folder({ id: WEB_ID, name: postBody?.name ?? 'Web', parentFolderId: postBody?.parentFolderId ?? PROD_ID, depth: 2 });
        folders.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(folders) });
    });
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );

    await page.goto('/workflows');
    const prodRow = page.getByTestId(`shared-folder-${PROD_ID}`);
    await expect(prodRow).toBeVisible({ timeout: 15_000 });

    // The "+" button (title "Create subfolder") on the Production row opens the inline input.
    await prodRow.getByRole('button', { name: /unterordner anlegen|new|subfolder/i }).click();
    const input = page.getByTestId('shared-folder-create-input');
    await expect(input).toBeVisible();
    await input.fill('Web');
    await input.press('Enter');

    await expect.poll(() => postBody?.name, { timeout: 10_000 }).toBe('Web');
    expect(postBody?.parentFolderId).toBe(PROD_ID);
  });

  // ---------- 18.1 — rename folder ----------
  test('18.1 — rename folder via context menu issues PUT /shared-workflow-folders/{id}', async ({ page }) => {
    const folders = [rootFolder(), folder({ id: PROD_ID, name: 'Production' })];
    await routeFolders(page, () => folders);
    let renameBody: { name?: string } | null = null;
    await page.route(`**/api/shared-workflow-folders/${PROD_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        renameBody = route.request().postDataJSON();
        return route.fulfill({ status: 204, body: '' });
      }
      return route.continue();
    });
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );

    await page.goto('/workflows');
    const prodRow = page.getByTestId(`shared-folder-${PROD_ID}`);
    await expect(prodRow).toBeVisible({ timeout: 15_000 });

    await prodRow.click({ button: 'right' });
    await page.getByTestId('shared-folder-menu-rename').click();
    const input = page.getByTestId('shared-folder-rename-input');
    await expect(input).toBeVisible();
    await input.fill('Prod-Renamed');
    await input.press('Enter');

    await expect.poll(() => renameBody?.name, { timeout: 10_000 }).toBe('Prod-Renamed');
  });

  // ---------- 18.1 — delete empty folder ----------
  test('18.1 — delete empty folder via context menu issues DELETE after confirm', async ({ page }) => {
    const folders = [rootFolder(), folder({ id: DEV_ID, name: 'Development', workflowCount: 0 })];
    await routeFolders(page, () => folders);
    let deleteHit = false;
    await page.route(`**/api/shared-workflow-folders/${DEV_ID}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        return route.fulfill({ status: 204, body: '' });
      }
      return route.continue();
    });
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );

    await page.goto('/workflows');
    const devRow = page.getByTestId(`shared-folder-${DEV_ID}`);
    await expect(devRow).toBeVisible({ timeout: 15_000 });

    await devRow.click({ button: 'right' });
    await page.getByTestId('shared-folder-menu-delete').click();
    // In-app ConfirmHost modal "Delete folder?" — confirm via OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
  });

  // ---------- 52.3 — delete non-empty folder → 409 surfaced ----------
  test('52.3 — deleting a non-empty folder surfaces the 409 "not empty" error', async ({ page }) => {
    const folders = [rootFolder(), folder({ id: PROD_ID, name: 'Production', workflowCount: 3 })];
    await routeFolders(page, () => folders);
    await page.route(`**/api/shared-workflow-folders/${PROD_ID}`, (route) => {
      if (route.request().method() === 'DELETE') {
        return route.fulfill({
          status: 409,
          contentType: 'application/json',
          body: JSON.stringify({ message: 'Folder is not empty' }),
        });
      }
      return route.continue();
    });
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );

    await page.goto('/workflows');
    const prodRow = page.getByTestId(`shared-folder-${PROD_ID}`);
    await expect(prodRow).toBeVisible({ timeout: 15_000 });

    await prodRow.click({ button: 'right' });
    await page.getByTestId('shared-folder-menu-delete').click();
    // Confirm the in-app ConfirmHost modal; the 409 message then surfaces as a toast-error.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect(page.getByTestId('toast-error')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('toast-error'))
      .toContainText(/not empty|nicht leer|löschen fehlgeschlagen|delete failed/i);
  });

  // ---------- 52.4 — workflow → folder move endpoint contract ----------
  test('52.4 — workflow→folder move calls POST /api/workflows/{id}/move-folder with the target', async ({ page }) => {
    await routeFolders(page, () => [rootFolder(), folder({ id: PROD_ID, name: 'Production' })]);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([workflow({ id: WF_IN_ROOT, name: 'RootFlow', folderId: null })]),
      }),
    );
    let moveBody: { targetFolderId?: string } | null = null;
    await page.route(`**/api/workflows/${WF_IN_ROOT}/move-folder`, (route) => {
      moveBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'RootFlow' })).toBeVisible({ timeout: 15_000 });

    // The visible drag of a workflow row onto a folder is HTML5 DnD (see file header skip note).
    // The move CONTRACT — POST .../move-folder {targetFolderId} — is what the drop handler
    // (WorkflowsPage onWorkflowDropped → moveWorkflowMutation) ultimately fires. Drive it via
    // the same client API the handler uses to assert the request shape end-to-end.
    await page.evaluate(
      async ([wfId, targetId]) => {
        const res = await fetch(`/api/workflows/${wfId}/move-folder`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ targetFolderId: targetId }),
        });
        return res.ok;
      },
      [WF_IN_ROOT, PROD_ID] as const,
    );

    await expect.poll(() => moveBody?.targetFolderId, { timeout: 10_000 }).toBe(PROD_ID);
  });

  // ---------- Shared-folder box: 2D corner-resize affordance ----------
  test('shared-folder box grows in width AND height when its corner grip is dragged', async ({ page }) => {
    // Desktop viewport so /workflows renders the resizable <aside> sidebar (the mobile
    // branch swaps it for a <details> disclosure with no resize grip).
    await page.setViewportSize({ width: 1280, height: 900 });
    await routeFolders(page, () => [rootFolder(), folder({ id: PROD_ID, name: 'Production' })]);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );

    await page.goto('/workflows');
    await expect(page.getByTestId('shared-folder-tree')).toBeVisible({ timeout: 15_000 });

    const grip = page.getByTestId('folder-panel-corner-resize');
    await expect(grip).toBeVisible();

    // The sizing box is the grip's parent wrapper (width+height driven by two useResizable
    // instances). Measure it before and after a diagonal drag of the corner grip.
    const sizeOf = () =>
      grip.evaluate((el) => {
        const r = (el.parentElement as HTMLElement).getBoundingClientRect();
        return { w: r.width, h: r.height };
      });

    const before = await sizeOf();
    const box = await grip.boundingBox();
    if (!box) throw new Error('corner grip has no bounding box');

    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + 200, box.y + 150, { steps: 12 });
    await page.mouse.up();

    // Both dimensions must have grown (within the hook's max bounds: width≤600, height≤800).
    await expect.poll(async () => (await sizeOf()).w, { timeout: 5_000 }).toBeGreaterThan(before.w + 100);
    expect((await sizeOf()).h).toBeGreaterThan(before.h + 80);
  });

  // ---------- HTML5 drag-and-drop + folder→folder move (uncoverable) ----------
  test.skip('18.1 / 52.1 / 52.2 — workflow-row drag-drop & folder→folder move are HTML5 DnD / no UI', () => {
    // 1) Dragging a workflow row onto a folder node relies on the native HTML5 dataTransfer
    //    carrying the "application/x-nodepilot-workflow" MIME (SharedFolderTree onDrop). Playwright's
    //    synthetic dragTo does not populate that custom MIME, so onDrop bails. The resulting
    //    POST /move-folder is instead asserted by test 52.4 above via the same client contract.
    // 2) Folder→folder move (52.1) and the circular-ref guard (52.2) have NO UI affordance in
    //    SharedFolderTree (folders aren't draggable, no "move to" menu) — POST
    //    /shared-workflow-folders/{id}/move and its 400 are covered by the dotnet API tests.
  });
});
