import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Mobile / smartphone responsiveness.
 *
 * Covers the responsive refactor: the app shell collapses its sidebar into an off-canvas
 * drawer (hamburger in the TopBar) below Tailwind's `lg` breakpoint, and the wide list-page
 * tables become stacked cards. The core regression these guard against is forced horizontal
 * scrolling on a ~390px phone screen.
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
    // folderId = Root sentinel (…0002): the real API always returns a non-null FolderId
    // (default RootFolderId); the page scopes variables to the selected folder, so omitting
    // it leaves undefined which never matches Root → the card list renders empty.
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
          // Handle-less edge, exactly like real workflow JSON — must still render a line
          // (regression guard: requires withDefaultEdgePorts + connectable nodes).
          edges: [{ id: 'e1', source: 'step-1', target: 'step-2', type: 'labeled', data: { label: 'On Success' } }],
        }),
      }),
    }));
    await page.setViewportSize(PHONE);
    await page.goto(`/workflows/${WF}`);

    // Read-only graph: hint + the reused nodes render, but no editor library tabs.
    await expect(page.getByText(/read-only view/i)).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('.react-flow__node[data-id="step-1"]')).toBeVisible({ timeout: 20_000 });
    // The edge must be created in the DOM — handle-less edges were silently DROPPED by
    // React Flow before the fix (unresolvable ports), so it wasn't in the tree at all.
    // (toBeAttached, not toBeVisible: Playwright reports SVG <g> groups as "hidden".)
    await expect(page.locator('.react-flow__edge[data-id="e1"]')).toBeAttached({ timeout: 20_000 });
    await expect(page.locator('.react-flow__edge[data-id="e1"] .react-flow__edge-path')).toBeAttached();
    await expect(page.getByRole('tab', { name: /nodes/i })).toHaveCount(0);
  });
});
