import { test, expect, type Page, type Route } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

// Hermetic spec for the Live-Ops Mission-Control view (/operations): real-time execution
// timeline (running + recently-finished bars), bar drill-down + cancel, and the next-fires
// departure board. All APIs are mocked via page.route; SignalR is 404-stubbed by
// installDefaultMocks so the page runs off the polled snapshot.

const json = (r: Route, body: unknown) =>
  r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

const MIN = 60_000;
const now = () => Date.now();

const GRAPH = () => ({
  nodes: [
    { workflowId: 'wf-1', name: 'Nightly Backup', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 1, lastStatus: null, callFrequency: 8, canRun: true, canEdit: true },
    { workflowId: 'wf-2', name: 'Cleanup Temp', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 0, lastStatus: 'Failed', callFrequency: 3, canRun: true, canEdit: true },
    { workflowId: 'wf-3', name: 'Staging Job', folderId: 'staging', folderPath: '/Staging', isEnabled: true, runningCount: 0, lastStatus: 'Succeeded', callFrequency: 1, canRun: true, canEdit: true },
  ],
  edges: [],
  running: [
    { executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(now() - 4 * MIN).toISOString() },
  ],
  recent: [
    { executionId: 'ex-2', workflowId: 'wf-2', status: 'Failed', startedAt: new Date(now() - 10 * MIN).toISOString(), completedAt: new Date(now() - 8 * MIN).toISOString() },
  ],
  density: [],
  meta: { overdueSeconds: 600, windowMinutes: 20, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
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
    { workflowId: 'wf-1', workflowName: 'Nightly Backup', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(now() + 30 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null, blockedByWindowName: null },
    { workflowId: 'wf-3', workflowName: 'Staging Job', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(now() + 10 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null, blockedByWindowName: 'Weekend Freeze' },
  ],
});

const EXEC_DETAIL = {
  id: 'ex-1', workflowId: 'wf-1', status: 'Running',
  startedAt: new Date(now() - 4 * MIN).toISOString(), completedAt: null,
  triggeredBy: 'schedule', errorMessage: null, traceId: null, spanId: null,
  returnData: null, inputParametersJson: null,
  stepsTotal: 0, stepsCompleted: 0, failedSteps: null,
};

async function mock(page: Page) {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => json(r, GRAPH()));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));
}

test('timeline bars render from the snapshots', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  // "Live-Ops" appears twice (TopBar page-title chip + the page <h1>); scope to main content.
  await expect(page.locator('#np-main-scroll').getByRole('heading', { name: 'Live-Ops' })).toBeVisible();

  // Timeline: a growing running bar (wf-1) and a settled failed bar (wf-2).
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  await expect(page.getByTitle(/Cleanup Temp · Failed/)).toBeVisible();

  // Copyable execution-id chips (8-char prefix in parens) behind each workflow name.
  await expect(page.getByText('(ex-1)')).toBeVisible();
  await expect(page.getByText('(ex-2)')).toBeVisible();
});

test('timeline bar opens the drilldown with cancel + open-in-editor', async ({ page }) => {
  let cancelHit = false;
  await mock(page);
  await page.route('**/api/executions/ex-1/cancel', (r) => { cancelHit = true; return json(r, {}); });

  await page.goto('/operations');
  await page.getByTitle(/Nightly Backup · Running/).click();

  await expect(page.getByRole('button', { name: 'Open in editor' })).toBeVisible();
  // The drilldown surfaces the execution (job) id with a copy button.
  await expect(page.getByLabel('Execution details').getByText('ex-1')).toBeVisible();
  await expect(page.getByLabel('Execution details').getByRole('button', { name: 'Copy' })).toBeVisible();
  await expect(page.getByText('schedule', { exact: true })).toBeVisible(); // triggeredBy from the detail fetch

  // exact: true — "Cancel all runs (1)" would otherwise match the same substring.
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
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

test('a start suppressed by a maintenance window is flagged, not hidden', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  const rows = page.getByRole('table').locator('tbody tr');
  // Staging Job fires in 10 min but a Blackout window will swallow it: it keeps its
  // sort position and swaps the countdown for the blackout marker.
  await expect(rows.nth(0)).toContainText('Staging Job');
  await expect(rows.nth(0)).toContainText('maintenance');
  await expect(rows.nth(0)).toHaveAttribute('title', /Weekend Freeze/);
  await expect(rows.nth(1)).not.toContainText('maintenance');
});

test('drilldown names the failed step, not just the error message', async ({ page }) => {
  await mock(page);
  await page.route('**/api/executions/ex-2', (r) => json(r, {
    id: 'ex-2', workflowId: 'wf-2', status: 'Failed',
    startedAt: new Date(now() - 6 * MIN).toISOString(),
    completedAt: new Date(now() - 5 * MIN).toISOString(),
    triggeredBy: 'manual', errorMessage: 'Step failed', traceId: null, spanId: null,
    returnData: null, inputParametersJson: null,
    stepsTotal: 9, stepsCompleted: 7,
    failedSteps: [{ stepId: 'step-7', stepName: 'Check Disk' }],
  }));
  await page.goto('/operations');

  await page.getByTitle(/Cleanup Temp · Failed/).click();
  await expect(page.getByText('Failed steps')).toBeVisible();
  await expect(page.getByText('Check Disk')).toBeVisible();
});

test('a run past the long-running threshold surfaces in the stuck strip', async ({ page }) => {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => json(r, {
    ...GRAPH(),
    // 3 hours old: clamped to the left edge of the 20-minute window, so on the bar alone it is
    // indistinguishable from a 21-minute run.
    running: [{ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(now() - 180 * MIN).toISOString(), stepsFinished: 3, lastCompletedStepName: 'Copy files', lastProgressAt: new Date(now() - 40 * MIN).toISOString(), activeStepCount: 0 }],
    meta: { overdueSeconds: 600, windowMinutes: 20, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false },
  }));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));

  await page.goto('/operations');

  const strip = page.getByLabel('Stuck / long-running');
  await expect(strip).toBeVisible();
  await expect(strip).toContainText('Nightly Backup');
  await expect(strip).toContainText('running for 3:00');
  // The distinguishing detail: long vs. stuck on ONE step.
  await expect(strip).toContainText('last step 40:0');
  await expect(strip).toContainText('Copy files');

  // The bar itself carries the overdue treatment and states its real start time.
  const bar = page.getByTitle(/Nightly Backup · Running/);
  await expect(bar).toHaveClass(/np-ops-bar--overdue/);
  await expect(bar).toContainText('‹');

  // Clicking the strip entry opens that run's drilldown.
  await strip.getByRole('button').first().click();
  await expect(page.getByLabel('Execution details')).toBeVisible();
});

test('no stuck strip while every run is younger than the threshold', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  await expect(page.getByLabel('Stuck / long-running')).toHaveCount(0);
});

test('quarantine confirms, then disables before cancelling all runs', async ({ page }) => {
  const order: string[] = [];
  await mock(page);
  await page.route('**/api/workflows/wf-1/disable', (r) => { order.push('disable'); return json(r, {}); });
  await page.route('**/api/workflows/wf-1/cancel-all', (r) => { order.push('cancel-all'); return json(r, { total: 2, signalled: 2 }); });

  await page.goto('/operations');
  await page.getByTitle(/Nightly Backup · Running/).click();
  await page.getByRole('button', { name: 'Quarantine' }).click();

  // Destructive action → confirm dialog first; nothing has been sent yet.
  // (ModalShell carries no role="dialog", so match on the confirm copy instead.)
  await expect(page.getByText(/Quarantine “Nightly Backup”\?/)).toBeVisible();
  expect(order).toEqual([]);

  await page.getByRole('button', { name: 'OK', exact: true }).click();

  // Order is load-bearing: cancelling first would let the still-armed trigger restart the runs.
  await expect.poll(() => order).toEqual(['disable', 'cancel-all']);
});

test('a workflow the caller may only read offers no incident actions', async ({ page }) => {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => json(r, {
    ...GRAPH(),
    nodes: GRAPH().nodes.map((n) => ({ ...n, canRun: false, canEdit: false })),
  }));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));

  await page.goto('/operations');
  await page.getByTitle(/Nightly Backup · Running/).click();

  await expect(page.getByRole('button', { name: 'Open in editor' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Cancel' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Quarantine' })).toHaveCount(0);
});

test('window selector re-requests the snapshot and freeze pins the view', async ({ page }) => {
  const windows: string[] = [];
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => {
    windows.push(new URL(r.request().url()).searchParams.get('windowMinutes') ?? '');
    return json(r, GRAPH());
  });
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));

  await page.goto('/operations');
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  expect(windows[0]).toBe('20');

  await page.getByLabel('Window').selectOption('240');
  await expect.poll(() => windows.at(-1)).toBe('240');

  // Freeze: loud badge, and the poll stops (no further requests land).
  await page.getByRole('button', { name: 'Freeze view' }).click();
  await expect(page.getByTestId('ops-frozen-badge')).toBeVisible();
  const atFreeze = windows.length;
  await page.waitForTimeout(6_000); // longer than the 5 s poll interval
  expect(windows.length).toBe(atFreeze);

  await page.getByRole('button', { name: 'Go live' }).click();
  await expect(page.getByTestId('ops-frozen-badge')).toHaveCount(0);
});

test('a window the bars cannot cover is filled with density, not left empty', async ({ page }) => {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => json(r, {
    ...GRAPH(),
    density: [
      { workflowId: 'wf-1', buckets: [{ bucketIndex: 4, total: 12, failed: 0, cancelled: 0 }] },
      { workflowId: 'wf-2', buckets: [{ bucketIndex: 6, total: 20, failed: 3, cancelled: 0 }] },
    ],
    meta: {
      overdueSeconds: 600, windowMinutes: 240,
      recentSinceUtc: new Date(now() - 240 * MIN).toISOString(),
      oldestReturnedCompletedAt: new Date(now() - 8 * MIN).toISOString(),
      recentTruncated: true, densityBucketSeconds: 300, densityCapped: false,
    },
  }));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));
  await page.route('**/api/executions/ex-1', (r) => json(r, EXEC_DETAIL));

  await page.goto('/operations');
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  // The track has to actually span 4 h for the aggregate to have anywhere to sit — the window
  // the user picked is what sets the visible span, the snapshot only fills it.
  await page.getByLabel('Window').selectOption('240');

  // The whole point: at 4 h the stretch older than the newest bars carries the run counts
  // instead of the hatched "nothing came back" band it used to show.
  await expect(page.getByTestId('ops-density-cell')).toHaveCount(2);
  await expect(page.getByTestId('ops-density-notice')).toContainText('32 finished runs');
  await expect(page.getByTestId('ops-density-notice')).toContainText('3 failed');
  await expect(page.getByTestId('ops-history-gap')).toHaveCount(0);
});

test('folder filter scopes timeline and departure board together', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();

  // Scope to /Staging: no bars in the window → idle hero; board only shows the staging trigger.
  await page.getByLabel('Folder').selectOption('staging');
  await expect(page.getByText('Nothing is running right now.')).toBeVisible();
  await expect(page.getByTitle(/Nightly Backup · Running/)).toHaveCount(0);
  const board = page.getByRole('table');
  await expect(board.locator('tbody tr')).toHaveCount(1);
  await expect(board).toContainText('Staging Job');

  // Back to /Prod: the running bar returns, staging trigger disappears from the board.
  await page.getByLabel('Folder').selectOption('prod');
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  await expect(board).not.toContainText('Staging Job');
});
