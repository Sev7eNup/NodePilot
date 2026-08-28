import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Part 71 — LiveConsole filter and pause.
 *
 * The LiveConsole, with its filter field, "Errors only" toggle and Live/Paused control, sits in
 * the Console sub-tab of LiveOverview under the Live tab and mounts only while a `liveExecution`
 * is active. That object comes solely from `useWorkflowSignalR(id)`, and the hermetic harness
 * answers SignalR negotiation with 404 (see fixtures/mockApi.ts), so those controls never render.
 *
 * What stays reachable is asserted here: the bottom Execution panel with its Live, History,
 * Output and Watch tabs, plus the "No active execution" empty state. The filter, errors-only and
 * pause scenarios are skipped with a reason, since they need a streaming SignalR execution.
 *
 * Hermetic: page.route mocks only. The SPA renders English under Playwright.
 */

const WF_ID = 'e7171717-7171-7171-7171-717171717171';

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Console', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify({
      nodes: [
        { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
          data: { label: 'A', activityType: 'runScript', config: { script: 'x' } } },
      ],
      edges: [],
    }),
    version: 1,
  });
}

async function openEditor(page: Page) {
  await seedExpertMode(page);
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('LiveConsole — Filter & Pause (Teil 71)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('71.0 — Execution panel renders the Live/History/Output/Watch tab bar', async ({ page }) => {
    await openEditor(page);

    // The bottom Execution panel and its four tabs render without any live run.
    await expect(page.getByRole('button').filter({ hasText: /^Live/ }).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button').filter({ hasText: /^History/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /^Output/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /^Watch/ }).first()).toBeVisible();

    // The Live tab is the default and shows the empty state, because SignalR is mocked off.
    await expect(page.getByText(/No active execution|keine aktive/i).first()).toBeVisible();
  });

  test('71.1 — log filter narrows the console lines', async () => {
    test.skip(true, 'LiveConsole only mounts with a streaming SignalR execution; SignalR is mocked 404 in the hermetic harness, so no live lines/filter render.');
  });

  test('71.2 — errors-only toggle + error count badge', async () => {
    test.skip(true, 'Errors-only toggle lives in the LiveConsole, which requires a live SignalR execution unavailable in the mocked-404 hermetic harness.');
  });

  test('71.3 — pause auto-scroll toggle (Live ↔ Paused)', async () => {
    test.skip(true, 'The pause/auto-scroll toggle lives in the LiveConsole, which requires a live SignalR execution unavailable in the mocked-404 hermetic harness.');
  });
});
