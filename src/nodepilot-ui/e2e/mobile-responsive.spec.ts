import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, capsJson, mockCaps } from './fixtures/mockApi';

/**
 * Mobile / smartphone responsiveness.
 *
 * Below Tailwind's `lg` breakpoint the app shell collapses its sidebar into an off-canvas
 * drawer (hamburger in the TopBar) and the wide list-page tables become stacked cards.
 * These tests guard against forced horizontal scrolling on a phone-sized screen.
 *
 * Hermetic: page.route() mocks only (no backend). SPA renders EN under Playwright.
 */

const PHONE = { width: 390, height: 844 };
const DESKTOP = { width: 1440, height: 900 };

const ONE_MACHINE = [
  {
    id: 'm1', name: 'Web-01', hostname: 'web01.lab.local', winRmPort: 5985, useSsl: true,
    defaultCredentialId: null, tags: 'prod,web', lastConnectivityCheck: '2026-06-01T10:00:00Z',
    isReachable: true, usedByWorkflowCount: 4, recentStepCount: 20, recentFailedStepCount: 1, activeRunCount: 2,
  },
];

async function mockMachines(page: Page) {
  await page.route('**/api/machines', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ONE_MACHINE) }),
  );
}

async function hasNoHorizontalOverflow(page: Page): Promise<boolean> {
  return page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
}

test.describe('Mobile responsiveness', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('shell: hamburger opens an off-canvas drawer that closes on navigation', async ({ page }) => {
    await page.setViewportSize(PHONE);
    await page.goto('/machines');

    const hamburger = page.getByRole('button', { name: 'Open menu' });
    await expect(hamburger).toBeVisible({ timeout: 15_000 });

    const aside = page.locator('aside');
    // Closed: the drawer is translated off-screen to the left (negative x).
    await expect.poll(async () => (await aside.boundingBox())?.x ?? 0).toBeLessThan(0);

    await hamburger.click();
    // Open: the drawer slides to x = 0.
    await expect.poll(async () => (await aside.boundingBox())?.x ?? -999).toBeGreaterThanOrEqual(-1);

    // Tapping a nav link navigates and auto-closes the drawer.
    await aside.getByRole('link', { name: 'Settings' }).click();
    await expect(page).toHaveURL(/\/settings$/);
    await expect.poll(async () => (await aside.boundingBox())?.x ?? 0).toBeLessThan(0);
  });

  test('shell: hamburger is hidden on desktop', async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    await page.goto('/machines');
    await expect(page.getByRole('heading', { name: /machines/i }).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: 'Open menu' })).toBeHidden();
  });

  test('machines: renders cards (not a table) and has no horizontal overflow on a phone', async ({ page }) => {
    await mockMachines(page);
    await page.setViewportSize(PHONE);
    await page.goto('/machines');

    await expect(page.getByText('Web-01')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('mobile-card-list')).toBeVisible();
    await expect(page.locator('table')).toHaveCount(0);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('machines: renders a table on desktop', async ({ page }) => {
    await mockMachines(page);
    await page.setViewportSize(DESKTOP);
    await page.goto('/machines');

    await expect(page.getByText('Web-01')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('table')).toHaveCount(1);
    await expect(page.getByTestId('mobile-card-list')).toHaveCount(0);
  });

  test('users: cards with no horizontal overflow on a phone', async ({ page }) => {
    await page.route('**/api/users', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'u1', username: 'alice', role: 'Admin', isActive: true, createdAt: '2026-06-01T00:00:00Z' }]),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/users');
    await expect(page.getByText('alice')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('mobile-card-list')).toBeVisible();
    await expect(page.locator('table')).toHaveCount(0);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('global variables: cards with no horizontal overflow on a phone', async ({ page }) => {
    // folderId is the Root sentinel: the real API always returns a non-null FolderId and the
    // page scopes variables to the selected folder. Omitting it leaves undefined, which never
    // matches Root, so the card list would render empty.
    await page.route('**/api/global-variables', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'g1', name: 'API_BASE', value: 'https://x', isSecret: false, description: 'base url', folderId: '00000000-0000-0000-0000-000000000002', createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' }]),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/global-variables');
    await expect(page.getByText('API_BASE')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('mobile-card-list')).toBeVisible();
    await expect(page.locator('table')).toHaveCount(0);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('executions: cards with no horizontal overflow on a phone', async ({ page }) => {
    await page.route('**/api/workflows', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'wf1', name: 'Nightly Backup', version: 1, activityCount: 3, triggerTypes: [], isEnabled: true, createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z' }]),
    }));
    await page.route('**/api/executions**', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'ex1', workflowId: 'wf1', status: 'Succeeded', startedAt: '2026-06-01T10:00:00Z', completedAt: '2026-06-01T10:01:00Z', stepsTotal: 3, stepsCompleted: 3, failedSteps: [], triggeredBy: 'manual', startedByUsername: 'admin', traceId: null, parentExecutionId: null }]),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/executions');
    const cards = page.getByTestId('mobile-card-list');
    // Name also appears in the workflow filter <option>; scope to the card list.
    await expect(cards.getByText('Nightly Backup')).toBeVisible({ timeout: 15_000 });
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('workflows: cards + collapsible folder tree, no horizontal overflow on a phone', async ({ page }) => {
    await page.route('**/api/workflows', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'wf1', name: 'Nightly Backup', version: 2, activityCount: 5, triggerTypes: ['scheduleTrigger'], isEnabled: true, successCount: 9, totalCount: 10, avgDurationMs: 1200, createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-02T00:00:00Z', createdBy: 'admin', updatedBy: 'admin' }]),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/workflows');
    await expect(page.getByText('Nightly Backup')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('mobile-card-list')).toBeVisible();
    await expect(page.locator('table')).toHaveCount(0);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('audit: stacked rows (no column-header grid) and no horizontal overflow on a phone', async ({ page }) => {
    await page.route('**/api/audit**', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ items: [{ id: 'a1', timestamp: '2026-06-01T10:00:00Z', userId: 'u1', username: 'admin', action: 'CREATE_WORKFLOW', resourceType: 'Workflow', resourceId: 'wf123456789', details: '{}', ipAddress: '10.0.0.1' }], nextCursor: null }),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/audit');
    // The action appears both as a quick-filter chip ("CREATE_WORKFLOW (1)") and in the row;
    // match the row's exact-text span.
    await expect(page.getByText('CREATE_WORKFLOW', { exact: true })).toBeVisible({ timeout: 15_000 });
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('maintenance windows: cards with no horizontal overflow on a phone', async ({ page }) => {
    await page.route('**/api/maintenance-windows', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ id: 'w1', name: 'Weekend Blackout', description: 'No prod runs', isEnabled: true, mode: 'Blackout', scopeKind: 'Global', recurrence: 'Weekly', oneTimeStartUtc: null, oneTimeEndUtc: null, weeklyDaysMask: 65, weeklyStartMinuteOfDay: 1320, weeklyEndMinuteOfDay: 120, cronExpression: null, durationMinutes: null, timeZoneId: 'UTC', targets: [], createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' }]),
    }));
    await page.setViewportSize(PHONE);
    await page.goto('/maintenance-windows');
    await expect(page.getByText('Weekend Blackout')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('mobile-card-list')).toBeVisible();
    await expect(page.locator('table')).toHaveCount(0);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('dialog: fits within the viewport and stays scrollable on a phone', async ({ page }) => {
    await page.setViewportSize(PHONE);
    await page.goto('/machines');
    await page.getByRole('button', { name: /add machine/i }).click();
    // Dialog mounts (heading + submit reachable) and does not push the document wider.
    await expect(page.getByRole('heading', { name: /add machine/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByPlaceholder(/hostname or ip/i)).toBeVisible();
    expect(await hasNoHorizontalOverflow(page)).toBe(true);
  });

  test('ai chat: a phone gets the trimmed header and four starter prompts, desktop the full set', async ({ page }) => {
    await mockCaps(page, capsJson());
    await page.setViewportSize(PHONE);
    await page.goto('/ai-chat');

    const main = page.locator('#np-main-scroll');
    await expect(main.getByRole('heading', { name: /^AI Chat$/i })).toBeVisible({ timeout: 20_000 });

    // These all stay in the DOM (`hidden lg:*`) but must not take space on a phone: subtitle,
    // source badges and the empty-state hint would fill most of the screen above the fold.
    // The heading alone carries the message.
    await expect(main.getByText(/^Ask NodePilot — docs/i)).toBeHidden();
    await expect(main.getByText(/^Sources:$/i)).toBeHidden();
    await expect(main.getByText(/^Docs$/i)).toBeHidden();
    await expect(main.getByText(/^Docs, your installed workflows/i)).toBeHidden();
    await expect(main.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible();

    // Four of the eight starter prompts. Role selectors skip `display:none`, so this counts what
    // is actually offered, not what is rendered.
    const prompts = page.getByTestId('ai-chat-empty').getByRole('button');
    await expect(prompts).toHaveCount(4);
    expect(await hasNoHorizontalOverflow(page)).toBe(true);

    await page.setViewportSize(DESKTOP);
    await expect(main.getByText(/^Ask NodePilot — docs/i)).toBeVisible();
    await expect(main.getByText(/^Sources:$/i)).toBeVisible();
    await expect(main.getByText(/^Docs, your installed workflows/i)).toBeVisible();
    await expect(prompts).toHaveCount(8);
  });

  test('ai chat: the empty state stays fully scrollable when it outgrows the screen', async ({ page }) => {
    await mockCaps(page, capsJson());
    // A short viewport, not PHONE: the trimmed mobile empty state (heading + four prompts) fits
    // a full-height phone with room to spare, so only a short screen — a landscape phone, a
    // small split view — reproduces the overflow this guards.
    await page.setViewportSize({ width: 390, height: 520 });
    await page.goto('/ai-chat');

    await expect(page.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible({ timeout: 20_000 });

    // A centred flex block that outgrows its scroll port overflows on both sides, and anything
    // above the scroll origin cannot be reached by scrolling. The block must therefore start at
    // or below the top of its scroll port, and the port must genuinely overflow for that check
    // to mean anything.
    const port = await page.getByTestId('ai-chat-scroll').boundingBox();
    const block = await page.getByTestId('ai-chat-empty').boundingBox();
    expect(block!.height).toBeGreaterThan(port!.height);
    expect(block!.y).toBeGreaterThanOrEqual(port!.y - 1);
  });

  test('live-ops: phones get a run list instead of the timeline, and can still drill in', async ({ page }) => {
    const MIN = 60_000;
    const t0 = Date.now();
    await page.route('**/api/operations/graph*', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        nodes: [
          { workflowId: 'wf-1', name: 'Nightly Backup Of The Whole Estate', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 1, lastStatus: null, callFrequency: 2, canRun: true, canEdit: true },
          { workflowId: 'wf-2', name: 'Cleanup Temp', folderId: 'prod', folderPath: '/Prod', isEnabled: true, runningCount: 0, lastStatus: 'Failed', callFrequency: 1, canRun: true, canEdit: true },
        ],
        edges: [],
        running: [{ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(t0 - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
        recent: [{ executionId: 'ex-2', workflowId: 'wf-2', status: 'Failed', startedAt: new Date(t0 - 10 * MIN).toISOString(), completedAt: new Date(t0 - 8 * MIN).toISOString(), parentExecutionId: null }],
        density: [],
        meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
      }),
    }));
    await page.route('**/api/executions/ex-1', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        id: 'ex-1', workflowId: 'wf-1', status: 'Running',
        startedAt: new Date(t0 - 4 * MIN).toISOString(), completedAt: null,
        triggeredBy: 'schedule', errorMessage: null, traceId: null, spanId: null,
        returnData: null, inputParametersJson: null, stepsTotal: 0, stepsCompleted: 0, failedSteps: null,
      }),
    }));

    await page.setViewportSize(PHONE);
    await page.goto('/operations');

    // The Gantt timeline is dropped rather than shrunk: at phone width its track is too narrow
    // to show a run as more than a sliver next to a name truncated to nothing.
    const list = page.getByTestId('ops-mobile');
    await expect(list).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('ops-time-axis')).toHaveCount(0);

    // Full workflow name and live elapsed time...
    await expect(list.getByText('Nightly Backup Of The Whole Estate')).toBeVisible();
    await expect(list.getByText(/running for/i)).toBeVisible();
    // ...and the failure in its own section, where a long success list cannot bury it.
    const failed = page.getByRole('region', { name: 'Failed' });
    await expect(failed.getByText('Cleanup Temp')).toBeVisible();
    await expect(list.getByText('1 failed')).toBeVisible();
    expect(await hasNoHorizontalOverflow(page)).toBe(true);

    // Tapping a run opens the same drilldown the timeline opens, hosted as a full-height sheet.
    await list.getByText('Nightly Backup Of The Whole Estate').click();
    await expect(page.getByTestId('ops-drilldown-sheet')).toBeVisible();
    await expect(page.getByRole('complementary', { name: /execution detail/i })).toBeVisible();

    await page.setViewportSize(DESKTOP);
    await expect(page.getByTestId('ops-time-axis')).toBeVisible();
    await expect(page.getByTestId('ops-mobile')).toHaveCount(0);
  });

  test('designer: phones get a read-only graph (with edges) instead of the editor', async ({ page }) => {
    const WF = '20202020-2020-2020-2020-202020202020';
    await page.route(`**/api/workflows/${WF}`, (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        id: WF, name: 'Disk Health Check', description: '', isEnabled: true, version: 1,
        definitionJson: JSON.stringify({
          nodes: [
            { id: 'step-1', type: 'activity', position: { x: 80, y: 80 }, data: { label: 'Check Disk', activityType: 'runScript', config: { script: 'Get-PSDrive C' } } },
            { id: 'step-2', type: 'activity', position: { x: 360, y: 80 }, data: { label: 'Email Result', activityType: 'emailNotification', config: {} } },
          ],
          // Handle-less edge, exactly like real workflow JSON. Rendering a line for it requires
          // withDefaultEdgePorts plus connectable nodes.
          edges: [{ id: 'e1', source: 'step-1', target: 'step-2', type: 'labeled', data: { label: 'On Success' } }],
        }),
      }),
    }));
    await page.setViewportSize(PHONE);
    await page.goto(`/workflows/${WF}`);

    // Read-only graph: hint + the reused nodes render, but no editor library tabs.
    await expect(page.getByText(/read-only view/i)).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('.react-flow__node[data-id="step-1"]')).toBeVisible({ timeout: 20_000 });
    // The edge must exist in the DOM: React Flow silently drops edges whose ports it cannot
    // resolve, which leaves them out of the tree entirely.
    // toBeAttached, not toBeVisible: Playwright reports SVG <g> groups as hidden.
    await expect(page.locator('.react-flow__edge[data-id="e1"]')).toBeAttached({ timeout: 20_000 });
    await expect(page.locator('.react-flow__edge[data-id="e1"] .react-flow__edge-path')).toBeAttached();
    await expect(page.getByRole('tab', { name: /nodes/i })).toHaveCount(0);
  });
});
