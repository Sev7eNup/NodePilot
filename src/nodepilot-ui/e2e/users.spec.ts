import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Part 15 — Admin & User Management.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts. The SPA renders
 * EN under Playwright, so selectors use roles/attributes + bilingual regexes.
 *
 * The /users page is Admin-only (App.tsx <AdminOnly> guard redirects non-Admin → "/"). The
 * default MOCK_USER is Admin, so the page mounts. Self-protection compares by `username`
 * (authStore.username ← me.username == 'e2e-admin'); the row whose username matches gets a
 * "you" badge and a disabled delete button.
 *
 * Maps to:
 *   - 15.1 — Create user (Admin): New User dialog → Create → POST /api/users.
 *   - 15.3 — Password reset: Reset-Password dialog → PUT /api/users/{id} { password }.
 *   - 15.4 — Delete user + self-/last-admin protection: DELETE /api/users/{id}, native confirm(),
 *            self-row delete disabled, last-admin 4xx surfaced in the UI.
 */

const OP_ID = '00000000-0000-0000-0000-0000000000a1';
const VIEWER_ID = '00000000-0000-0000-0000-0000000000a2';

type UserRow = {
  id: string;
  username: string;
  role: 'Admin' | 'Operator' | 'Viewer';
  isActive: boolean;
  createdAt: string;
};

function userRow(overrides: Partial<UserRow> = {}): UserRow {
  return {
    id: OP_ID,
    username: 'operator1',
    role: 'Operator',
    isActive: true,
    createdAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

// The admin's own row — username must equal MOCK_USER.username so the self-protection fires.
function selfRow(overrides: Partial<UserRow> = {}): UserRow {
  return userRow({ id: MOCK_USER.id, username: MOCK_USER.username, role: 'Admin', ...overrides });
}

test.describe('Admin & User-Management (Teil 15)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page); // MOCK_USER = Admin → page mounts
  });

  test('15.1 — create new user posts username/role/password', async ({ page }) => {
    const rows: UserRow[] = [selfRow()];
    let postedBody: { username?: string; role?: string; password?: string } | null = null;

    await page.route('**/api/users', (route) => {
      if (route.request().method() === 'POST') {
        postedBody = route.request().postDataJSON();
        const created = userRow({ id: 'created-1', username: postedBody?.username, role: postedBody?.role as UserRow['role'] });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/users');
    await expect(page.getByRole('button', { name: /new user|neuer benutzer/i })).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /new user|neuer benutzer/i }).click();

    // Dialog (role="presentation"): scope by the "New User" heading's panel.
    const dialog = page.getByRole('heading', { name: /new user|neuer benutzer/i }).locator('..');
    await expect(dialog).toBeVisible();

    await dialog.getByRole('textbox').first().fill('operator1');                 // Username
    await dialog.locator('input[type="password"]').first().fill('SecurePass123!'); // Password (≥8)
    await dialog.getByRole('combobox').selectOption('Operator');                  // Role select

    await dialog.getByRole('button', { name: /^(create|erstellen|anlegen)$/i }).click();

    await expect.poll(() => postedBody, { timeout: 10_000 }).not.toBeNull();
    expect(postedBody).toMatchObject({ username: 'operator1', role: 'Operator', password: 'SecurePass123!' });

    // Dialog closes; refetched list shows the new user.
    await expect(dialog).toHaveCount(0);
    await expect(page.getByText('operator1')).toBeVisible();
  });

  test('15.1b — Create button stays disabled until the form is valid', async ({ page }) => {
    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([selfRow()]) }),
    );

    await page.goto('/users');
    await page.getByRole('button', { name: /new user|neuer benutzer/i }).click();

    const dialog = page.getByRole('heading', { name: /new user|neuer benutzer/i }).locator('..');
    const create = dialog.getByRole('button', { name: /^(create|erstellen|anlegen)$/i });
    await expect(create).toBeDisabled(); // empty form

    await dialog.getByRole('textbox').first().fill('shortpw');
    await dialog.locator('input[type="password"]').first().fill('1234567'); // 7 chars < 8
    await expect(create).toBeDisabled();

    await dialog.locator('input[type="password"]').first().fill('12345678'); // now valid
    await expect(create).toBeEnabled();
  });

  test('15.3 — reset password issues PUT with the new password', async ({ page }) => {
    const target = userRow({ id: OP_ID, username: 'operator1' });
    let putBody: { password?: string } | null = null;

    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([selfRow(), target]) }),
    );
    await page.route(`**/api/users/${OP_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(target) });
    });

    await page.goto('/users');
    await expect(page.getByText('operator1')).toBeVisible({ timeout: 15_000 });

    // Reset-Password is the key icon button (title "Reset password").
    const row = page.getByRole('row').filter({ hasText: 'operator1' });
    await row.getByRole('button', { name: /reset password|passwort zurücksetzen/i }).click();

    const dialog = page.getByRole('heading', { name: /reset password|passwort zurücksetzen/i }).locator('..');
    await expect(dialog).toBeVisible();
    const pwFields = dialog.locator('input[type="password"]');
    await pwFields.nth(0).fill('BrandNewPass1!');
    await pwFields.nth(1).fill('BrandNewPass1!'); // confirm must match
    await dialog.getByRole('button', { name: /reset password|passwort zurücksetzen/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ password: 'BrandNewPass1!' });
  });

  test('15.4 — delete a user after in-app confirm', async ({ page }) => {
    const target = userRow({ id: VIEWER_ID, username: 'viewer1', role: 'Viewer' });
    let rows: UserRow[] = [selfRow(), target];
    let deleteHit = false;

    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/users/${VIEWER_ID}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        rows = [selfRow()];
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(target) });
    });

    await page.goto('/users');
    await expect(page.getByText('viewer1')).toBeVisible({ timeout: 15_000 });

    const row = page.getByRole('row').filter({ hasText: 'viewer1' });
    await row.getByRole('button', { name: /^delete$|löschen/i }).click();
    // In-app ConfirmHost modal — confirm with OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText('viewer1')).toHaveCount(0);
  });

  test('15.4b — own account: "you" badge + delete button disabled (self-protection)', async ({ page }) => {
    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([selfRow()]) }),
    );

    await page.goto('/users');
    // "you" badge identifies the current user's row.
    await expect(page.getByText(/^you$|^du$/i)).toBeVisible({ timeout: 15_000 });

    const selfRowLoc = page.getByRole('row').filter({ hasText: MOCK_USER.username });
    // Delete button on the self row is disabled — cannot delete own account.
    await expect(selfRowLoc.getByRole('button', { name: /cannot delete self|delete|löschen/i })).toBeDisabled();
  });

  test('15.4c — last-admin delete is rejected and the UI surfaces the 4xx message', async ({ page }) => {
    // A *second* admin so the delete button is enabled (not self-protected), but the server
    // refuses the delete because it's the last admin. The UI surfaces the failure as an
    // error toast (toast-error).
    const otherAdmin = userRow({ id: 'admin-2', username: 'admin2', role: 'Admin' });

    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([selfRow(), otherAdmin]) }),
    );
    await page.route('**/api/users/admin-2', (route) => {
      if (route.request().method() === 'DELETE') {
        return route.fulfill({
          status: 400,
          contentType: 'application/json',
          body: JSON.stringify({ message: 'Cannot delete the last admin' }),
        });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(otherAdmin) });
    });

    await page.goto('/users');
    await expect(page.getByText('admin2')).toBeVisible({ timeout: 15_000 });

    const row = page.getByRole('row').filter({ hasText: 'admin2' });
    await row.getByRole('button', { name: /^delete$|löschen/i }).click();
    // Confirm the in-app ConfirmHost modal, then the 4xx message surfaces as a toast-error.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect(page.getByTestId('toast-error')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('toast-error')).toContainText(/last admin/i);
  });

  test('15.2 — editing a user changes role + active via PUT /users/{id}', async ({ page }) => {
    const target = userRow({ id: OP_ID, username: 'operator1', role: 'Operator', isActive: true });
    let putBody: { role?: string; isActive?: boolean } | null = null;

    await page.route('**/api/users', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([selfRow(), target]) }),
    );
    await page.route(`**/api/users/${OP_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...target, ...putBody }) });
      }
      return route.fallback();
    });

    await page.goto('/users');
    const row = page.getByRole('row').filter({ hasText: 'operator1' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    // Open the edit dialog (Pencil, title "Edit role / active").
    await row.getByRole('button', { name: /edit role|rolle.*aktiv|bearbeiten/i }).click();
    const dialog = page.getByRole('heading', { name: /edit user:|benutzer bearbeiten/i }).locator('..');
    await expect(dialog).toBeVisible();

    // Promote Operator → Admin. (Save is disabled until something actually changes.)
    await dialog.locator('select').selectOption('Admin');
    await dialog.getByRole('button', { name: /^save$|^speichern$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ role: 'Admin', isActive: true });
  });

  test('15.5 — search filters the user list; a non-matching term shows the empty state', async ({ page }) => {
    await page.route('**/api/users', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          selfRow(),
          userRow({ id: OP_ID, username: 'operator1', role: 'Operator' }),
          userRow({ id: VIEWER_ID, username: 'viewer1', role: 'Viewer' }),
        ]),
      }),
    );

    await page.goto('/users');
    await expect(page.getByText('operator1')).toBeVisible({ timeout: 15_000 });

    const search = page.getByPlaceholder(/search by user|suche nach benutzer/i);
    await search.fill('viewer1');
    await expect(page.getByText('viewer1')).toBeVisible();
    await expect(page.getByText('operator1')).toHaveCount(0);

    await search.fill('nobody-xyz');
    await expect(page.getByText(/no user matches the current search|kein benutzer/i)).toBeVisible();
  });
});
