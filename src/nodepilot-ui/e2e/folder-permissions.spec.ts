import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 45 — Shared Folder Permissions Grant / Revoke (lines 3253-3276).
 *
 * Hermetic: page.route() mocks only (predicate catch-all from fixtures/mockApi.ts).
 *
 * The grant/revoke API is server-side RBAC — we cannot observe "Operator B now sees the
 * folder" in a single browser session. What IS observable, and what the test asserts, is the
 * round-trip the Admin UI performs against the documented endpoints:
 *
 *   - GET  /api/shared-workflow-folders                     → folder list (canAdmin=true)
 *   - GET  /api/shared-workflow-folders/{id}/permissions    → existing grants
 *   - POST /api/shared-workflow-folders/{id}/permissions    → grant {principalType,principalKey,role}
 *   - DELETE /api/shared-workflow-folders/{id}/permissions/{permId} → revoke
 *
 * Flow (WorkflowsPage → SharedFolderTree → SharedFolderPermissionsModal):
 *   Admin selects a folder in the sidebar tree → a "manage permissions" button renders only
 *   when folder.capabilities.canAdmin is true → opens the modal → list/grant/revoke.
 *
 * The modal labels are German source strings ("Vergeben"/"Entfernen") with stable
 * data-testid hooks (shared-folder-perms-*) — we lean on those test-ids for resilience.
 */

const ADMIN = { id: MOCK_USER.id, username: 'e2e-admin', role: 'Admin' };

const FOLDER_ID = 'f0f0f0f0-1111-2222-3333-444444444444';
const FOLDER_PATH = '\\Finance';

// A user available in the principal-picker (GET /api/users) who has no grant yet.
const PICK_USER = { id: '11111111-aaaa-bbbb-cccc-222222222222', username: 'operator-b' };
const GRANT_ID = '99999999-0000-0000-0000-000000000001';

function folder(overrides: Record<string, unknown> = {}) {
  return {
    id: FOLDER_ID,
    parentFolderId: '00000000-0000-0000-0000-000000000001', // child of Root → renders as a tree node
    name: 'Finance',
    path: FOLDER_PATH,
    depth: 1,
    createdAt: '2026-06-01T00:00:00.000Z',
    createdByUserId: null,
    workflowCount: 0,
    capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    ...overrides,
  };
}

/** Root + the Finance folder. canAdmin on Finance unlocks the manage-permissions button. */
function folderList() {
  return JSON.stringify([
    {
      id: '00000000-0000-0000-0000-000000000001',
      parentFolderId: null,
      name: 'Root',
      path: '\\',
      depth: 0,
      createdAt: '2026-06-01T00:00:00.000Z',
      createdByUserId: null,
      workflowCount: 0,
      capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
    },
    folder(),
  ]);
}

function permission(overrides: Record<string, unknown> = {}) {
  return {
    id: GRANT_ID,
    folderId: FOLDER_ID,
    principalType: 'User',
    principalKey: PICK_USER.id,
    principalDisplayName: PICK_USER.username,
    role: 'FolderViewer',
    grantedAt: '2026-06-10T00:00:00.000Z',
    grantedByUserId: ADMIN.id,
    ...overrides,
  };
}

async function asAdmin(page: Page) {
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ADMIN) }),
  );
  await page.route('**/api/shared-workflow-folders', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: folderList() }),
  );
  await page.route('**/api/users', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([PICK_USER]) }),
  );
}

/** Open the permissions modal: navigate, select the Finance folder, click "manage permissions". */
async function openModal(page: Page) {
  await page.goto('/workflows');
  const folderRow = page.locator(`[data-testid="shared-folder-${FOLDER_ID}"]`);
  await expect(folderRow).toBeVisible({ timeout: 15_000 });
  await folderRow.click();

  // The manage-permissions button renders only when the selected folder has canAdmin.
  const manageBtn = page.getByRole('button', { name: /berechtigungen|permission/i });
  await expect(manageBtn).toBeVisible({ timeout: 10_000 });
  await manageBtn.click();

  await expect(page.getByTestId('shared-folder-permissions-modal')).toBeVisible({ timeout: 10_000 });
}

test.describe('Shared Folder Permissions Grant/Revoke (Teil 45)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await asAdmin(page);
  });

  test('45.3 — manage-permissions button opens the modal and lists existing grants', async ({ page }) => {
    await page.route(`**/api/shared-workflow-folders/${FOLDER_ID}/permissions`, (route) => {
      if (route.request().method() === 'GET') {
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([permission()]) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });

    await openModal(page);

    // The folder path is in the header, and the seeded grant (operator-b / FolderViewer) is listed.
    await expect(page.getByRole('heading', { name: /Finance/i })).toBeVisible();
    await expect(page.getByText(PICK_USER.username, { exact: false })).toBeVisible({ timeout: 10_000 });
    // Its role select carries the granted role.
    await expect(page.locator('select').filter({ has: page.locator('option[value="FolderViewer"]') }).first())
      .toHaveValue('FolderViewer');
  });

  test('45.1 — Grant: picking a user + role + "Vergeben" POSTs {principalType:User, principalKey, role}', async ({ page }) => {
    let grantBody: { principalType?: string; principalKey?: string; role?: string } | null = null;
    // Flag (not a call-counter): React StrictMode double-invokes mount effects in dev, so the
    // initial reload() can fire the GET twice — a counter would prematurely flip to "populated".
    let granted = false;
    await page.route(`**/api/shared-workflow-folders/${FOLDER_ID}/permissions`, (route) => {
      const method = route.request().method();
      if (method === 'POST') {
        grantBody = route.request().postDataJSON();
        granted = true;
        return route.fulfill({
          status: 201,
          contentType: 'application/json',
          body: JSON.stringify(permission({ role: grantBody?.role ?? 'FolderViewer' })),
        });
      }
      // GET: empty before the grant, the new grant after (reload() re-fetches post-grant).
      const body = granted ? JSON.stringify([permission({ role: 'FolderOperator' })]) : '[]';
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });

    await openModal(page);

    // Empty list before granting.
    await expect(page.getByText(/keine expliziten|no explicit|keine berechtigung/i)).toBeVisible({ timeout: 10_000 });

    // Pick the user, pick a non-default role (FolderOperator), then grant.
    await page.getByTestId('shared-folder-perms-user-picker').selectOption(PICK_USER.id);
    await page.getByTestId('shared-folder-perms-role-picker').selectOption('FolderOperator');
    await page.getByTestId('shared-folder-perms-grant-btn').click();

    // Assert the grant round-trip carried the right principal + role.
    await expect.poll(() => grantBody, { timeout: 10_000 }).not.toBeNull();
    expect(grantBody).toMatchObject({
      principalType: 'User',
      principalKey: PICK_USER.id,
      role: 'FolderOperator',
    });

    // After the grant + reload the new operator grant appears in the list.
    await expect(page.getByText(PICK_USER.username, { exact: false })).toBeVisible({ timeout: 10_000 });
  });

  test('45.2 — Revoke: confirm + "Entfernen" sends DELETE for that grant, then the row disappears', async ({ page }) => {
    let revoked = false;
    let getCount = 0;
    await page.route(`**/api/shared-workflow-folders/${FOLDER_ID}/permissions`, (route) => {
      getCount += 1;
      // First GET shows the grant; after the DELETE the reload returns the empty list.
      const body = revoked ? '[]' : JSON.stringify([permission()]);
      void getCount;
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });
    // Revoke targets the per-grant DELETE endpoint.
    await page.route(`**/api/shared-workflow-folders/${FOLDER_ID}/permissions/${GRANT_ID}`, (route) => {
      if (route.request().method() === 'DELETE') {
        revoked = true;
        return route.fulfill({ status: 204, body: '' });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openModal(page);

    // The grant is present.
    await expect(page.getByText(PICK_USER.username, { exact: false })).toBeVisible({ timeout: 10_000 });

    // Click the revoke ("Entfernen" / Remove) action in the grant row, then confirm
    // via the in-app ConfirmHost dialog (native confirm() was retired).
    await page.getByRole('button', { name: /entfernen|remove|revoke/i }).first().click();
    await page.getByRole('button', { name: 'OK' }).click();

    // The DELETE fired and, after reload, the grant is gone (empty-state copy returns).
    await expect.poll(() => revoked, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText(/keine expliziten|no explicit|keine berechtigung/i)).toBeVisible({ timeout: 10_000 });
  });
});
