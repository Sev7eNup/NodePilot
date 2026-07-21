import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 57 (companion to execution-timeline.spec.ts) — the everyday
 * ExecutionsPage *list* interactions an operator performs to find a run: the search
 * box, the status quick-filter chips, the per-workflow dropdown, column sorting, the
 * summary chips, the Refresh button and the per-row "open workflow" action.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * The preview build renders the UI in ENGLISH, so selectors use the EN strings /
 * bilingual regexes / stable title attributes. Desktop viewport (1440px) → the table
 * (not the mobile card list) renders, so row order is readable from the DOM.
 */

const WF_BACKUP = 'aaaa1111-0000-0000-0000-000000000001';
const WF_REPORT = 'bbbb2222-0000-0000-0000-000000000002';
const WF_CLEANUP = 'cccc3333-0000-0000-0000-000000000003';

const E_BACKUP = 'e0000001-0000-0000-0000-000000000001';
const E_REPORT = 'e0000002-0000-0000-0000-000000000002';
const E_CLEANUP = 'e0000003-0000-0000-0000-000000000003';

function workflows() {
  return [
    { id: WF_BACKUP, name: 'Backup Job' },
    { id: WF_REPORT, name: 'Nightly Report' },
    { id: WF_CLEANUP, name: 'Cleanup Task' },
  ];
}

/**
 * One terminal run per workflow with deliberately distinct durations so a sort by the
 * Duration column reorders the rows unambiguously:
 *   Backup Job   — Succeeded, 2 s
 *   Nightly Report — Failed,   9 s (carries an errorMessage we can search for)
 *   Cleanup Task — Cancelled,  5 s
 * All start at the same instant so the default "Started desc" sort keeps the input order.
 */
function executions() {
  const base = {
    triggeredBy: 'manual', traceId: null, spanId: null, returnData: null,
    inputParametersJson: null, parentExecutionId: null, parentWorkflowName: null,
    failedSteps: [] as unknown[], stepsTotal: 1, stepsCompleted: 1,
  };
  return [
    { ...base, id: E_BACKUP, workflowId: WF_BACKUP, status: 'Succeeded', startedByUsername: 'alice',
      startedAt: '2026-06-18T10:00:00.000Z', completedAt: '2026-06-18T10:00:02.000Z', errorMessage: null },
    { ...base, id: E_REPORT, workflowId: WF_REPORT, status: 'Failed', startedByUsername: 'bob', triggeredBy: 'schedule',
      startedAt: '2026-06-18T10:00:00.000Z', completedAt: '2026-06-18T10:00:09.000Z', errorMessage: 'disk full on D:' },
    { ...base, id: E_CLEANUP, workflowId: WF_CLEANUP, status: 'Cancelled', startedByUsername: 'alice',
      startedAt: '2026-06-18T10:00:00.000Z', completedAt: '2026-06-18T10:00:05.000Z', errorMessage: null },
  ];
}

/** Mock /workflows (names) + /executions (list). Returns a getter for the list-request count. */
async function mockExecutions(page: Page) {
  let listRequests = 0;
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflows()) }),
  );
  await page.route('**/api/executions**', (route) => {
    // /executions/{id}/steps is registered separately; never shadow it here.
    if (route.request().url().includes('/steps')) return route.fallback();
    listRequests += 1;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(executions()) });
  });
  return () => listRequests;
}

const tableEl = (page: Page) => page.getByRole('table');

/** Workflow-name cell text per table row, top-to-bottom — reflects the active sort order. */
async function rowNames(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('div[role="row"]'))
      .filter((r) => r.querySelector('div[role="gridcell"]'));
    return rows.map((r) => {
      const cell = r.querySelectorAll('div[role="gridcell"]')[1]; // 2nd cell = Workflow
      const btn = cell?.querySelector('button');
      return (btn?.textContent ?? '').trim();
    });
  });
}

test.describe('Executions list UI (Teil 57)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('renders all runs, the summary chips and the "X of Y" footer count', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');

    const table = tableEl(page);
    await expect(table.getByText('Backup Job')).toBeVisible({ timeout: 15_000 });
    await expect(table.getByText('Nightly Report')).toBeVisible();
    await expect(table.getByText('Cleanup Task')).toBeVisible();

    // Summary chips: 1 succeeded + 1 failed → success rate 50% (cancelled excluded from the rate).
    await expect(page.getByText('Success rate')).toBeVisible();
    await expect(page.getByText('50%')).toBeVisible();
    await expect(page.getByText('Ø Duration')).toBeVisible();

    // Footer reflects the unfiltered total.
    await expect(page.getByText('3 of 3 executions')).toBeVisible();
  });

  test('search narrows to a single run; a non-matching term shows the empty state; clearing restores', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');

    const table = tableEl(page);
    await expect(table.getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    const search = page.getByPlaceholder(/search workflow, trigger, id or error/i);
    await search.fill('Nightly');
    await expect(table.getByText('Nightly Report')).toBeVisible();
    await expect(table.getByText('Backup Job')).toHaveCount(0);
    // count=1 → i18n picks the singular ("…execution"); "execution" is a substring of both forms.
    await expect(page.getByText('1 of 3 execution')).toBeVisible();

    // A term that matches nothing surfaces the "no match" empty-state (not "no executions yet").
    await search.fill('zzz-nothing-here');
    await expect(page.getByText(/no executions match the current filters/i)).toBeVisible();

    // Clearing the box brings every run back.
    await search.fill('');
    await expect(table.getByText('Backup Job')).toBeVisible();
    await expect(table.getByText('Cleanup Task')).toBeVisible();
  });

  test('search also matches on a run\'s error message', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');

    const table = tableEl(page);
    await expect(table.getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    await page.getByPlaceholder(/search workflow, trigger, id or error/i).fill('disk full');
    await expect(table.getByText('Nightly Report')).toBeVisible();
    await expect(table.getByText('Backup Job')).toHaveCount(0);
    await expect(table.getByText('Cleanup Task')).toHaveCount(0);
  });

  test('the "Failed" status chip narrows to the failed run; "All" restores the list', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');

    const table = tableEl(page);
    await expect(table.getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /^Failed$/ }).click();
    await expect(table.getByText('Nightly Report')).toBeVisible();
    await expect(table.getByText('Backup Job')).toHaveCount(0);
    await expect(table.getByText('Cleanup Task')).toHaveCount(0);
    // count=1 → i18n picks the singular ("…execution"); "execution" is a substring of both forms.
    await expect(page.getByText('1 of 3 execution')).toBeVisible();

    await page.getByRole('button', { name: /^All$/ }).click();
    await expect(table.getByText('Backup Job')).toBeVisible();
    await expect(table.getByText('Cleanup Task')).toBeVisible();
  });

  test('the per-workflow dropdown filters the list to one workflow', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');

    const table = tableEl(page);
    await expect(table.getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    await page.locator('select').selectOption({ label: 'Backup Job' });
    await expect(table.getByText('Backup Job')).toBeVisible();
    await expect(table.getByText('Nightly Report')).toHaveCount(0);
    await expect(table.getByText('Cleanup Task')).toHaveCount(0);

    // Back to "All workflows" restores every run.
    await page.locator('select').selectOption({ label: 'All workflows' });
    await expect(table.getByText('Nightly Report')).toBeVisible();
  });

  test('sorting by the Duration column toggles descending then ascending', async ({ page }) => {
    await mockExecutions(page);
    await page.goto('/executions');
    await expect(tableEl(page).getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    // Default sort is Started desc; all three share a start instant → stable input order.
    expect(await rowNames(page)).toEqual(['Backup Job', 'Nightly Report', 'Cleanup Task']);

    // First Duration click → descending (longest first): Nightly 9s, Cleanup 5s, Backup 2s.
    await page.getByRole('button', { name: /^Duration$/ }).click();
    await expect.poll(() => rowNames(page)).toEqual(['Nightly Report', 'Cleanup Task', 'Backup Job']);

    // Second click → ascending (shortest first).
    await page.getByRole('button', { name: /^Duration$/ }).click();
    await expect.poll(() => rowNames(page)).toEqual(['Backup Job', 'Cleanup Task', 'Nightly Report']);
  });

  test('the Refresh button re-requests the executions list', async ({ page }) => {
    const count = await mockExecutions(page);
    await page.goto('/executions');
    await expect(tableEl(page).getByText('Backup Job')).toBeVisible({ timeout: 15_000 });

    const before = count();
    await page.locator('button[title="Reload"]').click();
    await expect.poll(() => count(), { timeout: 10_000 }).toBeGreaterThan(before);
  });

  test('the per-row "Open workflow" button navigates to that workflow', async ({ page }) => {
    await mockExecutions(page);
    // The editor that the open-button targets must resolve so navigation lands cleanly.
    await page.route(`**/api/workflows/${WF_BACKUP}`, (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          id: WF_BACKUP, name: 'Backup Job', description: '', isEnabled: true,
          checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
          checkedOutAt: '2026-06-01T00:00:00.000Z',
          definitionJson: '{"nodes":[],"edges":[]}', version: 1,
        }),
      }),
    );

    await page.goto('/executions');
    const row = page.getByRole('row').filter({ hasText: 'Backup Job' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByRole('button', { name: /open workflow/i }).click();
    await expect(page).toHaveURL(new RegExp(`/workflows/${WF_BACKUP}`));
  });
});
