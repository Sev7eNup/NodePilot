import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Maintenance-Windows E2E. A window gates when its targeted workflows may start (Blackout blocks /
 * AllowOnly permits while active). This page is the only frontend surface for the feature and is
 * otherwise untested at the UI level — these specs cover the CRUD round-trips that scripts and
 * admins rely on: list rendering, create (weekly blackout), edit, delete, and the Admin-only gate.
 *
 * Conventions mirror edit-lock.spec.ts: per-test `page.route()` mocks (no real backend) and
 * bilingual name regexes, because the preview build resolves i18n from the browser locale and
 * may render EN or DE. Day buttons (Mon/Tue/…) are NOT translated, so they match literally.
 */

const WF_ID = '11111111-1111-1111-1111-111111111111';

function windowJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'aaaaaaaa-0000-0000-0000-000000000001',
    name: 'Nightly Patch',
    description: null,
    isEnabled: true,
    mode: 'Blackout',
    scopeKind: 'Global',
    recurrence: 'Weekly',
    oneTimeStartUtc: null,
    oneTimeEndUtc: null,
    weeklyDaysMask: 0b0111110, // Mon-Fri
    weeklyStartMinuteOfDay: 22 * 60,
    weeklyEndMinuteOfDay: 2 * 60,
    cronExpression: null,
    durationMinutes: null,
    timeZoneId: 'UTC',
    targets: [] as Array<{ targetKind: string; targetId: string }>,
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    updatedBy: 'e2e-admin',
    ...overrides,
  };
}

// The dialog has no role="dialog" (role="presentation") and the name input has no associated
// <label htmlFor>, so we scope by the dialog heading's parent panel and pick fields by role.
function createPanel(page: Page) {
  return page
    .getByRole('heading', { name: /wartungsfenster anlegen|create maintenance window/i })
    .locator('..');
}
function editPanel(page: Page) {
  return page
    .getByRole('heading', { name: /wartungsfenster bearbeiten|edit maintenance window/i })
    .locator('..');
}

test.describe('Maintenance Windows', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // Secondary query the page fires for folder-target name resolution — empty by default.
    await page.route('**/api/shared-workflow-folders', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
  });

  test('renders the list with mode and scope', async ({ page }) => {
    // A workflow so the Workflows-scoped window resolves its target id to a human name.
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: WF_ID, name: 'Deploy Prod' }]),
      }),
    );
    await page.route('**/api/maintenance-windows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          windowJson({ id: 'win-global', name: 'Global Freeze', mode: 'Blackout', scopeKind: 'Global' }),
          windowJson({
            id: 'win-wf',
            name: 'Deploy AllowOnly',
            mode: 'AllowOnly',
            scopeKind: 'Workflows',
            targets: [{ targetKind: 'Workflow', targetId: WF_ID }],
          }),
        ]),
      }),
    );

    await page.goto('/maintenance-windows');

    await expect(page.getByText('Global Freeze')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('Deploy AllowOnly')).toBeVisible();
    // Mode badges (bilingual).
    await expect(page.getByText(/blackout/i).first()).toBeVisible();
    await expect(page.getByText(/nur erlaubt|allow only/i).first()).toBeVisible();
    // Global scope renders the "all workflows" marker; the workflow-scoped row resolves its target.
    await expect(page.getByText(/alle workflows|all workflows/i).first()).toBeVisible();
    await expect(page.getByText('Deploy Prod')).toBeVisible();
  });

  test('creates a weekly blackout window', async ({ page }) => {
    const rows: ReturnType<typeof windowJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    await page.route('**/api/maintenance-windows', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        const created = windowJson({ id: 'created-1', ...(postedBody as object) });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/maintenance-windows');

    // Empty state first.
    await expect(page.getByText(/noch keine wartungsfenster|no maintenance windows yet/i)).toBeVisible({
      timeout: 15_000,
    });

    await page.getByRole('button', { name: /neues fenster|new window/i }).click();

    const panel = createPanel(page);
    await expect(panel).toBeVisible();
    await panel.getByRole('textbox').first().fill('Nightly Patch E2E');
    // Weekly is the default recurrence; select Monday so the form is valid.
    await panel.getByRole('button', { name: 'Mon', exact: true }).click();
    await panel.getByRole('button', { name: /^anlegen$|^create$/i }).click();

    // Request body carries the form translation: Mon == bit 1 == mask 2; 22:00 == 1320; 02:00 == 120.
    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'Nightly Patch E2E',
      mode: 'Blackout',
      recurrence: 'Weekly',
      scopeKind: 'Global',
      weeklyDaysMask: 2,
      weeklyStartMinuteOfDay: 1320,
      weeklyEndMinuteOfDay: 120,
    });

    // Dialog closes and the refetched list shows the new row.
    await expect(panel).toHaveCount(0);
    await expect(page.getByText('Nightly Patch E2E')).toBeVisible();
  });

  test('edits a window name', async ({ page }) => {
    const id = 'edit-1';
    const rows = [windowJson({ id, name: 'Before Rename' })];
    let putBody: Record<string, unknown> | null = null;

    await page.route('**/api/maintenance-windows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/maintenance-windows/${id}`, (route) => {
      putBody = route.request().postDataJSON();
      rows[0] = windowJson({ id, ...(putBody as object) });
      return route.fulfill({ status: 204 });
    });

    await page.goto('/maintenance-windows');
    await expect(page.getByText('Before Rename')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /bearbeiten|edit/i }).click();

    const panel = editPanel(page);
    await expect(panel).toBeVisible();
    const nameInput = panel.getByRole('textbox').first();
    await expect(nameInput).toHaveValue('Before Rename'); // prefilled from the row
    await nameInput.fill('After Rename');
    await panel.getByRole('button', { name: /^aktualisieren$|^update$/i }).click();

    await expect.poll(() => putBody).not.toBeNull();
    expect(putBody).toMatchObject({ name: 'After Rename' });

    await expect(panel).toHaveCount(0);
    await expect(page.getByText('After Rename')).toBeVisible();
  });

  test('deletes a window after confirm', async ({ page }) => {
    const id = 'del-1';
    let rows = [windowJson({ id, name: 'Doomed Window' })];
    let deleteHit = false;

    await page.route('**/api/maintenance-windows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/maintenance-windows/${id}`, (route) => {
      deleteHit = true;
      rows = [];
      return route.fulfill({ status: 204 });
    });

    await page.goto('/maintenance-windows');
    await expect(page.getByText('Doomed Window')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /löschen|delete/i }).click();
    // The page deletes via the in-app ConfirmHost modal — confirm with OK.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => deleteHit).toBe(true);
    // Refetch returns the now-empty list → row gone, empty state back.
    await expect(page.getByText('Doomed Window')).toHaveCount(0);
    await expect(page.getByText(/noch keine wartungsfenster|no maintenance windows yet/i)).toBeVisible();
  });

  test('Viewer cannot mutate: no new/edit/delete controls', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ username: 'viewer', role: 'Viewer' }),
      }),
    );
    await page.route('**/api/maintenance-windows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([windowJson({ id: 'ro-1', name: 'Read Only Row' })]),
      }),
    );

    await page.goto('/maintenance-windows');

    await expect(page.getByText('Read Only Row')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /neues fenster|new window/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /bearbeiten|edit/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /löschen|delete/i })).toHaveCount(0);
  });
});
