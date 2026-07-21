import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 37 — Diagnostics / Support-Log (lines 3072-3100) and Teil 76.4 (the
 * SupportEventsTable viewer). The structured support-event projection is surfaced on its own
 * standalone, Admin-only page at `/support-log` (moved out of Settings → System so it's directly
 * reachable during an incident) via the SupportEventsTable (table view, default) plus a
 * plain-text file-tail toggle.
 *
 * These specs drive that UI:
 *  - 37.1 — rows render with eventType / workflowName / traceId / timestamp.
 *  - 37.2 — the EventType filter re-queries with `?eventType=…` and the table reflects it.
 *  - 76.4 — the Plain-Text toggle swaps to the raw file-tail view.
 *
 * Role-gating for the `/support-log` route (AdminOnly redirect + hidden sidebar link) is
 * covered by `rbac.spec.ts` (Teil 26.5) alongside the other admin-only routes.
 *
 * Hermetic: predicate catch-all from fixtures/mockApi.ts. The diagnostics endpoint is mocked
 * per test; query-string variants are matched with a trailing `**` so the filter round-trip
 * resolves.
 *
 * SPA renders ENGLISH under Playwright → bilingual /regex/i + role/attribute selectors.
 */

function eventRow(overrides: Record<string, unknown> = {}) {
  return {
    id: crypto.randomUUID(),
    timestamp: '2026-06-18T10:15:30.123Z',
    level: 2,
    eventType: 'EXECUTION_STARTED',
    message: 'Execution started',
    workflowId: '11111111-1111-1111-1111-111111111111',
    workflowName: 'Deploy Prod',
    executionId: '22222222-2222-2222-2222-222222222222',
    executionShort: '2222222',
    stepId: null,
    stepLabel: null,
    activityType: null,
    userName: 'e2e-admin',
    userId: null,
    traceId: 'abc123trace',
    spanId: 'span001',
    propertiesJson: null,
    ...overrides,
  };
}

function page200(items: ReturnType<typeof eventRow>[], hasMore = false) {
  return JSON.stringify({ items, nextCursor: null, hasMore });
}

/** Navigate straight to the standalone Support-Log page and wait for the heading. */
async function openSupportLog(page: Page) {
  await page.goto('/support-log');
  await expect(page.getByRole('heading', { name: /support-log/i })).toBeVisible({ timeout: 15_000 });
}

test.describe('Diagnostics / Support-Log (Teil 37)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('37.1 — support events render with type, workflow, trace and timestamp', async ({ page }) => {
    await page.route('**/api/diagnostics/support-events**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: page200([
          eventRow({ eventType: 'EXECUTION_STARTED', workflowName: 'Deploy Prod' }),
          eventRow({ eventType: 'EXECUTION_FAILED', level: 4, message: 'step blew up', workflowName: 'Nightly Backup' }),
        ]),
      }),
    );

    await openSupportLog(page);

    // Column headers prove the structured table mounted.
    await expect(page.getByText(/^workflow$/i).first()).toBeVisible();
    await expect(page.getByText(/^message$/i).first()).toBeVisible();

    // Each row renders as a <button>; assert by the row's workflow + message text (these never
    // appear in the EventType filter <option> list, unlike the bare eventType codes).
    const startedRow = page.getByRole('button').filter({ hasText: 'Deploy Prod' });
    const failedRow = page.getByRole('button').filter({ hasText: 'Nightly Backup' });
    await expect(startedRow.first()).toBeVisible();
    await expect(failedRow.first()).toBeVisible();
    // eventType is rendered inside the row (scope to the row to avoid the <option> collision).
    await expect(startedRow.first().getByText('EXECUTION_STARTED')).toBeVisible();
    await expect(failedRow.first().getByText('EXECUTION_FAILED')).toBeVisible();
    await expect(page.getByText('step blew up')).toBeVisible();

    // Expanding a row reveals the per-event detail, including the traceId from the payload.
    await page.getByText('step blew up').click();
    await expect(page.getByText(/traceid/i).first()).toBeVisible();
    await expect(page.getByText('abc123trace')).toBeVisible();
  });

  test('37.2 — EventType filter re-queries the backend with ?eventType=', async ({ page }) => {
    const seenEventTypeParams: string[] = [];
    await page.route('**/api/diagnostics/support-events**', (route) => {
      const url = new URL(route.request().url());
      const et = url.searchParams.get('eventType');
      seenEventTypeParams.push(et ?? '');
      // When filtered to STEP_FAILED, only return that kind; otherwise the mixed set.
      const items =
        et === 'STEP_FAILED'
          ? [eventRow({ eventType: 'STEP_FAILED', level: 4, message: 'only failures', workflowName: 'Deploy Prod' })]
          : [
              eventRow({ eventType: 'EXECUTION_STARTED', message: 'the started one', workflowName: 'Deploy Prod' }),
              eventRow({ eventType: 'STEP_FAILED', level: 4, message: 'only failures', workflowName: 'Deploy Prod' }),
            ];
      return route.fulfill({ status: 200, contentType: 'application/json', body: page200(items) });
    });

    await openSupportLog(page);
    // The started event's row is initially present (assert by its unique message text).
    await expect(page.getByText('the started one')).toBeVisible({ timeout: 15_000 });

    // The first <select> in the filter bar is the EventType dropdown (option values are the
    // raw codes). Select STEP_FAILED → the query string carries eventType=STEP_FAILED.
    await page.getByRole('combobox').first().selectOption('STEP_FAILED');

    await expect.poll(() => seenEventTypeParams.includes('STEP_FAILED'), { timeout: 10_000 }).toBe(true);
    // The filtered table now shows only the failure row, the started event is gone.
    await expect(page.getByText('only failures')).toBeVisible();
    await expect(page.getByText('the started one')).toHaveCount(0);
  });

  test('76.4 — Plain-Text toggle swaps to the raw file-tail view', async ({ page }) => {
    await page.route('**/api/diagnostics/support-events**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: page200([eventRow()]) }),
    );
    // Plain-text mode tails the support-log file.
    await page.route('**/api/diagnostics/support-log**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          file: 'C:/NodePilot/logs/nodepilot-support-2026-06-18.log',
          lineCount: 2,
          lines: ['2026-06-18 10:15:30 [INFO] booted', '2026-06-18 10:15:31 [ERR ] kaboom'],
        }),
      }),
    );

    await openSupportLog(page);
    // The table view is the default; wait for the seeded row's message text.
    await expect(page.getByText('Execution started')).toBeVisible({ timeout: 15_000 });

    // Toggle to the plain-text (file) view.
    await page.getByRole('button', { name: /plain-text|datei/i }).click();

    // Raw file lines render, and the file path is shown.
    await expect(page.getByText(/nodepilot-support-2026-06-18\.log/)).toBeVisible();
    await expect(page.getByText(/kaboom/)).toBeVisible();
  });
});
