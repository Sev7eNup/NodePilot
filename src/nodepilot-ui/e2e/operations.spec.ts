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
  meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
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

async function mockDensity(page: Page) {
  await installDefaultMocks(page);
  await page.route('**/api/operations/graph*', (r) => {
    const windowMinutes = Number(new URL(r.request().url()).searchParams.get('windowMinutes') ?? 30);
    return json(r, {
      ...GRAPH(),
      density: [
        {
          workflowId: 'wf-1',
          buckets: [
            { bucketIndex: 4, total: 12, failed: 0, cancelled: 0 },
            { bucketIndex: 5, total: 8, failed: 0, cancelled: 0 },
          ],
        },
        { workflowId: 'wf-2', buckets: [{ bucketIndex: 6, total: 20, failed: 3, cancelled: 0 }] },
      ],
      meta: {
        overdueSeconds: 600,
        windowMinutes,
        recentSinceUtc: new Date(now() - windowMinutes * MIN).toISOString(),
        oldestReturnedCompletedAt: new Date(now() - 8 * MIN).toISOString(),
        recentTruncated: true,
        densityBucketSeconds: windowMinutes === 60 ? 75 : 37,
        densityCapped: false,
      },
    });
  });
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
    // 3 hours old: clamped to the left edge of the 30-minute window, so on the bar alone it is
    // indistinguishable from a 21-minute run.
    running: [{ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(now() - 180 * MIN).toISOString(), stepsFinished: 3, lastCompletedStepName: 'Copy files', lastProgressAt: new Date(now() - 40 * MIN).toISOString(), activeStepCount: 0 }],
    meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false },
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
  expect(await page.getByLabel('Window').locator('option').evaluateAll(
    (options) => options.map((option) => (option as HTMLOptionElement).value),
  )).toEqual(['30', '60']);
  expect(windows[0]).toBe('30');

  await page.getByLabel('Window').selectOption('60');
  await expect.poll(() => windows.at(-1)).toBe('60');

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
  await mockDensity(page);

  await page.goto('/operations');
  await expect(page.getByTitle(/Nightly Backup · Running/)).toBeVisible();
  // The track has to actually span the wider window for the aggregate to have anywhere to sit —
  // the window the user picked is what sets the visible span, the snapshot only fills it.
  await page.getByLabel('Window').selectOption('60');

  // The whole point: at 1 h the stretch older than the newest bars carries the run counts
  // instead of the hatched "nothing came back" band it used to show.
  const cells = page.getByTestId('ops-density-cell');
  await expect(cells).toHaveCount(3);
  await expect(page.getByTestId('ops-density-notice')).toContainText('40 finished runs');
  await expect(page.getByTestId('ops-density-notice')).toContainText('3 failed');
  await expect(page.getByTestId('ops-history-gap')).toHaveCount(0);

  // Screen-reader contract: the interval and count are exposed as an image name, while density
  // stays out of the keyboard order because it has no action to invoke.
  const announced = page.getByRole('img', { name: /12 runs/ });
  await expect(announced).toBeVisible();
  await expect(announced).not.toHaveAttribute('tabindex');

  // The marks that keep the aggregate from reading as one long run — asserted in a real browser
  // with real layout, because that is where the flat slab actually manifested. One baseline per
  // density lane; only wf-2's slice holds failures, so only it gets a rug under the line.
  await expect(page.getByTestId('ops-density-axis')).toHaveCount(2);
  await expect(page.getByTestId('ops-density-rug')).toHaveCount(1);
  const column = await cells.first().boundingBox();
  expect(column!.height).toBeLessThan(22);

  // Consecutive buckets remain distinct columns instead of merging into the old solid slab.
  const nextColumn = await cells.nth(1).boundingBox();
  expect(nextColumn!.x - (column!.x + column!.width)).toBeGreaterThan(0);
});

test('timeline keyboard navigation is one tab stop and opens the active run', async ({ page }) => {
  await mock(page);
  await page.goto('/operations');

  const track = page.getByTestId('ops-track');
  const board = page.getByRole('region', { name: 'Next starts' });
  const bars = track.locator('[id^="ops-bar-"]');
  await expect(track).toHaveAttribute('tabindex', '0');
  await expect(bars).toHaveCount(2);
  for (let i = 0; i < await bars.count(); i++) await expect(bars.nth(i)).toHaveAttribute('tabindex', '-1');

  await track.focus();
  const first = await track.getAttribute('aria-activedescendant');
  await track.press('ArrowRight');
  await expect(track).not.toHaveAttribute('aria-activedescendant', first!);
  await track.press('ArrowLeft');
  await expect(track).toHaveAttribute('aria-activedescendant', first!);
  await track.press('End');
  await expect(track).not.toHaveAttribute('aria-activedescendant', first!);
  await track.press('Home');
  await expect(track).toHaveAttribute('aria-activedescendant', first!);
  await track.press('ArrowDown');
  await expect(track).not.toHaveAttribute('aria-activedescendant', first!);
  await track.press('ArrowUp');
  await expect(track).toHaveAttribute('aria-activedescendant', first!);

  await track.press('Space');
  await expect(page.getByLabel('Execution details')).toBeVisible();
  await page.getByRole('button', { name: 'Close' }).click();
  await track.focus();
  await track.press('Enter');
  await expect(page.getByLabel('Execution details')).toBeVisible();
  await page.getByRole('button', { name: 'Close' }).click();

  await track.focus();
  await page.keyboard.press('Tab');
  await expect(board).toBeFocused();
});

test('a capped lane keeps every run and the time axis aligned at phone width', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await installDefaultMocks(page);
  const busy = GRAPH();
  await page.route('**/api/operations/graph*', (r) => json(r, {
    ...busy,
    nodes: [{ ...busy.nodes[0], workflowId: 'wf-busy', name: 'Busy Workflow', runningCount: 13 }],
    running: Array.from({ length: 13 }, (_, i) => ({
      executionId: `busy-${i}`,
      workflowId: 'wf-busy',
      status: 'Running',
      startedAt: new Date(now() - 5 * MIN).toISOString(),
    })),
    recent: [],
    density: [],
  }));
  await page.route('**/api/stats/dashboard*', (r) => json(r, STATS()));

  await page.goto('/operations');

  await expect(page.locator('[id^="ops-bar-busy-"]')).toHaveCount(13);
  const cappedMarker = page.getByTestId('ops-lane-capped');
  await expect(cappedMarker).toHaveCount(1);
  await expect(cappedMarker).toHaveAttribute(
    'aria-label',
    /some bars share a row/i,
  );

  const track = await page.getByTestId('ops-track').boundingBox();
  const axis = await page.getByTestId('ops-time-axis').boundingBox();
  const labels = await page.locator('.np-ops-lane-labels').boundingBox();
  expect(track!.width).toBeGreaterThanOrEqual(120);
  expect(labels!.width).toBeCloseTo(140, 0);
  expect(Math.abs(axis!.x - track!.x)).toBeLessThanOrEqual(1);
});

for (const theme of ['light', 'dark'] as const) {
  for (const windowMinutes of [30, 60] as const) {
    test(`density marks remain visible in ${theme} at ${windowMinutes} minutes`, async ({ page }) => {
      await page.addInitScript((selectedTheme) => {
        localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: selectedTheme }, version: 0 }));
      }, theme);
      await mockDensity(page);
      await page.goto('/operations');
      if (windowMinutes === 60) await page.getByLabel('Window').selectOption('60');

      await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark')))
        .toBe(theme === 'dark');
      const cell = page.getByTestId('ops-density-cell').first();
      const baseline = page.getByTestId('ops-density-axis').first();
      const rug = page.getByTestId('ops-density-rug');
      await expect(cell).toBeVisible();
      await expect(rug).toBeVisible();

      const styles = await page.evaluate(() => {
        const style = (selector: string) => getComputedStyle(document.querySelector(selector)!);
        return {
          column: style('.np-ops-density').backgroundColor,
          baseline: style('.np-ops-density-axis').borderTopColor,
          baselineStyle: style('.np-ops-density-axis').borderTopStyle,
          rug: style('.np-ops-density-rug').backgroundColor,
        };
      });
      expect(styles.column).not.toBe('rgba(0, 0, 0, 0)');
      expect(styles.baseline).not.toBe('rgba(0, 0, 0, 0)');
      expect(styles.baselineStyle).toBe('dashed');
      expect(styles.rug).not.toBe('rgba(0, 0, 0, 0)');
      await expect(baseline).toHaveCount(1);
    });
  }
}

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
