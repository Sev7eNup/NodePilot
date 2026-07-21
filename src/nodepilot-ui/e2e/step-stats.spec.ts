import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 51 — Step Stats & Step Health.
 *
 * There is no dedicated step-stats *page*; the analytics surface inside the workflow editor.
 * `useNodeAnnotations` fetches:
 *   - GET /api/workflows/{id}/step-health?stepIds=...&limit=8  → per-node outcome sparkline (inline)
 *   - GET /api/workflows/{id}/step-stats?windowDays=30          → avg/p95/failureRate (hover tooltip
 *                                                                  + drives the failure heatmap tint)
 * Both are read-only analytics, so a Viewer gets them too (51.3).
 *
 * Hermetic: page.route() mocks only. We open the editor on a workflow whose definitionJson node
 * ids match the keys in the step-stats / step-health responses, then assert the data round-trips
 * into the canvas (sparkline dots) and the hover tooltip (perf annotations).
 */

const WF_ID = 'cccccccc-0051-0051-0051-stepstats00051';

const DEFINITION = JSON.stringify({
  nodes: [
    { id: 'step-flaky', type: 'activity', position: { x: 120, y: 100 },
      data: { label: 'Flaky Step', activityType: 'runScript', config: { script: 'Get-Date' } } },
    { id: 'step-solid', type: 'activity', position: { x: 120, y: 280 },
      data: { label: 'Solid Step', activityType: 'log', config: {} } },
  ],
  edges: [
    { id: 'e1', source: 'step-flaky', target: 'step-solid', type: 'labeled', data: {} },
  ],
});

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-StepStats',
    description: 'step-stats e2e fixture',
    isEnabled: true,
    checkedOutByUserId: null, // read-only view is enough — stats/health load regardless of lock
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson: DEFINITION,
    version: 1,
    activityCount: 2,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  });
}

// /step-stats shape consumed by useNodeAnnotations: Record<stepId, {...durations + failureRate}>.
const STEP_STATS = {
  'step-flaky': { totalRuns: 20, failedRuns: 6, failureRate: 0.3, avgDurationMs: 4200, p95DurationMs: 9100, lastDurationMs: 3800 },
  'step-solid': { totalRuns: 20, failedRuns: 0, failureRate: 0, avgDurationMs: 120, p95DurationMs: 210, lastDurationMs: 110 },
};

// /step-health shape: Record<stepId, {status, startedAt}[]> (last N outcomes, newest first).
const STEP_HEALTH = {
  'step-flaky': [
    { status: 'Failed', startedAt: '2026-06-17T10:00:00Z' },
    { status: 'Succeeded', startedAt: '2026-06-16T10:00:00Z' },
    { status: 'Succeeded', startedAt: '2026-06-15T10:00:00Z' },
    { status: 'Failed', startedAt: '2026-06-14T10:00:00Z' },
  ],
  'step-solid': [
    { status: 'Succeeded', startedAt: '2026-06-17T10:00:00Z' },
    { status: 'Succeeded', startedAt: '2026-06-16T10:00:00Z' },
  ],
};

async function mockEditor(page: import('@playwright/test').Page) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
}

async function openEditor(page: import('@playwright/test').Page) {
  await seedExpertMode(page);
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 15_000 });
}

test.describe('Step Stats & Step Health (Teil 51)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await mockEditor(page);
  });

  test('51.1 — editor requests step-stats (windowDays=30) and renders perf annotations on hover', async ({ page }) => {
    let statsUrl: string | null = null;
    await page.route('**/api/workflows/*/step-stats**', (route) => {
      statsUrl = route.request().url();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_STATS) });
    });
    // health mock so its absence doesn't change the tooltip's presence expectations.
    await page.route('**/api/workflows/*/step-health**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_HEALTH) }),
    );

    await openEditor(page);

    // The step-stats query fired against the right workflow + carries windowDays=30.
    await expect.poll(() => statsUrl, { timeout: 10_000 }).not.toBeNull();
    expect(statsUrl).toContain(`/workflows/${WF_ID}/step-stats`);
    expect(statsUrl).toContain('windowDays=30');

    // Hover the flaky node → tooltip surfaces avg/p95 + the 30% failure-rate annotation.
    await page.locator('.react-flow__node:has-text("Flaky Step")').hover();
    await expect(page.getByText(/avg\s+4\.2s/i)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/p95\s+9\.1s/i)).toBeVisible();
    await expect(page.getByText(/30%\s*fail/i)).toBeVisible();
  });

  test('51.2 — step-health drives the inline outcome sparkline (success + failure dots)', async ({ page }) => {
    let healthUrl: string | null = null;
    await page.route('**/api/workflows/*/step-health**', (route) => {
      healthUrl = route.request().url();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_HEALTH) });
    });
    await page.route('**/api/workflows/*/step-stats**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_STATS) }),
    );

    await openEditor(page);

    // step-health query fired with the step ids + a limit.
    await expect.poll(() => healthUrl, { timeout: 10_000 }).not.toBeNull();
    expect(healthUrl).toContain(`/workflows/${WF_ID}/step-health`);
    expect(healthUrl).toContain('stepIds=');
    expect(healthUrl).toContain('limit=');

    // The flaky node's sparkline renders 4 dots: 2 failed (red) + 2 succeeded (green).
    const flaky = page.locator('.react-flow__node:has-text("Flaky Step")');
    await expect(flaky.locator('div.bg-red-400')).toHaveCount(2, { timeout: 10_000 });
    await expect(flaky.locator('div.bg-green-400')).toHaveCount(2);

    // The solid node only has succeeded outcomes → all green, no red dots.
    const solid = page.locator('.react-flow__node:has-text("Solid Step")');
    await expect(solid.locator('div.bg-green-400')).toHaveCount(2);
    await expect(solid.locator('div.bg-red-400')).toHaveCount(0);
  });

  test('51.2b — failure heatmap toggle tints the high-failure node using step-stats', async ({ page }) => {
    await page.route('**/api/workflows/*/step-stats**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_STATS) }),
    );
    await page.route('**/api/workflows/*/step-health**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_HEALTH) }),
    );

    await openEditor(page);

    // Enable the failure heatmap. Since the toolbar redesign the overlay toggles live
    // inside the "Ansicht" (view-overlays) popover as switch rows — open it first.
    await page.getByTestId('view-overlays-trigger').click();
    await page.getByTestId('toggle-failure-heatmap').click();

    // The flaky node (30% failure) gets a red-tinted heavy border. The failure colour is now
    // token-driven (`color-mix(in srgb, var(--color-error) …%, transparent)`) instead of the
    // old rgba(220,38,38) literal; the solid node (0%) carries no --color-error tint.
    const flaky = page.locator('.react-flow__node:has-text("Flaky Step")');
    await expect(flaky.locator('[style*="--color-error"]').first()).toBeVisible({ timeout: 10_000 });

    // The solid node has zero failures → no red failure tint applied.
    const solid = page.locator('.react-flow__node:has-text("Solid Step")');
    await expect(solid.locator('[style*="--color-error"]')).toHaveCount(0);
  });
});

test.describe('Step Stats & Step Health — Viewer access (Teil 51.3)', () => {
  test('51.3 — a Viewer still loads step-stats + step-health (read-only analytics)', async ({ page }) => {
    await installDefaultMocks(page);
    await mockEditor(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: MOCK_USER.id, username: 'viewer1', role: 'Viewer' }),
      }),
    );

    let statsCalls = 0;
    let healthCalls = 0;
    await page.route('**/api/workflows/*/step-stats**', (route) => {
      statsCalls += 1;
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_STATS) });
    });
    await page.route('**/api/workflows/*/step-health**', (route) => {
      healthCalls += 1;
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STEP_HEALTH) });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 15_000 });

    // Both analytics endpoints were queried for the Viewer, and the sparkline (read-only) renders.
    await expect.poll(() => statsCalls, { timeout: 10_000 }).toBeGreaterThan(0);
    await expect.poll(() => healthCalls, { timeout: 10_000 }).toBeGreaterThan(0);
    await expect(page.locator('.react-flow__node:has-text("Flaky Step") div.bg-red-400')).toHaveCount(2, { timeout: 10_000 });
  });
});
