import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 30 — Coverage Heatmap, plus Teil 42 — Coverage Heatmap Erweiterte Parameter.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * The preview build resolves i18n from the browser locale (renders EN under Playwright), so
 * selectors stay language-agnostic / bilingual.
 *
 * The heatmap lives inside the workflow editor (designer). The toolbar carries a Target-icon
 * toggle (`data-testid="toggle-coverage-heatmap"`). When on, `useCoverageHeatmap` fetches
 * `/api/workflows/{id}/coverage?windowDays=N` and stamps `__coverage` onto each activity node,
 * which ActivityNode.tsx renders as:
 *   - `never`  → wrapper gets `opacity-40 grayscale`
 *   - `rare`   → wrapper gets `opacity-80`
 *   - `common` → no tint
 * and a `title="Coverage (Nd): executed/total runs reached this step"` on the node.
 *
 * To make the editor open in an editable (non-read-only) state we mark the workflow as
 * locked-by-me (`checkedOutByUserId === MOCK_USER.id`) and give it a definitionJson whose node
 * ids match the coverage response stepIds.
 */

const WF_ID = 'cccccccc-0030-0030-0030-coverage00030';

// Three activity nodes spanning all three coverage classes after we feed the matching counts.
const DEFINITION = JSON.stringify({
  nodes: [
    { id: 'step-common', type: 'activity', position: { x: 80, y: 80 },
      data: { label: 'Common Step', activityType: 'log', config: {} } },
    { id: 'step-rare', type: 'activity', position: { x: 80, y: 240 },
      data: { label: 'Rare Step', activityType: 'log', config: {} } },
    { id: 'step-never', type: 'activity', position: { x: 80, y: 400 },
      data: { label: 'Never Step', activityType: 'log', config: {} } },
  ],
  edges: [
    { id: 'e1', source: 'step-common', target: 'step-rare', type: 'labeled', data: {} },
    { id: 'e2', source: 'step-rare', target: 'step-never', type: 'labeled', data: {} },
  ],
});

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Coverage',
    description: 'coverage heatmap e2e fixture',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked-by-me → editable State B so the toolbar is fully live
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: DEFINITION,
    version: 1,
    activityCount: 3,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  });
}

// windowDays=30 default: common reached 80/100, rare 5/100 (<25%), never 0/100.
function coverageResponse(windowDays: number, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    workflowId: WF_ID,
    windowDays,
    totalExecutions: 100,
    oldestExecutionInWindow: '2026-05-19T00:00:00.000Z',
    nodes: [
      { stepId: 'step-common', executedCount: 80, failedCount: 2, skippedCount: 20,
        lastExecutedAt: '2026-06-17T00:00:00.000Z', lastSucceededAt: '2026-06-17T00:00:00.000Z', lastFailedAt: '2026-06-10T00:00:00.000Z' },
      { stepId: 'step-rare', executedCount: 5, failedCount: 1, skippedCount: 95,
        lastExecutedAt: '2026-06-01T00:00:00.000Z', lastSucceededAt: '2026-06-01T00:00:00.000Z', lastFailedAt: '2026-05-20T00:00:00.000Z' },
      { stepId: 'step-never', executedCount: 0, failedCount: 0, skippedCount: 100,
        lastExecutedAt: null, lastSucceededAt: null, lastFailedAt: null },
    ],
    ...overrides,
  });
}

async function openEditor(page: import('@playwright/test').Page) {
  await seedExpertMode(page); // heatmap toggle lives in the expert-mode toolbar (default is standard)
  await page.goto(`/workflows/${WF_ID}`);
  // Editor mounted once the React Flow canvas + our three nodes are present.
  await expect(page.locator('.react-flow__node').first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('.react-flow__node')).toHaveCount(3, { timeout: 15_000 });
}

/** The coverage toggle lives inside the "Ansicht" (view-overlays) popover since the
 *  toolbar redesign — open the popover first if the switch row isn't mounted yet. */
async function coverageToggle(page: import('@playwright/test').Page) {
  const toggle = page.getByTestId('toggle-coverage-heatmap');
  if (!(await toggle.isVisible().catch(() => false))) {
    await page.getByTestId('view-overlays-trigger').click();
    await expect(toggle).toBeVisible();
  }
  return toggle;
}

test.describe('Coverage Heatmap (Teil 30)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
  });

  test('30.1 — toggling the heatmap tints nodes by coverage class, toggling off clears it', async ({ page }) => {
    let coverageCalls = 0;
    await page.route('**/api/workflows/*/coverage**', (route) => {
      coverageCalls += 1;
      return route.fulfill({ status: 200, contentType: 'application/json', body: coverageResponse(30) });
    });

    await openEditor(page);

    const toggle = await coverageToggle(page);

    // Before toggling on: no node carries a Coverage tooltip.
    await expect(page.locator('[title^="Coverage ("]')).toHaveCount(0);

    await toggle.click();

    // The API was queried, and the never-run node grabbed the grayscale dim treatment.
    await expect.poll(() => coverageCalls, { timeout: 10_000 }).toBeGreaterThan(0);
    // `never` → opacity-40 + grayscale applied to the node wrapper.
    await expect(page.locator('.react-flow__node:has-text("Never Step") .grayscale')).toHaveCount(1, { timeout: 10_000 });
    // Three coverage tooltips now present (one per activity node), and the common one reads 80/100.
    await expect(page.locator('[title^="Coverage ("]')).toHaveCount(3);
    await expect(page.locator('[title*="80/100 runs reached this step"]').first()).toBeVisible();

    // Toggle off → tint + tooltips disappear.
    await toggle.click();
    await expect(page.locator('[title^="Coverage ("]')).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('.react-flow__node:has-text("Never Step") .grayscale')).toHaveCount(0);
  });

  test('30.2 — hover tooltips reflect per-step counts (executed/total, windowDays)', async ({ page }) => {
    await page.route('**/api/workflows/*/coverage**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: coverageResponse(30) }),
    );

    await openEditor(page);
    await (await coverageToggle(page)).click();

    // Each class surfaces its own count string. The window is reflected as "(30d)".
    await expect(page.locator('[title="Coverage (30d): 80/100 runs reached this step"]')).toHaveCount(1, { timeout: 10_000 });
    await expect(page.locator('[title="Coverage (30d): 5/100 runs reached this step"]')).toHaveCount(1);
    await expect(page.locator('[title="Coverage (30d): 0/100 runs reached this step"]')).toHaveCount(1);
  });

  test('30.3 — coverage request carries the default windowDays=30', async ({ page }) => {
    let requestedUrl: string | null = null;
    await page.route('**/api/workflows/*/coverage**', (route) => {
      requestedUrl = route.request().url();
      return route.fulfill({ status: 200, contentType: 'application/json', body: coverageResponse(30) });
    });

    await openEditor(page);
    await (await coverageToggle(page)).click();

    await expect.poll(() => requestedUrl, { timeout: 10_000 }).not.toBeNull();
    expect(requestedUrl).toContain('windowDays=30');
    expect(requestedUrl).toContain(`/workflows/${WF_ID}/coverage`);
  });
});

test.describe('Coverage Heatmap — Erweiterte Parameter (Teil 42)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
  });

  test('42.1 — windowDays=365 response renders with oldestExecutionInWindow populated', async ({ page }) => {
    // The client always requests windowDays=30 (no UI to change it), but the server may cap/clamp
    // and echo back a different window. We assert the UI faithfully renders whatever windowDays +
    // counts the response carries — here a 365-day window with a year-old oldest execution.
    await page.route('**/api/workflows/*/coverage**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: coverageResponse(365, { oldestExecutionInWindow: '2025-06-18T00:00:00.000Z' }),
      }),
    );

    await openEditor(page);
    await (await coverageToggle(page)).click();

    // The node tooltips reflect the response's windowDays (365d), proving the value round-trips.
    await expect(page.locator('[title="Coverage (365d): 80/100 runs reached this step"]')).toHaveCount(1, { timeout: 10_000 });
    await expect(page.locator('[title="Coverage (365d): 0/100 runs reached this step"]')).toHaveCount(1);
  });

  test('42.2 — windowDays=0 (empty window) renders every step as never-run', async ({ page }) => {
    // Degenerate window: no executions at all → totalExecutions 0, every node "never".
    await page.route('**/api/workflows/*/coverage**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          workflowId: WF_ID,
          windowDays: 7,
          totalExecutions: 0,
          oldestExecutionInWindow: null,
          nodes: [
            { stepId: 'step-common', executedCount: 0, failedCount: 0, skippedCount: 0, lastExecutedAt: null, lastSucceededAt: null, lastFailedAt: null },
            { stepId: 'step-rare', executedCount: 0, failedCount: 0, skippedCount: 0, lastExecutedAt: null, lastSucceededAt: null, lastFailedAt: null },
            { stepId: 'step-never', executedCount: 0, failedCount: 0, skippedCount: 0, lastExecutedAt: null, lastSucceededAt: null, lastFailedAt: null },
          ],
        }),
      }),
    );

    await openEditor(page);
    await (await coverageToggle(page)).click();

    // All three nodes never ran → all grayscale-dimmed, all tooltips show 0/0.
    await expect(page.locator('.react-flow__node .grayscale')).toHaveCount(3, { timeout: 10_000 });
    await expect(page.locator('[title="Coverage (7d): 0/0 runs reached this step"]')).toHaveCount(3);
  });
});
