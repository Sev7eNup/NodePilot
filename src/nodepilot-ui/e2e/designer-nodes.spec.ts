import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 3 — Node-Operationen & Designer-Interaktionen (lines 465-557).
 *
 * Hermetic: page.route() mocks only (predicate catch-all from fixtures/mockApi.ts).
 * The workflow is mocked locked-by-me (checkedOutByUserId === MOCK_USER.id) so the editor
 * opens in State B (editable) — context menus, palette add, delete-key, Save all live.
 *
 * React-Flow canvas drag (d3-drag) is NOT synthesizable with Playwright mouse events, so:
 *   - 3.3 (drag a node to a new position) → covered via keyboard nudge (ArrowKeys), which the
 *     editor binds to `nudgeSelectedNodes`; the literal mouse-drag is skipped with a reason.
 *   - 3.5 (marquee/drag-box select) → covered via Ctrl+A select-all → BulkEditPanel; the
 *     literal marquee drag is skipped with a reason.
 *
 * The SPA renders ENGLISH under Playwright → language-agnostic selectors (role + bilingual
 * regex / attribute) only.
 */

const WF_ID = 'd3d3d3d3-3333-3333-3333-333333333333';

const NODE_A = 'step-aaaaaaaa';
const NODE_B = 'step-bbbbbbbb';

/** Two activity nodes + one edge between them, so delete/edge-cleanup is observable. */
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

// Click near a node's top-left corner — keeps the click point clear of the bottom-right
// MiniMap / bottom-left Controls overlays that otherwise intercept a centered click.
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

    await seedExpertMode(page); // context-menu / bulk-select tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    await node(page, NODE_A).click({ button: 'right', position: { x: 15, y: 15 } });
    const menu = page.locator('div.z-30').filter({ has: page.getByRole('button', { name: /^(duplicate|duplizieren)$/i }) });
    await expect(menu).toBeVisible({ timeout: 5_000 });
    await menu.getByRole('button', { name: /^(duplicate|duplizieren)$/i }).click();

    // The duplicate appears under a fresh step-<uuid> id (visible alongside the original).
    await expect(page.locator(`.react-flow__node[data-id^="step-"]:not([data-id="${NODE_A}"]):not([data-id="${NODE_B}"])`))
      .toHaveCount(1, { timeout: 10_000 });

    // Save persists. The PUT body is the source of truth for the graph (the canvas DOM is
    // virtualized via onlyRenderVisibleElements). It must carry the original three nodes:
    // two "First Script" (original + copy) with distinct ids, plus the Delay.
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

    await seedExpertMode(page); // context-menu / bulk-select tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await expect(page.locator('.react-flow__edge')).toHaveCount(1);

    await node(page, NODE_A).click({ button: 'right', position: { x: 15, y: 15 } });
    const menu = page.locator('div.z-30').filter({ has: page.getByRole('button', { name: /^(delete|löschen)$/i }) });
    await expect(menu).toBeVisible({ timeout: 5_000 });
    await menu.getByRole('button', { name: /^(delete|löschen)$/i }).click();

    // Node A gone → one node left; the edge that touched A is gone too.
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
    test.skip(false, 'mouse-drag move is not synthesizable in React Flow; keyboard nudge covers persistence');
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // context-menu / bulk-select tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Select node A (leftmost → its center is clear of the bottom-right MiniMap), then nudge it
    // with arrow keys. The editor binds arrows → nudgeSelectedNodes (≥1px/step). This is the
    // synthesizable equivalent of "drag the node to a new position".
    await node(page, NODE_A).click();
    await expect(node(page, NODE_A)).toHaveClass(/selected/, { timeout: 5_000 });
    for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowDown');

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { id: string; position: { x: number; y: number } }[] };
    const moved = def.nodes.find((n) => n.id === NODE_A)!;
    // Started at x:60,y:60 — the ArrowRight presses + one ArrowDown moved it right and down.
    expect(moved.position.x).toBeGreaterThan(60);
    expect(moved.position.y).toBeGreaterThan(60);
  });

  test('3.4 — canvas zoom in/out via React Flow controls changes the viewport transform', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await seedExpertMode(page); // context-menu / bulk-select tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    const viewport = page.locator('.react-flow__viewport');
    const transformBefore = await viewport.getAttribute('style');

    // The Controls cluster renders zoom-in / zoom-out buttons. Zoom OUT first: after the
    // load-time fitView a small 2-node graph can already sit near max zoom (zoom-in disabled),
    // but zoom-out is reliably available.
    const zoomIn = page.locator('.react-flow__controls-zoomin');
    const zoomOut = page.locator('.react-flow__controls-zoomout');
    await expect(zoomIn).toBeVisible();
    await expect(zoomOut).toBeVisible();

    await zoomOut.click();
    await zoomOut.click();
    await expect.poll(async () => viewport.getAttribute('style'), { timeout: 5_000 }).not.toBe(transformBefore);

    // UI stays responsive — nodes still present after zooming back in.
    await expect(page.locator('.react-flow__node')).toHaveCount(2);
    await zoomIn.click();
    await expect(page.locator('.react-flow__node')).toHaveCount(2);
  });

  test('3.5 — multi-select via Ctrl+A opens the bulk-edit panel (marquee drag skipped)', async ({ page }) => {
    test.skip(false, 'marquee drag-box is not synthesizable in React Flow; Ctrl+A covers multi-select');
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await seedExpertMode(page); // context-menu / bulk-select tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Select a node first so the canvas (not an input) has focus, then Ctrl+A → select all.
    await node(page, NODE_A).click(TL);
    await expect(node(page, NODE_A)).toHaveClass(/selected/, { timeout: 5_000 });
    await page.keyboard.press('Control+a');

    // 2+ selected → the editor swaps the right panel to the BulkEditPanel. That panel is the
    // authoritative multi-select signal. (We don't assert the second node's `selected` class:
    // the panel narrows the canvas and onlyRenderVisibleElements can virtualize the rightmost
    // node out of the DOM — a render optimization, not a selection failure.)
    await expect(page.getByRole('heading', { name: /bulk edit|mehrfach/i })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/2\s+activit(y|ies)/i).first()).toBeVisible();
    // Bulk actions are available (each field has its own Apply button).
    await expect(page.getByRole('button', { name: /apply|anwenden/i }).first()).toBeVisible();
  });
});
