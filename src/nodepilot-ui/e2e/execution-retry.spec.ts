import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 49 — Execution Retry & Cancel-All.
 *
 * 49.1's full API contract (HTTP status, new executionId, trigger "retry:<id>", EXECUTION_RETRIED
 * audit code) is asserted server-side in NodePilot.Api.Tests / NodePilot.Cli.Tests. What the SPA
 * CAN drive is the retry affordance on ExecutionsPage (added in the executions-page overhaul): the
 * per-row Retry button POSTs /executions/{id}/retry. 49.5 covers that UI path; the deeper contract
 * checks below stay API-only skips:
 *   49.1  POST /api/executions/{id}/retry           → 202, new executionId, trigger "retry:<id>"
 *   49.2  POST /api/executions/{id}/retry (Running) → 400 naming the status
 *   49.3  POST /api/workflows/{id}/cancel-all       → 200, cancelledCount ≥ 1, all → Cancelled
 *   49.4  POST /api/workflows/{id}/cancel-all (idle)→ 200, cancelledCount: 0
 *
 * /workflows/{id}/cancel-all still has no frontend surface (np CLI `np workflow cancel-all` + REST
 * only). The status-guard (49.2) and zero-count (49.4) paths are server-side. They stay explicit
 * skips so the Teil-49 rows remain visible in the e2e report rather than silently absent.
 */

const RETRY_WF_ID = '49494949-0000-0000-0000-000000000049';
const RETRY_EXEC_ID = 'aaaa4949-0000-0000-0000-000000000049';

test.describe('Execution Retry & Cancel-All (Teil 49)', () => {
  test('49.5 — ExecutionsPage Retry button fires POST /executions/{id}/retry', async ({ page }) => {
    await installDefaultMocks(page);

    await page.route('**/api/workflows', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([{
          id: RETRY_WF_ID, name: 'Retry_WF', description: '', isEnabled: true,
          checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null,
          definitionJson: '{"nodes":[],"edges":[]}', version: 1, activityCount: 0,
          triggerTypes: [], createdAt: '2026-06-01T00:00:00.000Z', updatedAt: '2026-06-01T00:00:00.000Z',
        }]),
      }),
    );
    // A single terminal (Failed) run — retry is offered on terminal rows.
    await page.route('**/api/executions**', (route) => {
      if (route.request().url().includes('/steps')) return route.fallback();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([{
          id: RETRY_EXEC_ID, workflowId: RETRY_WF_ID, status: 'Failed',
          startedAt: '2026-06-18T10:00:00.000Z', completedAt: '2026-06-18T10:00:03.000Z',
          triggeredBy: 'manual', errorMessage: 'boom', traceId: null, spanId: null,
          returnData: null, inputParametersJson: null, startedByUsername: 'admin',
          stepsTotal: 1, stepsCompleted: 1, failedSteps: [{ stepId: 'n1', stepName: 'Boom' }],
        }]),
      });
    });

    let retriedId: string | null = null;
    await page.route(`**/api/executions/${RETRY_EXEC_ID}/retry`, (route) => {
      retriedId = RETRY_EXEC_ID;
      return route.fulfill({
        status: 202, contentType: 'application/json',
        body: JSON.stringify({ id: 'exec-retry-new', workflowId: RETRY_WF_ID, status: 'Pending', startedAt: '2026-06-18T11:00:00.000Z', completedAt: null, triggeredBy: `retry:${RETRY_EXEC_ID}`, errorMessage: null, traceId: null, spanId: null, returnData: null, inputParametersJson: null }),
      });
    });

    await page.goto('/executions');

    const retryBtn = page.getByRole('button', { name: /Retry execution/i });
    await expect(retryBtn).toBeVisible({ timeout: 15_000 });
    await retryBtn.click();
    // Confirm via the in-app ConfirmHost dialog (native confirm() was retired).
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => retriedId, { timeout: 10_000 }).toBe(RETRY_EXEC_ID);
  });

  test('49.1 — POST /executions/{id}/retry → 202 + new executionId + retry trigger', async () => {
    test.skip(true, 'Full API contract (202 + executionId + retry:<id> trigger + audit code) is server-side. UI trigger path covered by 49.5; contract by NodePilot.Api.Tests (Executions controller) + np CLI tests.');
  });

  test('49.2 — retry on a Running execution → 400', async () => {
    test.skip(true, 'API-only endpoint; status-guard (400 on non-terminal) is server-side. Covered by NodePilot.Api.Tests.');
  });

  test('49.3 — POST /workflows/{id}/cancel-all → 200 + cancelledCount ≥ 1', async () => {
    test.skip(true, 'API-only endpoint; no frontend UI calls /workflows/{id}/cancel-all. Covered by NodePilot.Api.Tests (Workflows controller) + np CLI tests.');
  });

  test('49.4 — cancel-all with no running executions → 200 + cancelledCount 0', async () => {
    test.skip(true, 'API-only endpoint; zero-count path is server-side. Covered by NodePilot.Api.Tests.');
  });
});
