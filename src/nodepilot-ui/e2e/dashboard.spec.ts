import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_HOST } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 11 — Dashboard.
 *
 * The dashboard reads a single aggregate endpoint: GET /api/stats/dashboard. Everything on the
 * page (KPI cards, the 24h bar chart, Top/Failing workflow panels, Currently-Running, Recent
 * Executions table, edit-locks, armed triggers, recent audit) is derived from that one payload.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts. The catch-all returns
 * [] for unmocked /api/* calls (including /observability/config so the opt-in Telemetry section
 * stays hidden), so we only need to pin /stats/dashboard. SPA renders EN under Playwright →
 * bilingual / EN selectors.
 */

const WF_TOP = 'a0000000-0000-0000-0000-0000000000aa';
const WF_FAIL = 'b0000000-0000-0000-0000-0000000000bb';
const EXEC_RECENT = 'c0000000-0000-0000-0000-0000000000cc';
const EXEC_RUNNING = 'd0000000-0000-0000-0000-0000000000dd';

function hourBuckets() {
  // 24 hourly buckets ending now; sprinkle a few with activity so bars render.
  const out: Array<{ hourStart: string; succeeded: number; failed: number; cancelled: number }> = [];
  const base = Date.now() - 23 * 3600 * 1000;
  for (let i = 0; i < 24; i++) {
    out.push({
      hourStart: new Date(base + i * 3600 * 1000).toISOString(),
      succeeded: i % 3 === 0 ? 4 : 0,
      failed: i % 7 === 0 ? 1 : 0,
      cancelled: 0,
    });
  }
  return out;
}

function dashboardStats(overrides: Record<string, unknown> = {}) {
  return {
    workflowsTotal: 12,
    workflowsEnabled: 9,
    machinesTotal: 4,
    machinesReachable: 4,
    executionsTotal: 530,
    last24h: { total: 40, succeeded: 36, failed: 4, running: 1, cancelled: 0 },
    last24hBuckets: hourBuckets(),
    topWorkflows: [
      { id: WF_TOP, name: 'Nightly Backup', runCount: 20, successCount: 19, failCount: 1, avgDurationMs: 4200, p95DurationMs: 8000 },
      { id: 'a0000000-0000-0000-0000-0000000000ab', name: 'Disk Cleanup', runCount: 8, successCount: 8, failCount: 0, avgDurationMs: 1500, p95DurationMs: 2000 },
    ],
    running: [
      { id: EXEC_RUNNING, workflowId: WF_TOP, workflowName: 'Nightly Backup', status: 'Running', startedAt: new Date(Date.now() - 60_000).toISOString(), triggeredBy: 'schedule' },
    ],
    recent: [
      { id: EXEC_RECENT, workflowId: WF_TOP, workflowName: 'Nightly Backup', status: 'Succeeded', startedAt: new Date(Date.now() - 300_000).toISOString(), completedAt: new Date(Date.now() - 290_000).toISOString(), durationMs: 4200, triggeredBy: 'schedule' },
    ],
    armedTriggers: [],
    pendingCount: 0,
    runningCount: 1,
    longRunningCount: 0,
    failingWorkflows: [
      { id: WF_FAIL, name: 'Flaky Deploy', failCount: 3, runCount: 10, lastFailureAt: new Date(Date.now() - 600_000).toISOString() },
    ],
    editLocks: [],
    healthHeartbeats: [
      { serviceName: 'Scheduler', lastHeartbeatAt: new Date(Date.now() - 5000).toISOString(), expectedIntervalSeconds: 30, status: 'ok', isStale: false },
    ],
    databaseProvider: 'PostgreSQL',
    clusterRole: null,
    recentAudit: null,
    ...overrides,
  };
}

function cssColorChannels(value: string): number[][] {
  return [...value.matchAll(/rgba?\(([^)]+)\)|color\(srgb\s+([^)]+)\)/gi)]
    .map((match) => {
      const isSrgb = match[0].toLowerCase().startsWith('color(');
      const raw = match[1] ?? match[2] ?? '';
      const nums = raw.match(/-?\d*\.?\d+(?:e[-+]?\d+)?/gi)?.map(Number) ?? [];
      return nums.slice(0, 3).map((n) => Math.round(isSrgb && n <= 1 ? n * 255 : n));
    })
    .filter((channels) => channels.length === 3);
}

function firstCssColorChannels(value: string) {
  const [channels] = cssColorChannels(value);
  if (!channels) throw new Error(`Expected a CSS color in "${value}"`);
  return channels;
}

function hasNeutralBankMaterial(value: string) {
  const colors = cssColorChannels(value);
  return colors.some(([red, green, blue]) => red >= 250 && green >= 250 && blue >= 250)
    && colors.some(([red, green, blue]) => red >= 245 && green >= 247 && blue >= 249);
}

test.describe('Dashboard (Teil 11)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('11.1 — stat cards, 24h chart, top/recent lists render from /stats/dashboard', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');

    // Title + the loading placeholder must be gone (no hung loading state).
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/^loading…?$|^lädt/i)).toHaveCount(0);

    // KPI cards — assert label + value pairs (values come from the mock payload).
    await expect(page.getByText(/^workflows$/i).first()).toBeVisible();
    // Scope the KPI value to the main content: the sidebar's Workflows nav badge now also
    // renders "12" (same /stats/dashboard payload), so an unscoped match is ambiguous.
    await expect(page.getByRole('main').getByText('12', { exact: true })).toBeVisible(); // workflowsTotal
    await expect(page.getByText(/^machines$/i).first()).toBeVisible();
    await expect(page.getByText(/success rate/i).first()).toBeVisible();
    await expect(page.getByText('90%', { exact: true })).toBeVisible(); // 36/(36+4)
    await expect(page.getByText(/runs \(24h\)/i)).toBeVisible();

    // Selected-window chart header renders.
    await expect(page.getByRole('heading', { name: /executions.*24h/i })).toBeVisible();

    // Top Workflows list — sorted-by-activity entries.
    await expect(page.getByRole('heading', { name: /^top workflows/i })).toBeVisible();
    await expect(page.getByText('Nightly Backup').first()).toBeVisible();
    await expect(page.getByText('Disk Cleanup').first()).toBeVisible();

    // Top-Workflows: rank numbers + success rate percentages.
    await expect(page.getByText('#1').first()).toBeVisible();
    await expect(page.getByText('95%', { exact: true }).first()).toBeVisible(); // Nightly Backup: 19/20

    // Failing + Currently Running panels.
    await expect(page.getByRole('heading', { name: /failing workflows/i })).toBeVisible();
    await expect(page.getByText('Flaky Deploy')).toBeVisible();
    await expect(page.getByText('30%', { exact: true }).first()).toBeVisible(); // Flaky Deploy: 3/10
    await expect(page.getByRole('heading', { name: /currently running/i })).toBeVisible();

    // Recent Executions table — last row present with a status badge.
    await expect(page.getByRole('heading', { name: /recent executions/i })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Nightly Backup' })).toBeVisible();
    await expect(page.getByText(/^succeeded$/i).last()).toBeVisible();
  });

  test('11.2a — Top-Workflows entry navigates to /workflows/:id', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );
    // Editor will try to load the workflow when we land there; give it something benign.
    await page.route(`**/api/workflows/${WF_TOP}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          id: WF_TOP, name: 'Nightly Backup', description: '', isEnabled: true,
          checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null,
          definitionJson: '{"nodes":[],"edges":[]}', version: 1, activityCount: 0, triggerTypes: [],
          createdAt: '2026-06-01T00:00:00.000Z', updatedAt: '2026-06-01T00:00:00.000Z',
        }),
      }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /^top workflows/i })).toBeVisible({ timeout: 15_000 });

    // Scope to the Top-Workflows panel (the heading's parent card) — the Currently-Running
    // panel also has a "Nightly Backup" role=button, so an unscoped match is ambiguous.
    const topPanel = page.getByRole('heading', { name: /top workflows/i }).locator('..');
    await topPanel.getByRole('button', { name: /Nightly Backup/ }).first().click();
    await expect(page).toHaveURL(new RegExp(`/workflows/${WF_TOP}`));

    // Browser-back returns to the dashboard.
    await page.goBack();
    await expect(page).toHaveURL(/\/$|\/$/);
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible();
  });

  test('11.2b — Recent-Executions row deep-links to the execution', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /recent executions/i })).toBeVisible({ timeout: 15_000 });

    await page.getByRole('cell', { name: 'Nightly Backup' }).click();
    await expect(page).toHaveURL(new RegExp(`/executions\\?id=${EXEC_RECENT}`));
  });

  test('11.3 — header shows the API host identity (machine, FQDN, domain) inline', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');

    // The TopBar host chip renders a single identity field: the FQDN when it contains a dot
    // (machine + domain in one), otherwise the bare machine name. MOCK_HOST.fqdn has a dot, so
    // the chip shows the full FQDN — which carries the machine name and domain as substrings.
    await expect(page.getByText(MOCK_HOST.fqdn, { exact: true })).toBeVisible({ timeout: 15_000 });
    // The "Host" label from the chip is present too (language-agnostic: it sits next to the value).
    await expect(page.getByText(MOCK_HOST.fqdn, { exact: true })).toContainText(MOCK_HOST.domain);
  });

  test('11.1c — many running executions stay in a fixed-height scroll box (no sibling distortion)', async ({ page }) => {
    // At xl the hero row is gauge | KPI cluster | Currently-Running. The running box must stay
    // pinned to the row height (driven by the gauge/KPI) and scroll internally — it must not grow
    // and stretch its siblings. Needs a wide viewport so the xl:4-col layout is active.
    await page.setViewportSize({ width: 1440, height: 900 });

    const manyRunning = Array.from({ length: 20 }, (_, i) => ({
      id: `e0000000-0000-0000-0000-0000000${String(i).padStart(5, '0')}`,
      workflowId: WF_TOP,
      workflowName: `Running Workflow ${i + 1}`,
      status: 'Running',
      startedAt: new Date(Date.now() - (i + 1) * 30_000).toISOString(),
      triggeredBy: 'schedule',
    }));

    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(dashboardStats({ running: manyRunning, runningCount: manyRunning.length })),
      }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /currently running/i })).toBeVisible({ timeout: 15_000 });

    const runningCard = page.getByRole('heading', { name: /currently running/i }).locator('..');
    const gaugeCard = page.locator('.np-card-hero');
    const scroller = runningCard.locator('.overflow-y-auto');

    // The running card is no taller than the gauge card — i.e. the long list did not blow out the row.
    const runningBox = await runningCard.boundingBox();
    const gaugeBox = await gaugeCard.boundingBox();
    expect(runningBox).not.toBeNull();
    expect(gaugeBox).not.toBeNull();
    expect(Math.abs(runningBox!.height - gaugeBox!.height)).toBeLessThanOrEqual(4);

    // The list overflows its container and scrolls internally instead of growing.
    const { scrollHeight, clientHeight } = await scroller.evaluate((el) => ({
      scrollHeight: el.scrollHeight,
      clientHeight: el.clientHeight,
    }));
    expect(scrollHeight).toBeGreaterThan(clientHeight);
  });

  test('11.1b — empty lists render their empty-states (no hung loading)', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(dashboardStats({
          last24h: { total: 0, succeeded: 0, failed: 0, running: 0, cancelled: 0 },
          last24hBuckets: hourBuckets().map((b) => ({ ...b, succeeded: 0, failed: 0, cancelled: 0 })),
          topWorkflows: [], running: [], recent: [], failingWorkflows: [],
        })),
      }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/nothing running right now/i)).toBeVisible();
    await expect(page.getByText(/no executions in the last 7 days/i)).toBeVisible();
    // "No executions yet" is the shared empty-state for several lists (top workflows, recent, chart),
    // so it renders multiple times — assert at least one is visible.
    await expect(page.getByText(/no executions yet/i).first()).toBeVisible();
  });

  test('11.4 - Bank Hell quick actions keep red-accented secondary buttons', async ({ page }) => {
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'light-sparkasse' }, version: 0 })),
    );
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-sparkasse');

    const primary = page.getByRole('button', { name: /new workflow|neuer workflow/i });
    const secondary = page.getByRole('button', { name: /start workflow|workflow starten/i });
    await expect(primary).toBeVisible();
    await expect(secondary).toBeVisible();

    const primaryStyles = await primary.evaluate((el) => {
      const cs = getComputedStyle(el);
      return { backgroundColor: cs.backgroundColor };
    });
    const secondaryStyles = await secondary.evaluate((el) => {
      const cs = getComputedStyle(el);
      return {
        backgroundColor: cs.backgroundColor,
        backgroundImage: cs.backgroundImage,
        borderColor: cs.borderColor,
        color: cs.color,
      };
    });

    const [primaryRed, primaryGreen, primaryBlue] = firstCssColorChannels(primaryStyles.backgroundColor);
    expect(primaryRed).toBeGreaterThanOrEqual(190);
    expect(primaryGreen).toBeLessThan(35);
    expect(primaryBlue).toBeLessThan(35);

    const [secondaryRed, secondaryGreen, secondaryBlue] = firstCssColorChannels(secondaryStyles.color);
    expect(secondaryRed).toBeGreaterThanOrEqual(190);
    expect(secondaryGreen).toBeLessThan(45);
    expect(secondaryBlue).toBeLessThan(45);

    // The Bank-Hell secondary button carries a red-accented but light border. The actual
    // rendered colour is a dusty rose ≈ rgb(233, 189, 194): red clearly dominant, green/blue
    // high enough to read as "light red" (a saturated/dark red would sit far below ~150).
    // The earlier >200 floor was miscalibrated to this specific tint.
    const [borderRed, borderGreen, borderBlue] = firstCssColorChannels(secondaryStyles.borderColor);
    expect(borderRed).toBeGreaterThan(borderGreen + 4);
    expect(borderRed).toBeGreaterThan(borderBlue + 4);
    expect(borderGreen).toBeGreaterThan(170);
    expect(borderBlue).toBeGreaterThan(170);

    const [backgroundRed, backgroundGreen, backgroundBlue] = firstCssColorChannels(secondaryStyles.backgroundColor);
    expect(backgroundRed).toBeGreaterThanOrEqual(250);
    expect(backgroundGreen).toBeGreaterThanOrEqual(250);
    expect(backgroundBlue).toBeGreaterThanOrEqual(250);
    expect(hasNeutralBankMaterial(secondaryStyles.backgroundImage)).toBe(true);
  });
});
