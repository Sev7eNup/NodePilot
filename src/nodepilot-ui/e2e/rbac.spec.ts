import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 26 — RBAC Rollen-Crossings (the core value here).
 *
 * Hermetic: page.route() mocks only. We simulate each role by overriding `/api/auth/me`
 * with role Admin / Operator / Viewer (authStore reads me.id + me.role). The client-side
 * mirror lives in src/lib/rbac.ts: canWrite = Admin|Operator, canDelete = Admin only,
 * canAdmin = Admin only. WorkflowsPage rows fall back to those global flags when the server
 * omits per-row `capabilities` (so we leave capabilities OUT of the mocks here to exercise
 * the role gate, not the folder-ACL gate).
 *
 * Maps to:
 *   - 26.1 — Viewer cannot write: no "New Workflow", no edit/disable/delete on rows.
 *   - 26.2 — Operator can edit but not delete: edit/duplicate present, delete hidden; same on
 *            Machines (test/edit visible, delete hidden) and Credentials.
 *   - 26.4 — Force-Unlock only Admin: a row locked-by-other shows force-unlock for Admin only.
 *   - 26.5 — DB-Admin viewer only Admin: sidebar /database link + /users + /audit hidden for
 *            non-admins; direct nav to an admin route redirects non-admins away. Same gate
 *            covers /support-log (moved out of Settings → System onto its own admin-only route).
 *
 *  Note: API-level 403 assertions (curl with a role JWT) are server-side and not coverable in
 *  this hermetic UI harness; we assert the UI affordances the server 403 would otherwise back.
 */

const ME_ADMIN = { id: '00000000-0000-0000-0000-0000000000a0', username: 'admin1', role: 'Admin' };
const ME_OPERATOR = { id: '00000000-0000-0000-0000-0000000000a1', username: 'operator1', role: 'Operator' };
const ME_VIEWER = { id: '00000000-0000-0000-0000-0000000000a2', username: 'viewer1', role: 'Viewer' };

const WF_ID = 'dddddddd-1111-2222-3333-444444444444';
const OTHER_USER = '00000000-0000-0000-0000-0000000000ff';

async function asRole(page: import('@playwright/test').Page, me: typeof ME_ADMIN) {
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(me) }),
  );
}

// A workflow WITHOUT `capabilities` so WorkflowsPage uses the global role flags for the row.
function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_ID,
    name: 'WF_RBAC',
    description: '',
    isEnabled: true,
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    activityCount: 0,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

function machine(overrides: Record<string, unknown> = {}) {
  return {
    id: 'm-1',
    name: 'WIN-RBAC',
    hostname: 'win-rbac.local',
    winRmPort: 5985,
    useSsl: false,
    isReachable: true,
    activeRunCount: 0,
    usedByWorkflowCount: 0,
    recentStepCount: 0,
    recentFailedStepCount: 0,
    defaultCredentialId: null,
    tags: null,
    lastConnectivityCheck: null,
    ...overrides,
  };
}

test.describe('RBAC Rollen-Crossings (Teil 26)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  // ---------- 26.1 — Viewer cannot write ----------
  test('26.1 — Viewer: no "New Workflow" and no edit/disable/delete row actions', async ({ page }) => {
    await asRole(page, ME_VIEWER);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'WF_RBAC' })).toBeVisible({ timeout: 15_000 });

    // Create affordances hidden.
    await expect(page.getByRole('button', { name: /new workflow|neuer workflow/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /ai workflow|ki.workflow/i })).toHaveCount(0);

    // Row write/delete actions hidden: no enable/disable toggle, no duplicate, no delete.
    const row = page.getByRole('row').filter({ hasText: 'WF_RBAC' });
    await expect(row.getByRole('button', { name: /disable|deaktivieren|enable|aktivieren/i })).toHaveCount(0);
    await expect(row.getByRole('button', { name: /duplicate|duplizieren/i })).toHaveCount(0);
    await expect(row.getByRole('button', { name: /^delete$|löschen/i })).toHaveCount(0);
  });

  test('26.1b — Viewer: admin sidebar links (Users / Audit / Database) hidden', async ({ page }) => {
    await asRole(page, ME_VIEWER);
    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'WF_RBAC' }).or(page.getByText(/no workflows|keine workflows/i))).toBeVisible({ timeout: 15_000 });

    const nav = page.getByRole('navigation');
    await expect(nav.getByRole('link', { name: /^users$|benutzer/i })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: /audit log|audit-log/i })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: /^database$|datenbank/i })).toHaveCount(0);
    // Log (support-log) nav item — anchored so it never matches "Audit Log".
    await expect(nav.getByRole('link', { name: /^log$/i })).toHaveCount(0);
  });

  // ---------- 26.2 — Operator can edit but not delete ----------
  test('26.2 — Operator: workflow edit/duplicate visible, delete hidden', async ({ page }) => {
    await asRole(page, ME_OPERATOR);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'WF_RBAC' })).toBeVisible({ timeout: 15_000 });

    // Operator CAN create.
    await expect(page.getByRole('button', { name: /new workflow|neuer workflow/i })).toBeVisible();

    const row = page.getByRole('row').filter({ hasText: 'WF_RBAC' });
    // Edit/duplicate present (write), delete absent (Admin-only).
    await expect(row.getByRole('button', { name: /duplicate|duplizieren/i })).toBeVisible();
    await expect(row.getByRole('button', { name: /^delete$|löschen/i })).toHaveCount(0);
  });

  test('26.2b — Operator: Machine test/edit visible, delete hidden', async ({ page }) => {
    await asRole(page, ME_OPERATOR);
    await page.route('**/api/machines', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([machine()]) }),
    );

    await page.goto('/machines');
    await expect(page.getByText('WIN-RBAC', { exact: true })).toBeVisible({ timeout: 15_000 });

    // Operator can add (canWrite).
    await expect(page.getByRole('button', { name: /add machine|maschine hinzufügen|neue maschine/i })).toBeVisible();

    const row = page.getByRole('row').filter({ hasText: 'WIN-RBAC' });
    await expect(row.getByRole('button', { name: /edit|bearbeiten/i })).toBeVisible();
    // Delete is Admin-only (canDelete).
    await expect(row.getByRole('button', { name: /^delete$|löschen/i })).toHaveCount(0);
  });

  test('26.2c — Admin: Machine delete IS available (canDelete)', async ({ page }) => {
    await asRole(page, ME_ADMIN);
    await page.route('**/api/machines', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([machine()]) }),
    );

    await page.goto('/machines');
    await expect(page.getByText('WIN-RBAC', { exact: true })).toBeVisible({ timeout: 15_000 });

    const row = page.getByRole('row').filter({ hasText: 'WIN-RBAC' });
    await expect(row.getByRole('button', { name: /^delete$|löschen/i })).toBeVisible();
  });

  // ---------- 26.4 — Force-Unlock only Admin ----------
  test('26.4 — locked-by-other: Operator has NO force-unlock, Admin DOES', async ({ page }) => {
    const lockedByOther = workflow({
      isEnabled: false,
      checkedOutByUserId: OTHER_USER,
      checkedOutByUserName: 'someone-else',
      checkedOutAt: '2026-06-18T08:00:00.000Z',
    });

    // Operator first.
    await asRole(page, ME_OPERATOR);
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([lockedByOther]) }),
    );
    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'WF_RBAC' })).toBeVisible({ timeout: 15_000 });
    let row = page.getByRole('row').filter({ hasText: 'WF_RBAC' });
    await expect(row.getByRole('button', { name: /force.?unlock|sperre aufheben|entsperren/i })).toHaveCount(0);

    // Now Admin — re-route /me and reload.
    await asRole(page, ME_ADMIN);
    await page.reload();
    await expect(page.getByRole('button', { name: 'WF_RBAC' })).toBeVisible({ timeout: 15_000 });
    row = page.getByRole('row').filter({ hasText: 'WF_RBAC' });
    await expect(row.getByRole('button', { name: /force.?unlock|sperre aufheben|entsperren/i })).toBeVisible();
  });

  // ---------- 26.5 — DB-Admin (and Users/Audit) viewer only Admin ----------
  test('26.5 — Operator direct-nav to /database redirects away (admin-only route)', async ({ page }) => {
    await asRole(page, ME_OPERATOR);
    await page.goto('/database');
    // AdminOnly guard → <Navigate to="/" replace/>. We land on the dashboard, not the DB viewer.
    await expect(page).toHaveURL(/\/$|\/dashboard|localhost:\d+\/$/, { timeout: 15_000 });
    await expect(page).not.toHaveURL(/\/database/);
  });

  test('26.5b — Operator direct-nav to /users redirects away', async ({ page }) => {
    await asRole(page, ME_OPERATOR);
    await page.goto('/users');
    await expect(page).not.toHaveURL(/\/users/, { timeout: 15_000 });
  });

  test('26.5c — Admin sees the admin sidebar group (Users / Audit / Database / Backup)', async ({ page }) => {
    await asRole(page, ME_ADMIN);
    await page.goto('/workflows');
    const nav = page.getByRole('navigation');
    await expect(nav.getByRole('link', { name: /^users$|benutzer/i })).toBeVisible({ timeout: 15_000 });
    await expect(nav.getByRole('link', { name: /audit log|audit-log/i })).toBeVisible();
    await expect(nav.getByRole('link', { name: /^database$|datenbank/i })).toBeVisible();
    // Log (support-log) nav item — anchored so it never matches "Audit Log".
    await expect(nav.getByRole('link', { name: /^log$/i })).toBeVisible();
  });

  test('26.5d — Operator direct-nav to /support-log redirects away', async ({ page }) => {
    await asRole(page, ME_OPERATOR);
    await page.goto('/support-log');
    await expect(page).not.toHaveURL(/\/support-log/, { timeout: 15_000 });
  });
});
