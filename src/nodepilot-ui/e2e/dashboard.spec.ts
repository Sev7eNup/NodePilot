import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_HOST } from './fixtures/mockApi';

/**
 * Dashboard page.
 *
 * KPI cards and execution lists read GET /api/stats/dashboard. Duration trends and failure
 * causes load independently through their own aggregate endpoints.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts. The catch-all returns
 * [] for unmocked /api/* calls (including /observability/config so the opt-in Telemetry section
 * stays hidden), so only /stats/dashboard needs pinning. The SPA renders English under
 * Playwright, so selectors stay bilingual.
 */

const WF_TOP = 'a0000000-0000-0000-0000-0000000000aa';
const WF_FAIL = 'b0000000-0000-0000-0000-0000000000bb';
const EXEC_RECENT = 'c0000000-0000-0000-0000-0000000000cc';
const EXEC_RUNNING = 'd0000000-0000-0000-0000-0000000000dd';

function hourBuckets() {
  // 24 hourly buckets ending now; a few carry activity so the bars render.
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
    retryStats: { finishedCount: 40, retriedCount: 3 },
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

  for (const theme of ['light', 'dark']) {
    for (const width of [1440, 390]) {
      test(`HA and queue KPIs stay readable at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
        const german = width === 390;
        await page.setViewportSize({ width, height: 1000 });
        await page.addInitScript(({ value, language }) => {
          localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: value }, version: 0 }));
          localStorage.setItem('nodepilot.lang', language);
        }, { value: theme, language: german ? 'de' : 'en' });
        await page.route('**/api/stats/dashboard**', route => route.fulfill({ json: dashboardStats({ clusterRole: null, pendingCount: 0, runningCount: 7, longRunningCount: 0 }) }));
        await page.goto('/');
        const card = page.locator('.np-card').filter({ has: page.getByText('HA', { exact: true }) });
        const value = card.getByText(german ? 'Deaktiviert' : 'Disabled', { exact: true });
        const hint = card.getByText(german ? 'Einzelknoten' : 'Single node', { exact: true });
        await expect(value).toBeVisible();
        await expect(hint).toBeVisible();
        await expect(page.getByText(german ? 'HA: deaktiviert' : 'HA: disabled', { exact: true })).toBeVisible();
        await expect(card.getByText('Leader', { exact: true })).toHaveCount(0);
        for (const text of [value, hint]) {
          expect(await text.evaluate(e => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
        }
        await card.screenshot({ path: testInfo.outputPath(`ha-disabled-${theme}-${width}.png`), animations: 'disabled' });
        const queue = page.locator('.np-card').filter({ has: page.getByText('Queue', { exact: true }) });
        await expect(queue.getByText(german ? 'Wartend' : 'Pending', { exact: true })).toBeVisible();
        await expect(queue.getByText(german ? 'Laufend' : 'Running', { exact: true })).toBeVisible();
        await expect(queue.locator('dd')).toHaveText(['0', '7']);
        expect(await queue.locator('dt, dd').evaluateAll(elements => elements.every(e => e.scrollWidth <= e.clientWidth + 1))).toBe(true);
        await queue.screenshot({ path: testInfo.outputPath(`queue-${theme}-${width}.png`), animations: 'disabled' });
      });

      test(`retry KPI stays readable at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
        const german = width === 390;
        await page.setViewportSize({ width, height: 1000 });
        await page.addInitScript(({ value, language }) => {
          localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: value }, version: 0 }));
          localStorage.setItem('nodepilot.lang', language);
        }, { value: theme, language: german ? 'de' : 'en' });
        await page.route('**/api/stats/dashboard**', route => route.fulfill({ json: dashboardStats({
          retryStats: { finishedCount: 59_516, retriedCount: 3690 },
        }) }));
        await page.goto('/');
        const label = page.getByText(german ? 'Wiederholungen nötig (24 h)' : 'Retries needed (24h)', { exact: true });
        const card = page.locator('.np-card').filter({ has: label });
        await expect(label).toBeVisible();
        await expect(card.getByText(german ? /6,2\s%/ : '6.2%', { exact: true })).toBeVisible();
        await expect(card.getByText(german ? '3.690 Ausführungen' : '3,690 executions', { exact: true })).toBeVisible();
        expect(await card.evaluate(e => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
        expect(await label.evaluate(e => e.scrollWidth <= e.clientWidth + 1 && e.scrollHeight <= e.clientHeight + 1)).toBe(true);
        await card.screenshot({ path: testInfo.outputPath(`retry-kpi-${theme}-${width}.png`), animations: 'disabled' });
        const explanation = card.getByLabel(german ? 'Berechnung des Wiederholungsanteils' : 'How the retry share is calculated');
        await explanation.focus();
        await page.keyboard.press('Enter');
        await expect(card.locator('details')).toHaveAttribute('open', '');
        await expect(card.locator('details p')).toContainText(german ? '59.516 beendeten Ausführungen' : '59,516 finished executions');
        await expect(card.locator('details p')).toBeVisible();
        await card.locator('details p').scrollIntoViewIfNeeded();
        const bounds = await card.locator('details p').boundingBox();
        expect(bounds!.x).toBeGreaterThanOrEqual(0);
        expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
        const visibility = await card.locator('details p').evaluate(element => {
          const rect = element.getBoundingClientRect();
          const hit = document.elementFromPoint(rect.x + rect.width / 2, rect.bottom - 6);
          return { unobscured: element.contains(hit), blocker: hit?.outerHTML.slice(0, 250) };
        });
        expect(visibility).toMatchObject({ unobscured: true });
        await page.screenshot({ path: testInfo.outputPath(`retry-kpi-help-${theme}-${width}.png`), animations: 'disabled' });
        await page.keyboard.press('Enter');
        await expect(card.locator('details')).not.toHaveAttribute('open');
        await expect(page).toHaveURL(/\/$/);
      });
    }
  }

  for (const theme of ['light', 'dark']) {
    for (const width of [1440, 390]) {
      test(`failure causes wrap and expand at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1000 });
        await page.addInitScript(value => {
          localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: value }, version: 0 }));
        }, theme);
        const longMessage = 'Connection refused on backup-server-02 while reading C:\\ProgramData\\NodePilot\\' + 'long-directory-name/'.repeat(18) + 'snapshot.json (503). Request <id> at <timestamp>.';
        await page.route('**/api/stats/dashboard**', route => route.fulfill({ json: dashboardStats() }));
        await page.route('**/api/stats/failure-causes**', route => route.fulfill({ json: {
          totalFailed: 788, remainingCount: 17,
          groups: [
            { message: longMessage, count: 380 },
            { message: 'Access denied on server-A while opening C:\\backup\\inventory.json (403)', count: 240 },
            { message: 'Timeout after 30 seconds waiting for the remote command to complete', count: 95 },
            { message: 'Invalid JSON response from https://inventory.example/api/assets', count: 42 },
            { message: null, count: 14 },
          ].map(group => ({ ...group, latestExecutionId: EXEC_RECENT, latestStartedAt: new Date().toISOString() })),
        } }));
        await page.goto('/');
        const card = page.locator('.np-card').filter({ has: page.getByRole('heading', { name: 'Most Common Errors (24h)', exact: true }) });
        await card.scrollIntoViewIfNeeded();
        await expect(card.getByText('380 executions · 48.2%')).toBeVisible();
        const scroller = card.locator('.overflow-y-auto');
        expect(await scroller.evaluate(e => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
        await card.screenshot({ path: testInfo.outputPath(`failure-causes-${theme}-${width}.png`), animations: 'disabled' });
        const expand = card.getByRole('button', { name: 'Show full message' });
        await expand.focus();
        await page.keyboard.press('Enter');
        await expect(card.getByRole('button', { name: 'Show less' })).toHaveAttribute('aria-expanded', 'true');
        await expect(card.getByText(longMessage, { exact: true })).toBeVisible();
        expect(await scroller.evaluate(e => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
        expect(await scroller.evaluate(e => e.scrollHeight > e.clientHeight)).toBe(true);
        await card.screenshot({ path: testInfo.outputPath(`failure-causes-expanded-${theme}-${width}.png`), animations: 'disabled' });
        await card.getByRole('link', { name: `Open latest failed execution: ${longMessage}`, exact: true }).click();
        await expect(page).toHaveURL(new RegExp(`/executions\\?id=${EXEC_RECENT}`));
      });
    }
  }

  test('one-hour window charts thirty two-minute buckets as an area', async ({ page }, testInfo) => {
    // Local-time starts keep the expected HH:mm labels independent of the machine time zone.
    const pad = (n: number) => String(n).padStart(2, '0');
    const starts = Array.from({ length: 30 }, (_, i) => new Date(2026, 8, 15, 21, 14 + 2 * i));
    const hovered = 22;
    const buckets = starts.map((start, i) => ({
      hourStart: start.toISOString(),
      succeeded: i === hovered ? 173 : 20 + (i % 5) * 6,
      failed: i === hovered ? 19 : i % 6 === 0 ? 2 : 0,
      cancelled: i === hovered ? 11 : i % 9 === 0 ? 1 : 0,
    }));
    const sum = (key: 'succeeded' | 'failed' | 'cancelled') => buckets.reduce((acc, b) => acc + b[key], 0);
    const last24h = { succeeded: sum('succeeded'), failed: sum('failed'), cancelled: sum('cancelled'), running: 0 };
    await page.route('**/api/stats/dashboard**', route => route.fulfill({
      json: new URL(route.request().url()).searchParams.get('windowHours') === '1'
        ? dashboardStats({ last24h: { ...last24h, total: last24h.succeeded + last24h.failed + last24h.cancelled }, last24hBuckets: buckets })
        : dashboardStats(),
    }));
    const label = (i: number) => `${pad(starts[i].getHours())}:${pad(starts[i].getMinutes())}`;
    await page.goto('/');
    await page.getByRole('button', { name: '1h', exact: true }).click();
    const chart = page.getByRole('img', { name: 'Executions — 1h', exact: true });
    await expect(chart).toBeVisible();
    await expect.poll(() => chart.locator('svg text').allTextContents()).toEqual(expect.arrayContaining([label(0), label(5)]));
    const succeededLine = chart.locator('svg path[fill="none"][stroke="#22c55e"]');
    await expect(succeededLine).toHaveCount(1);
    expect(await succeededLine.getAttribute('d')).toMatch(/C/);
    await expect(chart.locator('svg path[fill="#22c55e"]')).toHaveCount(0);
    await chart.scrollIntoViewIfNeeded();
    const box = await chart.boundingBox();
    // The category axis has no boundary gap, so bucket i sits at grid.left + i * step.
    await page.mouse.move(box!.x + 2 + hovered * (box!.width - 6) / 29, box!.y + box!.height / 2, { steps: 4 });
    await expect(chart).toContainText(label(hovered));
    await expect(chart).toContainText('173');
    await expect(chart).toContainText('19');
    await expect(chart).toContainText('11');
    await chart.locator('..').screenshot({ path: testInfo.outputPath('one-hour-executions.png'), animations: 'disabled' });
  });

  test('duration trend shows both percentiles and fills the card at every width', async ({ page }, testInfo) => {
    await page.route('**/api/stats/dashboard**', route => route.fulfill({ json: dashboardStats() }));
    await page.route('**/api/stats/duration-trend**', route => route.fulfill({ json: {
      workflows: [{ id: WF_TOP, name: 'Nightly Backup' }],
      buckets: hourBuckets().map((b, i) => ({
        startedAt: b.hourStart, count: i >= 12 ? 100 : 0,
        medianMs: i >= 12 ? 1000 + i * 100 : null, p95Ms: i >= 12 ? 4000 + i * 200 : null,
      })),
    } }));
    await page.goto('/');
    const chart = page.getByRole('img', { name: 'Execution Duration (24h)', exact: true });
    await expect(chart).toBeVisible();
    const lines = chart.locator('svg path[fill="none"][stroke-width="2"]');
    await expect(lines).toHaveCount(2);
    for (const line of await lines.all()) {
      expect(await line.getAttribute('d')).toMatch(/[LC]/);
    }
    for (const width of [1920, 1440, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      await chart.scrollIntoViewIfNeeded();
      await expect.poll(() => chart.evaluate(el =>
        Math.abs(Number(el.querySelector('svg')?.getAttribute('height')) - el.clientHeight),
      )).toBeLessThanOrEqual(1);
      const card = chart.locator('..').locator('..');
      const chartBox = await chart.boundingBox();
      const cardBox = await card.boundingBox();
      expect(cardBox!.y + cardBox!.height - chartBox!.y - chartBox!.height).toBeLessThanOrEqual(24);
      await card.screenshot({ path: testInfo.outputPath(`duration-trend-${width}.png`), animations: 'disabled' });
    }
    await page.getByRole('combobox', { name: 'Workflow for execution duration' }).selectOption(WF_TOP);
    await expect(page.getByRole('combobox', { name: 'Workflow for execution duration' })).toHaveValue(WF_TOP);
    await expect(chart).toBeVisible();
  });

  for (const theme of ['light', 'dark']) {
    test(`duration trend displays isolated observations in ${theme} theme`, async ({ page }, testInfo) => {
      await page.addInitScript(theme => {
        localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme }, version: 0 }));
      }, theme);
      await page.route('**/api/stats/dashboard**', route => route.fulfill({ json: dashboardStats() }));
      await page.route('**/api/stats/duration-trend**', route => route.fulfill({ json: {
        workflows: [],
        buckets: hourBuckets().map((b, index) => ({
          startedAt: b.hourStart, count: [7, 19].includes(index) ? 10 : 0,
          medianMs: [7, 19].includes(index) ? 1000 : null,
          p95Ms: [7, 19].includes(index) ? 3000 : null,
        })),
      } }));
      await page.goto('/');
      const chart = page.getByRole('img', { name: 'Execution Duration (24h)', exact: true });
      await expect(chart).toBeVisible();
      const lines = chart.locator('svg path[fill="none"][stroke-width="2"]');
      await expect(lines).toHaveCount(2);
      for (const line of await lines.all()) {
        const path = await line.getAttribute('d');
        expect(path?.match(/M/g)).toHaveLength(2);
        expect(path).not.toMatch(/[LC]/);
      }
      const colors = await lines.evaluateAll(elements => elements.map(el => el.getAttribute('stroke')));
      const markers = chart.locator(colors.map(color => `svg path[fill="${color}"]`).join(','));
      await expect(markers).toHaveCount(4);
      await expect.poll(async () => (await markers.last().boundingBox())?.width ?? 0).toBeGreaterThan(4);
      await markers.last().hover();
      await expect(chart).toContainText('10 runs');
      await expect(chart).toContainText('Median: 1.0 s');
      await expect(chart).toContainText('P95: 3.0 s');
      await chart.locator('..').locator('..').screenshot({ path: testInfo.outputPath('sparse-duration-trend.png'), animations: 'disabled' });
    });
  }

  test('11.1 — stat cards, 24h chart, top/recent lists render from /stats/dashboard', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');

    // The title renders and the loading placeholder is gone (no hung loading state).
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/^loading…?$|^lädt/i)).toHaveCount(0);

    // KPI cards: label and value pairs, with the values coming from the mock payload.
    await expect(page.getByText(/^workflows$/i).first()).toBeVisible();
    // Scope the KPI value to the main content: the sidebar Workflows badge renders "12" from
    // the same /stats/dashboard payload, so an unscoped match is ambiguous.
    await expect(page.getByRole('main').getByText('12', { exact: true })).toBeVisible(); // workflowsTotal
    await expect(page.getByText(/^machines$/i).first()).toBeVisible();
    await expect(page.getByText(/success rate/i).first()).toBeVisible();
    await expect(page.getByText('90%', { exact: true })).toBeVisible(); // 36/(36+4)
    await expect(page.getByText('Retries needed (24h)', { exact: true })).toBeVisible();
    await expect(page.getByText('7.5%', { exact: true })).toBeVisible();

    // Selected-window chart header renders.
    await expect(page.getByRole('heading', { name: /executions.*24h/i })).toBeVisible();

    // Top Workflows list, sorted by activity.
    await expect(page.getByRole('heading', { name: /^top workflows/i })).toBeVisible();
    await expect(page.getByText('Nightly Backup').first()).toBeVisible();
    await expect(page.getByText('Disk Cleanup').first()).toBeVisible();

    // Top Workflows: rank numbers and success rate percentages.
    await expect(page.getByText('#1').first()).toBeVisible();
    await expect(page.getByText('95%', { exact: true }).first()).toBeVisible(); // Nightly Backup: 19/20

    // Failing and Currently Running panels.
    await expect(page.getByRole('heading', { name: /failing workflows/i })).toBeVisible();
    await expect(page.getByText('Flaky Deploy')).toBeVisible();
    await expect(page.getByText('30%', { exact: true }).first()).toBeVisible(); // Flaky Deploy: 3/10
    await expect(page.getByRole('heading', { name: /currently running/i })).toBeVisible();

    // Recent Executions table: the last row is present with a status badge.
    await expect(page.getByRole('heading', { name: /recent executions/i })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Nightly Backup' })).toBeVisible();
    await expect(page.getByText(/^succeeded$/i).last()).toBeVisible();
  });

  test('11.2a — Top-Workflows entry navigates to /workflows/:id', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );
    // The editor loads the workflow after navigation, so serve a minimal one.
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

    // Scope to the Top Workflows panel (the parent card of the heading). The Currently Running
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
    // (machine and domain in one), otherwise the bare machine name. MOCK_HOST.fqdn has a dot, so
    // the chip shows the full FQDN, which carries the machine name and domain as substrings.
    await expect(page.getByText(MOCK_HOST.fqdn, { exact: true })).toBeVisible({ timeout: 15_000 });
    // The chip value also contains the domain part of the FQDN.
    await expect(page.getByText(MOCK_HOST.fqdn, { exact: true })).toContainText(MOCK_HOST.domain);
  });

  test('11.1c — many running executions stay in a fixed-height scroll box (no sibling distortion)', async ({ page }) => {
    // At the xl breakpoint the hero row holds the gauge, the KPI cluster and Currently Running.
    // The running box keeps the row height set by the gauge and KPI cluster and scrolls inside
    // instead of stretching its siblings. The wide viewport activates the xl four-column layout.
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

    // The running card is no taller than the gauge card, so the long list did not stretch the row.
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
    // "No executions yet" is the shared empty state for several lists (top workflows, recent,
    // chart), so it renders more than once and one visible match is enough.
    await expect(page.getByText(/no executions yet/i).first()).toBeVisible();
  });

  test('11.4 - Bank Hell quick actions keep red-accented secondary buttons', async ({ page }) => {
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'light-bank' }, version: 0 })),
    );
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dashboardStats()) }),
    );

    await page.goto('/');
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-bank');

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

    // The secondary button carries a red-accented but light border: red dominates while green
    // and blue stay high enough for the border to read as a light red rather than a strong one.
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
