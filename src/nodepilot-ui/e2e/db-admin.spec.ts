import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 31 — DB-Admin Viewer.
 *
 * Route `/database` (Admin-only — guarded by <AdminOnly>). DbViewerPage renders a TableList
 * sidebar fed by GET /api/dbadmin/tables, and a TableGrid (rows) fed by
 * GET /api/dbadmin/tables/{name}/rows?skip&take&orderBy&desc.
 *   - Cell edit → PATCH /api/dbadmin/tables/{name}/rows?pk=... body {column,value}
 *   - Row delete → native confirm() then DELETE /api/dbadmin/tables/{name}/rows?pk=...
 *
 * Hermetic: page.route() mocks only. The default MOCK_USER is Admin, which the AdminOnly guard
 * requires. The catch-all returns [] for unmocked /api/* so the page mounts cookieless.
 * Selectors stay language-agnostic / bilingual (preview build renders EN under Playwright).
 */

// Two EF entities; "Workflows" is editable + has cascade targets, "AuditLogs" is read-only.
const TABLES = [
  {
    name: 'Workflow',
    displayName: 'Workflows',
    dbTableName: 'Workflows',
    pkColumns: ['Id'],
    capabilities: { canUpdate: true, canDelete: true },
    rowCount: 2,
    cascadeDeletesTo: ['WorkflowExecutions', 'WorkflowVersions'],
    columns: [
      { name: 'Id', clrType: 'guid', isNullable: false, maxLength: null, isPrimaryKey: true, isMasked: false, isReadOnly: true },
      { name: 'Name', clrType: 'string', isNullable: false, maxLength: 200, isPrimaryKey: false, isMasked: false, isReadOnly: false },
      { name: 'IsEnabled', clrType: 'boolean', isNullable: false, maxLength: null, isPrimaryKey: false, isMasked: false, isReadOnly: false },
    ],
  },
  {
    name: 'AuditLog',
    displayName: 'AuditLogs',
    dbTableName: 'AuditLogs',
    pkColumns: ['Id'],
    capabilities: { canUpdate: false, canDelete: false },
    rowCount: 1,
    cascadeDeletesTo: [],
    columns: [
      { name: 'Id', clrType: 'long', isNullable: false, maxLength: null, isPrimaryKey: true, isMasked: false, isReadOnly: true },
      { name: 'Action', clrType: 'string', isNullable: false, maxLength: 100, isPrimaryKey: false, isMasked: false, isReadOnly: true },
    ],
  },
];

const WORKFLOW_ROWS = {
  total: 2,
  rows: [
    { Id: '11111111-1111-1111-1111-111111111111', Name: 'Patch Tuesday', IsEnabled: true },
    { Id: '22222222-2222-2222-2222-222222222222', Name: 'Nightly Backup', IsEnabled: false },
  ],
};

async function mockTables(page: import('@playwright/test').Page) {
  await page.route('**/api/dbadmin/tables', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(TABLES) }),
  );
}

test.describe('DB-Admin Viewer (Teil 31)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page); // MOCK_USER is Admin → AdminOnly guard passes
    await mockTables(page);
  });

  test('31.1 — table list renders all EF entities with read-only marker', async ({ page }) => {
    await page.goto('/database');

    // Both tables show up in the sidebar as buttons (accessible name = displayName text).
    // The left nav also has a "Workflows" *link*; using role=button disambiguates.
    await expect(page.getByRole('button', { name: /Workflows/ })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /AuditLogs/ })).toBeVisible();
    // The read-only table is flagged with a lock marker (accessible label "Read-only").
    await expect(page.getByLabel(/read-only|nur lesen/i).first()).toBeVisible();
  });

  test('31.2 — selecting a table shows its rows and columns', async ({ page }) => {
    // /rows must be mocked BEFORE the catch-all-relative ordering: last route wins, and this is
    // added after installDefaultMocks, so it takes precedence.
    let rowsUrl: string | null = null;
    await page.route('**/api/dbadmin/tables/*/rows**', (route) => {
      if (route.request().method() === 'GET') {
        rowsUrl = route.request().url();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(WORKFLOW_ROWS) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/database');
    await page.getByRole('button', { name: /Workflows/ }).click();

    // Column headers from the table metadata.
    await expect(page.getByRole('columnheader', { name: /Name/ })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('columnheader', { name: /IsEnabled/ })).toBeVisible();
    // Row data.
    await expect(page.getByText('Patch Tuesday')).toBeVisible();
    await expect(page.getByText('Nightly Backup')).toBeVisible();
    // The rows request hit the named table.
    await expect.poll(() => rowsUrl, { timeout: 10_000 }).not.toBeNull();
    expect(rowsUrl).toContain('/dbadmin/tables/Workflow/rows');
  });

  test('31.3 — editing a cell issues a PATCH with pk in the query string', async ({ page }) => {
    await page.route('**/api/dbadmin/tables/*/rows**', (route) => {
      const method = route.request().method();
      if (method === 'GET') {
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(WORKFLOW_ROWS) });
      }
      // PATCH success.
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/database');
    await page.getByRole('button', { name: /Workflows/ }).click();
    await expect(page.getByText('Patch Tuesday')).toBeVisible({ timeout: 15_000 });

    // Editable cell click opens EditCellDialog. The Name column is editable (not PK / RO / masked).
    const patchPromise = page.waitForRequest(
      (req) => req.method() === 'PATCH' && /\/dbadmin\/tables\/Workflow\/rows\?/.test(req.url()),
    );

    await page.getByText('Patch Tuesday').click();
    const dialog = page.getByRole('heading', { name: /edit cell|zelle bearbeiten/i });
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    const input = page.locator('input[type="text"]');
    await expect(input).toBeVisible();
    await input.fill('Patch Wednesday');
    await page.getByRole('button', { name: /^save$|^speichern$/i }).click();

    const patchReq = await patchPromise;
    // pk value carried in the query string (per dbAdminApi.patchRow).
    expect(patchReq.url()).toContain('pk=11111111-1111-1111-1111-111111111111');
    expect(patchReq.postDataJSON()).toMatchObject({ column: 'Name', value: 'Patch Wednesday' });
  });

  test('31.4 — deleting a row confirms then issues DELETE with pk in the query string', async ({ page }) => {
    await page.route('**/api/dbadmin/tables/*/rows**', (route) => {
      if (route.request().method() === 'GET') {
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(WORKFLOW_ROWS) });
      }
      return route.fulfill({ status: 204, body: '' });
    });

    await page.goto('/database');
    await page.getByRole('button', { name: /Workflows/ }).click();
    await expect(page.getByText('Patch Tuesday')).toBeVisible({ timeout: 15_000 });

    const deletePromise = page.waitForRequest(
      (req) => req.method() === 'DELETE' && /\/dbadmin\/tables\/Workflow\/rows\?/.test(req.url()),
    );

    // The first row's delete button (Trash2 icon) — title is the localized "Delete".
    const firstRow = page.getByRole('row').filter({ hasText: 'Patch Tuesday' });
    await firstRow.getByRole('button', { name: /delete|löschen/i }).click();

    // The in-app ConfirmHost dialog surfaces the cascade hint; confirm via OK.
    await expect(page.getByText(/WorkflowExecutions/)).toBeVisible({ timeout: 10_000 });
    await page.getByRole('button', { name: 'OK' }).click();

    const deleteReq = await deletePromise;
    expect(deleteReq.url()).toContain('pk=11111111-1111-1111-1111-111111111111');
  });

  test('31.5 — read-only table exposes no delete column / edit affordance', async ({ page }) => {
    // AuditLog rows.
    await page.route('**/api/dbadmin/tables/*/rows**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ total: 1, rows: [{ Id: 7, Action: 'LOGIN_OK' }] }),
      }),
    );

    await page.goto('/database');
    await page.getByRole('button', { name: /AuditLogs/ }).click();
    await expect(page.getByText('LOGIN_OK')).toBeVisible({ timeout: 15_000 });

    // canDelete=false → no per-row delete buttons at all.
    const row = page.getByRole('row').filter({ hasText: 'LOGIN_OK' });
    await expect(row.getByRole('button', { name: /delete|löschen/i })).toHaveCount(0);

    // Clicking a read-only cell does NOT open the edit dialog.
    await page.getByText('LOGIN_OK').click();
    await expect(page.getByRole('heading', { name: /edit cell|zelle bearbeiten/i })).toHaveCount(0);
  });
});

test.describe('DB-Admin Viewer — access control (Teil 31.5 / 26.5)', () => {
  test('31.6 — non-admin (Viewer) hitting /database is redirected away', async ({ page }) => {
    await installDefaultMocks(page);
    await mockTables(page);
    // Override /me to a Viewer; AdminOnly guard → <Navigate to="/" replace/>.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: MOCK_USER.id, username: 'viewer1', role: 'Viewer' }),
      }),
    );

    await page.goto('/database');
    await expect(page).not.toHaveURL(/\/database/, { timeout: 15_000 });
    // DB-admin chrome must not render for a Viewer.
    await expect(page.getByRole('button', { name: /Workflows/ })).toHaveCount(0);
  });
});
