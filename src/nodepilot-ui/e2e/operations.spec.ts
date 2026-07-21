import { test, expect, type Page, type Route } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

// Hermetic spec for the Live-Ops Mission-Control view (/operations): pulse header, real-time
// execution timeline, event ticker, health rail and next-fires departure board. All APIs are
// mocked via page.route; SignalR is 404-stubbed by installDefaultMocks so the page runs off
// the polled snapshot (ticker stays in its empty state).

const json = (r: Route, body: unknown) =>
  r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

const MIN = 60_000;
const now = () => Date.now();

const GRAPH = () => ({
  nodes: [
    { workflowId: 'wf-1', name: 'Nightly Backup', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 1, lastStatus: null, callFrequency: 8 },
    { workflowId: 'wf-2', name: 'Cleanup Temp', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 0, lastStatus: 'Failed', callFrequency: 3 },
    { workflowId: 'wf-3', name: 'Staging Job', folderId: 'staging', folderPath: '/Staging', isEnabled: true, runningCount: 0, lastStatus: 'Succeeded', callFrequency: 1 },
  ],
  edges: [],
  running: [
    { executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(now() - 4 * MIN).toISOString() },
  ],
  recent: [
    { executionId: 'ex-2', workflowId: 'wf-2', status: 'Failed', startedAt: new Date(now() - 10 * MIN).toISOString(), completedAt: new Date(now() - 8 * MIN).toISOString() },
  ],
  capabilities: { canCancel: true },
});

const STATS = () => ({
  machinesTotal: 3, machinesReachable: 2,
  pendingCount: 0, runningCount: 1, longRunningCount: 0,
  clusterRole: null,
  healthHeartbeats: [
    { serviceName: 'Scheduler', lastHeartbeatAt: new Date(now()).toISOString(), expectedIntervalSeconds: 60, status: null, isStale: false },
    { serviceName: 'NotificationDispatcher', lastHeartbeatAt: new Date(now() - 10 * MIN).toISOString(), expectedIntervalSeconds: 60, status: null, isStale: true },
  ],
  armedTriggers: [
    { workflowId: 'wf-1', workflowName: 'Nightly Backup', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(now() + 30 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null },
    { workflowId: 'wf-3', workflowName: 'Staging Job', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(now() + 10 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null },
  ],
});

const EXEC_DETAIL = {
  id: 'ex-1', workflowId: 'wf-1', status: 'Running',
  startedAt: new Date(now() - 4 * MIN).toISOString(), completedAt: null,
  triggeredBy: 'schedule', errorMessage: null, traceId: null, spanId: null,
  returnData: null, inputParametersJson: null,
};

async function mock(page: Page) {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph', (r) => json(r, GRAPH()));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));
}

test('pulse header, timeline bars, ticker empty state and health rail render from the snapshots', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  // "Live-Ops" appears twice (TopBar page-title chip + the page <h1>); scope to main content.
  await expect(page.locator('#np-main-scroll').getByRole('heading', { name: 'Live-Ops' })).toBeVisible();

  // Pulse header: recent failure + stale heartbeat + machine down → Degraded.
  await expect(page.locator('.np-ops-pulse')).toContainText('Degraded');

  // Timeline: a growing running bar (wf-1) and a settled failed bar (wf-2).
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  await expect(page.getByTitle(/Cleanup Temp · Failed/)).toBeVisible();

  // Ticker: SignalR is stubbed out → empty state.
  await expect(page.getByText('No live events yet.')).toBeVisible();

  // Health rail: machines fraction + stale service badge ("stale" also appears inside the
  // pulse-header reason text, so match the badge's exact text).
  await expect(page.getByText('2/3')).toBeVisible();
  await expect(page.getByText('stale', { exact: true })).toBeVisible();
});

test('timeline bar opens the drilldown with cancel + open-in-editor', async ({ page }) => {
  let cancelHit = false;
  await mock(page);
  await page.route('**/api/executions/ex-1/cancel', (r) => { cancelHit = true; return json(r, {}); });

  await page.goto('/operations');
  await page.getByTitle(/Nightly Backup · Running/).click();

  await expect(page.getByRole('button', { name: 'Open in editor' })).toBeVisible();
  await expect(page.getByText('schedule', { exact: true })).toBeVisible(); // triggeredBy from the detail fetch

  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect.poll(() => cancelHit).toBe(true);
});

test('departure board lists next fires time-sorted', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  const board = page.getByRole('table');
  await expect(board).toBeVisible();
  const rows = board.locator('tbody tr');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText('Staging Job');   // +10 min
  await expect(rows.nth(1)).toContainText('Nightly Backup'); // +30 min
});

test('folder filter scopes timeline and departure board together', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();

  // Scope to /Staging: no bars in the window → idle hero; board only shows the staging trigger.
  await page.getByRole('combobox').selectOption('staging');
  await expect(page.getByText('Nothing is running right now.')).toBeVisible();
  await expect(page.getByTitle(/Nightly Backup · Running/)).toHaveCount(0);
  const board = page.getByRole('table');
  await expect(board.locator('tbody tr')).toHaveCount(1);
  await expect(board).toContainText('Staging Job');

  // Back to /Prod: the running bar returns, staging trigger disappears from the board.
  await page.getByRole('combobox').selectOption('prod');
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  await expect(board).not.toContainText('Staging Job');
});
