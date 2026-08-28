import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md part 63 — workflow list view UI.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * The preview/dev build resolves i18n from the browser locale (renders EN here), so
 * selectors use bilingual regexes or stable title attributes that exist in both locales.
 *
 * Covers:
 *   - 63.1 — column-header sorting (Name ascending, then descending, then switch to Updated).
 *            The page sorts client-side (WorkflowsPage.handleSort/sortedWorkflows), so the
 *            assertion is on the visual row order.
 *   - 63.2 — enable/disable toggle in a list row: POST /enable|/disable fires and the
 *            mutation's setQueryData flips isEnabled without a refetch.
 *   - 63.3 — delete confirm dialog (in-app ConfirmHost modal): cancel leaves the row, OK fires
 *            DELETE /api/workflows/{id} and removes the row after invalidation.
 *   Plus status badges (productive / disabled / locked-by-other) and trigger badges.
 */

const ME = MOCK_USER; // Admin, id matches authStore.userId for lock-owner comparisons
const OTHER_USER = '00000000-0000-0000-0000-0000000000ff';

const ID_ALPHA = 'a1111111-1111-1111-1111-111111111111';
const ID_BRAVO = 'b2222222-2222-2222-2222-222222222222';
const ID_CHARLIE = 'c3333333-3333-3333-3333-333333333333';

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: ID_ALPHA,
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

/**
 * The workflow name buttons currently rendered in the table body, in top-to-bottom DOM order.
 * Each first-column cell renders the workflow name inside a button, so this is the visual
 * sort order.
 */
async function rowNames(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    // Addressed by testid rather than column position: the leading selection column shifts
    // the indexes, and a positional selector would silently return [] instead of failing.
    const cells = Array.from(document.querySelectorAll('table tbody tr [data-testid="workflow-row-name"]'));
    return cells.map((c) => (c.textContent ?? '').trim());
  });
}

test.describe('Workflows-Listenansicht UI (Teil 63)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME) }),
    );
  });

  // ---------- 63.1 — column-header sorting ----------
  test('63.1 — clicking Name header sorts asc, second click desc; Updated header re-sorts', async ({ page }) => {
    // Three rows in an unsorted server order, so the default view does not already match the
    // alphabetical order and the click is shown to reorder.
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          workflow({ id: ID_BRAVO, name: 'Bravo', updatedAt: '2026-03-01T00:00:00.000Z' }),
          workflow({ id: ID_ALPHA, name: 'Alpha', updatedAt: '2026-01-01T00:00:00.000Z' }),
          workflow({ id: ID_CHARLIE, name: 'Charlie', updatedAt: '2026-02-01T00:00:00.000Z' }),
        ]),
      }),
    );

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'Alpha' })).toBeVisible({ timeout: 15_000 });

    // Server (unsorted) order.
    expect(await rowNames(page)).toEqual(['Bravo', 'Alpha', 'Charlie']);

    // Click Name for ascending order.
    await page.getByRole('button', { name: /^Name$/i }).click();
    await expect.poll(() => rowNames(page)).toEqual(['Alpha', 'Bravo', 'Charlie']);

    // Second click on Name for descending order.
    await page.getByRole('button', { name: /^Name$/i }).click();
    await expect.poll(() => rowNames(page)).toEqual(['Charlie', 'Bravo', 'Alpha']);

    // Switch to the Updated header: ascending by updatedAt (Alpha=Jan, Charlie=Feb, Bravo=Mar).
    await page.getByRole('button', { name: /^Updated$|Geändert/i }).click();
    await expect.poll(() => rowNames(page)).toEqual(['Alpha', 'Charlie', 'Bravo']);
  });

  // ---------- 63.2 — enable/disable toggle ----------
  test('63.2 — disable toggle fires POST /disable and optimistically flips the badge', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow({ isEnabled: true })]) }),
    );
    let disableHit = false;
    await page.route(`**/api/workflows/${ID_ALPHA}/disable`, (route) => {
      disableHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    // Status badge starts "Productive" (enabled, no lock).
    await expect(row.getByText(/^Productive$|Produktiv/i)).toBeVisible();

    // Click the power toggle (title = "Disable workflow" / "Workflow deaktivieren").
    await row.getByRole('button', { name: /disable workflow|workflow deaktivieren/i }).click();

    // POST /disable fired and the optimistic setQueryData flipped the badge to Disabled.
    await expect.poll(() => disableHit, { timeout: 10_000 }).toBe(true);
    await expect(row.getByText(/^Disabled$|Deaktiviert/i)).toBeVisible({ timeout: 10_000 });
  });

  test('63.2b — enable toggle fires POST /enable on a disabled row', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow({ isEnabled: false })]) }),
    );
    let enableHit = false;
    await page.route(`**/api/workflows/${ID_ALPHA}/enable`, (route) => {
      enableHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row.getByText(/^Disabled$|Deaktiviert/i)).toBeVisible();

    await row.getByRole('button', { name: /enable workflow|workflow aktivieren/i }).click();
    await expect.poll(() => enableHit, { timeout: 10_000 }).toBe(true);
    await expect(row.getByText(/^Productive$|Produktiv/i)).toBeVisible({ timeout: 10_000 });
  });

  // ---------- 63.3 — delete confirm dialog ----------
  test('63.3 — delete: cancel keeps the row, accept fires DELETE and removes it', async ({ page }) => {
    let workflows = [workflow()];
    let deleteHit = false;
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflows) }),
    );
    await page.route(`**/api/workflows/${ID_ALPHA}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        workflows = []; // next list refetch (post-invalidation) returns empty
        return route.fulfill({ status: 204, body: '' });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflow()) });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    // First: cancel the in-app ConfirmHost modal; the workflow must stay.
    await row.getByRole('button', { name: /^delete$|löschen/i }).click();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await page.waitForTimeout(300);
    expect(deleteHit).toBe(false);
    await expect(page.getByRole('button', { name: 'Alpha' })).toBeVisible();

    // Second: confirm with OK; DELETE fires, the list invalidates, the row disappears.
    await row.getByRole('button', { name: /^delete$|löschen/i }).click();
    await page.getByRole('button', { name: 'OK' }).click();
    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByRole('button', { name: 'Alpha' })).toHaveCount(0, { timeout: 10_000 });
  });

  // ---------- status badges + trigger badges ----------
  test('renders status badges (productive / disabled / locked-by-other) and trigger badges', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          workflow({ id: ID_ALPHA, name: 'Alpha', isEnabled: true, triggerTypes: ['scheduleTrigger'] }),
          workflow({ id: ID_BRAVO, name: 'Bravo', isEnabled: false }),
          workflow({
            id: ID_CHARLIE,
            name: 'Charlie',
            isEnabled: false,
            checkedOutByUserId: OTHER_USER,
            checkedOutByUserName: 'colleague',
            checkedOutAt: '2026-06-18T08:00:00.000Z',
          }),
        ]),
      }),
    );

    await page.goto('/workflows');
    const alpha = page.getByRole('row').filter({ hasText: 'Alpha' });
    const bravo = page.getByRole('row').filter({ hasText: 'Bravo' });
    const charlie = page.getByRole('row').filter({ hasText: 'Charlie' });
    await expect(alpha).toBeVisible({ timeout: 15_000 });

    await expect(alpha.getByText(/^Productive$|Produktiv/i)).toBeVisible();
    await expect(bravo.getByText(/^Disabled$|Deaktiviert/i)).toBeVisible();
    // Locked-by-other badge shows the lock owner's name.
    await expect(charlie.getByText('colleague')).toBeVisible();

    // Trigger badge for the scheduled workflow (TRIGGER_BADGE_META label, schedule-flavored).
    await expect(alpha.getByText(/schedule|zeitplan|cron|geplant/i)).toBeVisible();
  });

  test('duplicate + export-as-JSON row buttons are present for an editable row', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );
    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row.getByRole('button', { name: /duplicate|duplizieren/i })).toBeVisible();
    await expect(row.getByRole('button', { name: /export as json|als json exportieren/i })).toBeVisible();
  });

  // ---------- Run-from-row (the list-level Play button) ----------
  test('Run Now on a param-less productive workflow fires POST /execute directly (no dialog)', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );
    let executeBody: unknown = undefined;
    await page.route(`**/api/workflows/${ID_ALPHA}/execute`, (route) => {
      executeBody = route.request().postDataJSON();
      return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ executionId: '00000000-0000-0000-0000-0000000000ee' }) });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByRole('button', { name: /run now|jetzt ausführen|ausführen/i }).click();

    // The empty definition has no manualTrigger, so the run goes straight through, no dialog.
    await expect.poll(() => executeBody, { timeout: 10_000 }).not.toBeUndefined();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('Run Now on a workflow with manualTrigger params opens the dialog; entered values POST as parameters', async ({ page }) => {
    const defWithParam = JSON.stringify({
      nodes: [{
        id: 'trg', type: 'trigger', position: { x: 0, y: 0 },
        data: { activityType: 'manualTrigger', config: { parameters: [{ name: 'env', type: 'string', required: true, default: 'dev' }] } },
      }],
      edges: [],
    });
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow({ definitionJson: defWithParam })]) }),
    );
    let executeBody: unknown = undefined;
    await page.route(`**/api/workflows/${ID_ALPHA}/execute`, (route) => {
      executeBody = route.request().postDataJSON();
      return route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ executionId: '00000000-0000-0000-0000-0000000000ef' }) });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'Alpha' });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByRole('button', { name: /run now|jetzt ausführen|ausführen/i }).click();

    // The RunWorkflowDialog opens, prefilled from the parameter's default.
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    const envInput = dialog.getByRole('textbox');
    await expect(envInput).toHaveValue('dev');
    await envInput.fill('prod');

    await dialog.getByRole('button', { name: /^run$|^ausführen$/i }).click();

    await expect.poll(() => executeBody, { timeout: 10_000 }).toEqual({ parameters: { env: 'prod' } });
  });
});
