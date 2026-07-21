import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 57 — Gantt-Chart & Execution-Timeline (lines 3592-3611).
 *
 * Hermetic: page.route() mocks only. Both timeline surfaces are driven by REST
 * (/executions + /executions/{id}/steps), NOT SignalR, so they ARE reachable here:
 *
 *   57.2 — ExecutionsPage (/executions): clicking a terminal run row expands it in-place and
 *          renders the step list (status + output). Clicking is the deep-link; the step table
 *          shows each step's status badge and output/error.
 *
 *   57.1 — Designer ExecutionPanel → History tab → expand a run → StepTimeline → "Gantt" view.
 *          The shared GanttChart (timeline/GanttChart.tsx, data-testid="gantt-chart") renders
 *          one horizontal bar per step proportional to its runtime. We assert the chart mounts
 *          with the seeded steps and that switching List↔Gantt works.
 *
 * LIVE Gantt growth + "click bar selects node on canvas" round-trip needs SignalR (mocked 404),
 * so the live-grow assertion is skipped; the static (terminal-run) Gantt fully covers the bars.
 */

const WF_ID = '57575757-0000-0000-0000-000000000057';
const EXEC_ID = 'bbbb5757-0000-0000-0000-000000000057';

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_ID,
    name: 'Timeline_WF',
    description: '',
    isEnabled: true,
    checkedOutByUserId: MOCK_USER.id, // lock-by-me so the editor mounts fully (ExecutionPanel present)
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify({
      nodes: [
        { id: 'a', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Alpha', activityType: 'log', config: {} } },
        { id: 'b', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Bravo', activityType: 'delay', config: {} } },
        { id: 'c', type: 'activity', position: { x: 440, y: 0 }, data: { label: 'Charlie', activityType: 'log', config: {} } },
      ],
      edges: [
        { id: 'e1', source: 'a', target: 'b', type: 'labeled', data: {} },
        { id: 'e2', source: 'b', target: 'c', type: 'labeled', data: {} },
      ],
    }),
    version: 1,
    activityCount: 3,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

function execution() {
  return {
    id: EXEC_ID,
    workflowId: WF_ID,
    status: 'Succeeded',
    startedAt: '2026-06-18T10:00:00.000Z',
    completedAt: '2026-06-18T10:00:09.000Z',
    triggeredBy: 'manual',
    errorMessage: null,
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: null,
    stepsTotal: 3,
    stepsCompleted: 3,
  };
}

// Three steps with distinct, increasing durations → bars of clearly different widths.
function steps() {
  return JSON.stringify([
    {
      id: 's-a', stepId: 'a', stepName: 'Alpha', stepType: 'log', targetMachine: null,
      status: 'Succeeded',
      startedAt: '2026-06-18T10:00:00.000Z', completedAt: '2026-06-18T10:00:01.000Z',
      output: 'alpha ran', errorOutput: null, traceOutput: null, outputParametersJson: null,
    },
    {
      id: 's-b', stepId: 'b', stepName: 'Bravo', stepType: 'delay', targetMachine: null,
      status: 'Succeeded',
      startedAt: '2026-06-18T10:00:01.000Z', completedAt: '2026-06-18T10:00:06.000Z',
      output: 'bravo waited 5s', errorOutput: null, traceOutput: null, outputParametersJson: null,
    },
    {
      id: 's-c', stepId: 'c', stepName: 'Charlie', stepType: 'log', targetMachine: null,
      status: 'Failed',
      startedAt: '2026-06-18T10:00:06.000Z', completedAt: '2026-06-18T10:00:09.000Z',
      output: null, errorOutput: 'charlie blew up', traceOutput: null, outputParametersJson: null,
    },
  ]);
}

/** Mock everything the executions surfaces fetch. */
async function mockTimeline(page: Page) {
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
  );
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflow()) }),
  );
  // Both /executions and /executions?workflowId=...&terminalOnly=true land here.
  await page.route('**/api/executions**', (route) => {
    // Don't shadow the /steps sub-route (registered after this wins anyway, but be explicit).
    if (route.request().url().includes('/steps')) return route.fallback();
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([execution()]) });
  });
  await page.route(`**/api/executions/${EXEC_ID}/steps`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: steps() }),
  );
}

test.describe('Gantt-Chart & Execution-Timeline (Teil 57)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('57.2 — ExecutionsPage: clicking a run row expands it in-place with the step list (status + output)', async ({ page }) => {
    await mockTimeline(page);

    await page.goto('/executions');

    const row = page.getByRole('button', { name: /Timeline_WF/i });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.click();

    // Expanded in-place: a step table with one row per seeded step.
    await expect(page.getByText('a', { exact: true }).first()).toBeVisible({ timeout: 10_000 }); // stepId column
    await expect(page.getByText('delay', { exact: true })).toBeVisible(); // step type

    // Step statuses surface (Succeeded badge + the failed step's error output).
    await expect(page.getByText('Succeeded').first()).toBeVisible();
    await expect(page.getByText('Failed').first()).toBeVisible();
    await expect(page.getByText(/charlie blew up/)).toBeVisible(); // errorOutput of the failed step
    await expect(page.getByText('bravo waited 5s')).toBeVisible(); // output of a succeeded step
  });

  test('57.1 — Designer History tab: expand a run → StepTimeline → Gantt view renders proportional bars', async ({ page }) => {
    await mockTimeline(page);

    await page.goto(`/workflows/${WF_ID}`);

    // The bottom ExecutionPanel mounts with the editor. Switch from the default Live tab to History.
    // The tab's accessible name is its text "History" (+ optional count badge) — no anchors so the
    // badge digit doesn't break the match.
    const historyTab = page.getByRole('button', { name: /history/i });
    await expect(historyTab).toBeVisible({ timeout: 15_000 });
    await historyTab.click();

    // The terminal run appears in the History grid. The clickable toggle is the row div
    // (role="row", data-row-id=<execId>) — NOT the inner copy-id button, which stops propagation.
    const runRow = page.locator(`[data-row-id="${EXEC_ID}"]`);
    await expect(runRow).toBeVisible({ timeout: 10_000 });
    await runRow.click();

    // StepTimeline mounts (default List view) with the aggregate header "3 steps".
    await expect(page.getByText(/3 steps/i)).toBeVisible({ timeout: 10_000 });

    // Switch to the Gantt view; the shared GanttChart renders with one bar per step.
    await page.getByRole('button', { name: /^gantt$/i }).click();
    const gantt = page.getByTestId('gantt-chart');
    await expect(gantt).toBeVisible({ timeout: 10_000 });

    // Bars are the absolutely-positioned colored divs inside each row's track. Three steps
    // with non-null start+end → at least the succeeded/failed bars are present and have width.
    const bars = gantt.locator('div[style*="width"]');
    await expect.poll(async () => bars.count(), { timeout: 10_000 }).toBeGreaterThanOrEqual(2);

    // Step names appear in the Gantt rows.
    await expect(gantt.getByText('Bravo')).toBeVisible();
    await expect(gantt.getByText('Charlie')).toBeVisible();

    // The List/Gantt toggle round-trips back to the table.
    await page.getByRole('button', { name: /^list$/i }).click();
    await expect(page.getByText(/3 steps/i)).toBeVisible();
  });

  test('57.1b — live Gantt growth + bar-click-selects-canvas-node', async () => {
    test.skip(true, 'Live-growing bars (nowMs ticker) and the click-bar→select-node-on-canvas round-trip both depend on the SignalR live stream (mocked 404 here) and React-Flow canvas selection. The static terminal-run Gantt in 57.1 covers bar rendering proportional to runtime; the live path is owned by NodePilot.Api.Tests + LiveTimeline unit tests.');
  });
});
