import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 71 — LiveConsole: Filter & Pause (lines 3885-3909).
 *
 * The LiveConsole (filter field + "Errors only" toggle + Live/Paused pause-toggle) lives
 * inside the Live tab → LiveOverview → Console sub-tab, and only MOUNTS when there is an
 * active `liveExecution`. That object is fed exclusively by `useWorkflowSignalR(id)`. In the
 * hermetic harness SignalR negotiation is mocked to 404 (see fixtures/mockApi.ts), so no live
 * execution ever arrives and the LiveConsole's filter/errors/pause controls cannot render.
 *
 * Therefore:
 *   - We ASSERT what IS reachable hermetically: the bottom Execution panel and its tab bar
 *     (Live / History / Output / Watch) render, and the Live tab shows the documented
 *     "No active execution" empty state.
 *   - We test.skip the filter / errors-only / pause-toggle scenarios with a reason — those
 *     require a streaming SignalR execution which the mocked-404 harness deliberately denies.
 *
 * Hermetic: page.route mocks only. SPA renders ENGLISH under Playwright.
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

    // The bottom Execution panel + its four tabs are present without any live run.
    await expect(page.getByRole('button').filter({ hasText: /^Live/ }).first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button').filter({ hasText: /^History/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /^Output/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /^Watch/ }).first()).toBeVisible();

    // The Live tab (default) shows the "No active execution" empty state because SignalR is mocked off.
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
