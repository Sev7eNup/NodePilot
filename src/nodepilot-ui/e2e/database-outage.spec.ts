import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Database outage surface: banner, toast suppression and recovery.
 *
 * Hermetic: the SPA learns about an outage exclusively from `/healthz/database` (memory-only on
 * the real backend, mocked here) plus the DATABASE_* 503 bodies on `/api/*`. That makes the whole
 * feature drivable from `page.route` with no backend: override the health route to `unavailable`,
 * let the api catch-alls answer 503, and the banner, pill and toast behaviour is the same as
 * against a stopped PostgreSQL.
 *
 * The banner is mounted in App.tsx as a sibling of the router, outside the layout shell, because
 * `/workflows/:id` (the designer) renders a bare Outlet without the shell, and the designer is
 * where losing work matters. The last test pins that mount point.
 */

const OUTAGE_HEALTH = {
  status: 'unavailable',
  sinceUtc: '2026-08-07T07:00:00.000Z',
  reason: 'Unreachable',
};

const OUTAGE_503 = {
  code: 'DATABASE_UNAVAILABLE',
  message: 'The database is not reachable right now. NodePilot keeps checking and resumes on its own as soon as it answers.',
  retryAfterSeconds: 15,
  reason: 'Unreachable',
  retryable: true,
};

async function mockHealth(page: Page, body: object) {
  await page.route('**/healthz/database', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }),
  );
}

const banner = (page: Page) => page.getByRole('alert').filter({ hasText: /database unreachable|datenbank nicht erreichbar/i });

test.describe('Teil 82 — database outage', () => {
  test('82.1 banner appears while the health probe reports unavailable — and API 503s raise no toast storm', async ({ page }) => {
    await installDefaultMocks(page);
    await mockHealth(page, OUTAGE_HEALTH);
    // Every list endpoint answers the outage contract, the way the real backend would.
    // /api/workflows is the WorkflowsPage query (meta.silentError, never toasted); machines,
    // executions and the dashboard pollers run through the global QueryCache.onError, which has
    // to stay silent during an outage so the banner is the only message.
    for (const path of ['**/api/workflows', '**/api/machines', '**/api/executions**', '**/api/dashboard/**']) {
      await page.route(path, (route) =>
        route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify(OUTAGE_503) }),
      );
    }

    await page.goto('/workflows');

    await expect(banner(page)).toBeVisible({ timeout: 15_000 });
    // The early wording promises automatic recovery; the copy escalates with elapsed time,
    // which a hermetic run never reaches.
    await expect(page.getByText(/resumes on its own|automatisch wieder auf/i)).toBeVisible();

    // No toast storm: the banner owns this message. Give the queries a moment to fail.
    await page.waitForTimeout(1500);
    await expect(page.getByTestId('toast-error')).toHaveCount(0);
  });

  test('82.2 recovery clears the banner, refetches, and says so once', async ({ page }) => {
    await installDefaultMocks(page);
    await mockHealth(page, OUTAGE_HEALTH);

    let workflowListRequests = 0;
    await page.route('**/api/workflows', (route) => {
      workflowListRequests += 1;
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify(OUTAGE_503) });
    });

    await page.goto('/workflows');
    await expect(banner(page)).toBeVisible({ timeout: 15_000 });
    const requestsDuringOutage = workflowListRequests;

    // The database comes back: the health probe flips (the SPA polls every 3 s during an
    // outage), the banner clears, every query refetches and one success toast marks the moment.
    // The recovery handler keeps counting requests because route registration is last-wins: a
    // non-counting 200 handler would freeze the counter and the refetch assertion would time out.
    await page.route('**/api/workflows', (route) => {
      workflowListRequests += 1;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });
    await mockHealth(page, { status: 'ok', sinceUtc: null, reason: null });

    await expect(banner(page)).toHaveCount(0, { timeout: 15_000 });
    await expect(page.getByTestId('toast-success')).toBeVisible({ timeout: 15_000 });
    await expect
      .poll(() => workflowListRequests, { timeout: 15_000 })
      .toBeGreaterThan(requestsDuringOutage);
  });

  test('82.3 banner renders inside the workflow designer (bare-Outlet route)', async ({ page }) => {
    const wfId = '82e28282-8282-4282-8282-828282828282';
    await installDefaultMocks(page);
    await mockHealth(page, OUTAGE_HEALTH);
    await page.route(`**/api/workflows/${wfId}`, (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          id: wfId,
          name: 'Outage visibility',
          isEnabled: false,
          version: 1,
          checkedOutByUserId: MOCK_USER.id,
          checkedOutByUserName: MOCK_USER.username,
          checkedOutAt: '2026-06-01T00:00:00.000Z',
          definitionJson: JSON.stringify({
            nodes: [{
              id: 'trigger-1', type: 'activity', position: { x: 80, y: 80 },
              data: { label: 'Manual', activityType: 'manualTrigger', config: {} },
            }],
            edges: [],
          }),
        }),
      }),
    );

    await page.goto(`/workflows/${wfId}`);

    // The designer route bypasses the layout shell, so a banner mounted inside the shell would
    // be invisible where unsaved work is at stake. Mounted from App, it overlays the canvas.
    await expect(page.locator('.react-flow__node')).toHaveCount(1, { timeout: 15_000 });
    await expect(banner(page)).toBeVisible();
  });
});
