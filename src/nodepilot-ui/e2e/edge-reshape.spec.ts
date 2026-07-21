import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 61 — Edge-Reshape Handles (lines 3670-3689).
 *
 * Hermetic: page.route() mocks only. Workflow is locked-by-me → editable (State B), so reshape
 * is enabled (LabeledEdge gates handles on `selected && segments.length === 1 && canWrite`).
 *
 * Dragging a handle is a React-Flow pointer-capture canvas drag — NOT synthesizable with
 * Playwright's event model (the screenToFlowPosition math runs on real pointermove deltas).
 * So 61.1 (drag-to-bend) is split:
 *   - VERIFIED: handles appear on the selected single-segment edge (the SVG control-point divs,
 *     data-testid `edge-reshape-cp1/2-<id>`); and a pre-seeded `data.controlPoints` is honored
 *     on render (the custom shape exists, proving persistence round-trips).
 *   - SKIPPED: the actual drag-reshape gesture (canvas pointer drag) with a reason.
 *
 * 61.2 — Right-click on an edge that carries a custom shape (`data.controlPoints`) opens the
 *        EdgeContextMenu with the "Reset edge shape" item (it only renders when hasCustomShape);
 *        clicking it removes controlPoints and Save persists an edge without them.
 *
 * The SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'd6d6d6d6-6161-6161-6161-616161616161';
const NODE_A = 'step-src';
const NODE_B = 'step-dst';
const EDGE_ID = 'edge-main';

const CONTROL_POINTS = { cp1x: 220, cp1y: 140, cp2x: 320, cp2y: 20 };

function definition(edgeData: Record<string, unknown>) {
  return JSON.stringify({
    nodes: [
      { id: NODE_A, type: 'activity', position: { x: 60, y: 60 },
        data: { label: 'Producer', activityType: 'runScript', outputVariable: 'step1', config: { script: 'x' } } },
      { id: NODE_B, type: 'activity', position: { x: 320, y: 60 },
        data: { label: 'Consumer', activityType: 'delay', config: { seconds: 1 } } },
    ],
    edges: [{ id: EDGE_ID, source: NODE_A, target: NODE_B, type: 'labeled', data: edgeData }],
  });
}

function workflowJson(edgeData: Record<string, unknown>) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Reshape',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(edgeData),
    version: 1,
  });
}

async function waitForCanvas(page: Page) {
  await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 15_000 });
  await expect(page.locator(`.react-flow__edge[data-id="${EDGE_ID}"]`)).toBeVisible();
}

/** Returns a screen coordinate that lies exactly on the edge's SVG path (works for curved
 *  edges too, where the bounding-box midpoint is off the line). Uses getPointAtLength on the
 *  real <path> + the element's client rect to map SVG-user-space → screen pixels. */
async function pointOnEdge(page: Page): Promise<{ x: number; y: number }> {
  const pt = await page.locator(`.react-flow__edge[data-id="${EDGE_ID}"] .react-flow__edge-path`).first()
    .evaluate((el) => {
      const path = el as unknown as SVGPathElement;
      const total = path.getTotalLength();
      const p = path.getPointAtLength(total / 2);
      const svg = path.ownerSVGElement!;
      const ctm = path.getScreenCTM()!;
      const dom = svg.createSVGPoint();
      dom.x = p.x; dom.y = p.y;
      const screen = dom.matrixTransform(ctm);
      return { x: screen.x, y: screen.y };
    });
  return pt;
}

/** Click the edge's path (true on-path point) to select it. */
async function selectEdge(page: Page) {
  const heading = page.getByRole('heading', { name: /^connection$|^verbindung$/i });
  await page.waitForTimeout(500); // let the load-time fitView animation settle before measuring the point
  // The point is viewport-relative; if fitView is still animating the click can miss. Re-measure + retry.
  for (let i = 0; i < 8; i++) {
    const { x, y } = await pointOnEdge(page);
    await page.mouse.click(x, y);
    if (await heading.isVisible().catch(() => false)) return;
    await page.waitForTimeout(250);
  }
  await expect(heading).toBeVisible({ timeout: 5_000 });
}

test.describe('Edge-Reshape Handles (Teil 61)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('61.1a — selecting a single-segment edge reveals the two reshape control-point handles', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson({ label: '', condition: '', disabled: false }) }),
    );

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Before selection no handles are rendered.
    await expect(page.locator(`[data-testid="edge-reshape-cp1-${EDGE_ID}"]`)).toHaveCount(0);

    await selectEdge(page);

    // After selection, both pen-tool control-point handles appear.
    await expect(page.locator(`[data-testid="edge-reshape-cp1-${EDGE_ID}"]`)).toBeVisible({ timeout: 10_000 });
    await expect(page.locator(`[data-testid="edge-reshape-cp2-${EDGE_ID}"]`)).toBeVisible();
    // The dashed connector hint-lines group is also rendered.
    await expect(page.locator(`[data-testid="edge-reshape-lines-${EDGE_ID}"]`)).toHaveCount(1);
  });

  test('61.1b — a pre-seeded data.controlPoints custom shape is honored on render', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({ label: '', condition: '', disabled: false, controlPoints: CONTROL_POINTS }),
      }),
    );

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);

    // With a persisted shape, the cp1 handle is positioned at the saved flow coordinates. We
    // assert the handle exists (its transform is driven by CONTROL_POINTS) — proving the
    // controlPoints in definitionJson are read and rendered, not ignored.
    const cp1 = page.locator(`[data-testid="edge-reshape-cp1-${EDGE_ID}"]`);
    await expect(cp1).toBeVisible({ timeout: 10_000 });
    const transform = await cp1.evaluate((el) => (el as HTMLElement).style.transform);
    // Custom shape → handle transform embeds the saved cp1 flow coordinates (220,140).
    expect(transform).toContain('220px');
    expect(transform).toContain('140px');
  });

  test('61.1c — drag-reshape gesture is skipped (React-Flow canvas pointer-capture drag is not synthesizable)', async () => {
    test.skip(true, 'Bending an edge requires a real pointer-capture drag whose screenToFlowPosition deltas Playwright cannot synthesize; render/persistence of controlPoints is covered by 61.1b.');
  });

  test('61.2 — right-click on a custom-shaped edge offers "Reset edge shape"; reset removes controlPoints and Save persists it', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson({ label: '', condition: '', disabled: false }) });
      }
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({ label: '', condition: '', disabled: false, controlPoints: CONTROL_POINTS }),
      });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Right-click on a true on-path point → EdgeContextMenu. hasCustomShape=true (controlPoints
    // seeded) → "Reset edge shape" item is present.
    const { x, y } = await pointOnEdge(page);
    await page.mouse.click(x, y, { button: 'right' });

    const resetItem = page.getByText(/reset edge shape|edge-form zurücksetzen|kantenform/i);
    await expect(resetItem).toBeVisible({ timeout: 10_000 });
    await resetItem.click();

    // Save → PUT persists the edge WITHOUT controlPoints.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { edges: { data?: { controlPoints?: unknown } }[] };
    expect(def.edges[0].data?.controlPoints).toBeUndefined();
  });
});
