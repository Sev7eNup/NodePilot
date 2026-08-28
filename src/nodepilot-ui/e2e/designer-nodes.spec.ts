import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md part 3 — node operations and designer interactions (lines 465-557).
 *
 * Hermetic: page.route() mocks only (predicate catch-all from fixtures/mockApi.ts). The workflow
 * is mocked as checked out by the current user, so the editor opens editable.
 *
 * React-Flow canvas drag (d3-drag) cannot be synthesized with Playwright mouse events, so 3.3
 * covers node movement with the keyboard nudge and 3.5 covers multi-select with Ctrl+A; the
 * literal drags are skipped with a reason. The SPA renders English under Playwright, so
 * selectors stay language-agnostic (role plus bilingual regex or attribute).
 */

const WF_ID = 'd3d3d3d3-3333-3333-3333-333333333333';

const NODE_A = 'step-aaaaaaaa';
const NODE_B = 'step-bbbbbbbb';

/** Two activity nodes and one edge between them, so delete and edge cleanup are observable. */
function definition() {
  return JSON.stringify({
    nodes: [
      {
        id: NODE_A,
        type: 'activity',
        position: { x: 60, y: 60 },
        data: { label: 'First Script', activityType: 'runScript', config: { script: 'Get-Date' } },
      },
      {
        id: NODE_B,
        type: 'activity',
        position: { x: 300, y: 60 },
        data: { label: 'Second Delay', activityType: 'delay', config: { seconds: 5 } },
      },
    ],
    edges: [
      {
        id: 'edge-ab',
        source: NODE_A,
        target: NODE_B,
        type: 'labeled',
        data: { label: 'On Success', condition: `${NODE_A}.success`, disabled: false },
      },
    ],
  });
}

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-NodeOps',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(),
    version: 1,
    ...overrides,
  });
}

/** Wait until both seeded nodes have rendered into the canvas. */
async function waitForCanvas(page: Page) {
  await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 15_000 });
}

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

// Click near a node's top-left corner to keep the click point clear of the bottom-right
// MiniMap and bottom-left Controls overlays, which otherwise intercept a centered click.
const TL = { position: { x: 15, y: 15 } } as const;

test.describe('Designer Node-Operationen (Teil 3)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('3.1 — right-click → Duplicate creates a second node with a fresh id, then Save PUTs', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // context menu and bulk select live in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    await node(page, NODE_A).click({ button: 'right', position: { x: 15, y: 15 } });
    const menu = page.locator('div.z-30').filter({ has: page.getByRole('button', { name: /^(duplicate|duplizieren)$/i }) });
    await expect(menu).toBeVisible({ timeout: 5_000 });
    await menu.getByRole('button', { name: /^(duplicate|duplizieren)$/i }).click();

    // The duplicate appears under a fresh step-<uuid> id (visible alongside the original).
    await expect(page.locator(`.react-flow__node[data-id^="step-"]:not([data-id="${NODE_A}"]):not([data-id="${NODE_B}"])`))
      .toHaveCount(1, { timeout: 10_000 });

    // The PUT body is the source of truth for the graph, because the canvas DOM is virtualized
    // via onlyRenderVisibleElements. It must carry three nodes: two "First Script" ones with
    // distinct ids, plus the Delay.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { id: string; data: { label: string } }[] };
    expect(def.nodes).toHaveLength(3);
    const scriptNodes = def.nodes.filter((n) => n.data.label === 'First Script');
    expect(scriptNodes).toHaveLength(2);                          // properties copied
    expect(new Set(scriptNodes.map((n) => n.id)).size).toBe(2);  // unique ids
    expect(scriptNodes.some((n) => n.id === NODE_A)).toBe(true);  // original kept
    expect(scriptNodes.some((n) => n.id !== NODE_A)).toBe(true);  // copy has a new id
  });

  test('3.2 — right-click → Delete removes the node and its touching edge', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    const consoleErrors: string[] = [];
    page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });

    await seedExpertMode(page); // context menu and bulk select live in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await expect(page.locator('.react-flow__edge')).toHaveCount(1);

    await node(page, NODE_A).click({ button: 'right', position: { x: 15, y: 15 } });
    const menu = page.locator('div.z-30').filter({ has: page.getByRole('button', { name: /^(delete|löschen)$/i }) });
    await expect(menu).toBeVisible({ timeout: 5_000 });
    await menu.getByRole('button', { name: /^(delete|löschen)$/i }).click();

    // Node A is gone, leaving one node, and the edge that touched A is gone with it.
    await expect(page.locator('.react-flow__node')).toHaveCount(1, { timeout: 10_000 });
    await expect(page.locator('.react-flow__edge')).toHaveCount(0);
    await expect(node(page, NODE_B)).toBeVisible();

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: unknown[]; edges: unknown[] };
    expect(def.nodes).toHaveLength(1);
    expect(def.edges).toHaveLength(0);

    // No React render crashes during the delete.
    expect(consoleErrors.join('\n')).not.toMatch(/Cannot read|is not a function|Maximum update depth/i);
  });

  test('3.3 — node position is saved (keyboard nudge; literal mouse-drag skipped)', async ({ page }) => {
    // Scope note: a mouse-drag move cannot be synthesized in React Flow, so the keyboard
    // nudge covers position persistence.
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // context menu and bulk select live in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Select node A, the leftmost one, so its center stays clear of the bottom-right MiniMap.
    // The editor binds the arrow keys to nudgeSelectedNodes, which is the synthesizable
    // equivalent of dragging the node to a new position.
    await node(page, NODE_A).click();
    await expect(node(page, NODE_A)).toHaveClass(/selected/, { timeout: 5_000 });
    for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowDown');

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { id: string; position: { x: number; y: number } }[] };
    const moved = def.nodes.find((n) => n.id === NODE_A)!;
    // The node starts at x:60,y:60; the arrow presses move it right and down.
    expect(moved.position.x).toBeGreaterThan(60);
    expect(moved.position.y).toBeGreaterThan(60);
  });

  test('3.4 — canvas zoom in/out via React Flow controls changes the viewport transform', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await seedExpertMode(page); // context menu and bulk select live in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    const viewport = page.locator('.react-flow__viewport');
    const transformBefore = await viewport.getAttribute('style');

    // The Controls cluster renders zoom-in and zoom-out buttons. Zoom out first: after the
    // load-time fitView a small two-node graph can already sit near max zoom, which disables
    // zoom-in, while zoom-out is always available.
    const zoomIn = page.locator('.react-flow__controls-zoomin');
    const zoomOut = page.locator('.react-flow__controls-zoomout');
    await expect(zoomIn).toBeVisible();
    await expect(zoomOut).toBeVisible();

    await zoomOut.click();
    await zoomOut.click();
    await expect.poll(async () => viewport.getAttribute('style'), { timeout: 5_000 }).not.toBe(transformBefore);

    // The UI stays responsive: the nodes are still present after zooming back in.
    await expect(page.locator('.react-flow__node')).toHaveCount(2);
    await zoomIn.click();
    await expect(page.locator('.react-flow__node')).toHaveCount(2);
  });

  test('3.5 — multi-select via Ctrl+A opens the bulk-edit panel (marquee drag skipped)', async ({ page }) => {
    // Scope note: a marquee drag-box cannot be synthesized in React Flow, so Ctrl+A covers
    // multi-select.
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await seedExpertMode(page); // context menu and bulk select live in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Select a node first so the canvas, not an input, has focus; then Ctrl+A selects all.
    await node(page, NODE_A).click(TL);
    await expect(node(page, NODE_A)).toHaveClass(/selected/, { timeout: 5_000 });
    await page.keyboard.press('Control+a');

    // With two or more nodes selected the editor swaps the right panel to the BulkEditPanel,
    // which is the authoritative multi-select signal. The second node's `selected` class is not
    // asserted: the panel narrows the canvas and onlyRenderVisibleElements can virtualize the
    // rightmost node out of the DOM.
    await expect(page.getByRole('heading', { name: /bulk edit|mehrfach/i })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/2\s+activit(y|ies)/i).first()).toBeVisible();
    // Bulk actions are available (each field has its own Apply button).
    await expect(page.getByRole('button', { name: /apply|anwenden/i }).first()).toBeVisible();
  });
});
