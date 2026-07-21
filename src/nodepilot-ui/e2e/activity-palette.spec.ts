import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Part 68 — Activity Palette & Node Context Menu (lines 3803-3840).
 *
 * The "activity palette" in NodePilot is the left-sidebar Node Library (EditorSidebar →
 * buildActivityCategories): a categorized, searchable list of activities. Clicking an entry
 * calls `addNode` and drops a fresh node onto the canvas (the same code path the drag-drop
 * uses, minus the d3-drag which Playwright cannot synthesize). The ActivityPickerGrid
 * component itself is a centered/positioned variant used by EdgeInserter + QuickConnectPicker
 * (covered in quick-interactions.spec.ts) — the editor has no pane-right-click → picker, so
 * 68.1's "right-click on canvas" is exercised here as the sidebar palette + click-add.
 *
 * The node context menu (right-click an activity node → NodeContextMenu) carries
 * Duplicate / Enable-Disable / Add-Remove-Breakpoint / Delete. Outside-click + Escape close it.
 *
 * Hermetic: page.route mocks only. Workflow is locked-by-me (State B) so the palette's
 * click-add and the context-menu actions are all live. SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'e6868686-6868-6868-6868-686868686868';
const NODE_A = 'step-aaaa6868';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: NODE_A, type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'First Script', activityType: 'runScript', config: { script: 'Get-Date' } } },
    ],
    edges: [],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Palette',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(),
    version: 1,
  });
}

async function openEditor(page: Page, onPut?: (body: { definitionJson?: string }) => void) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) => {
    if (route.request().method() === 'PUT') {
      onPut?.(route.request().postDataJSON());
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
  });
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator(`.react-flow__node[data-id="${NODE_A}"]`)).toBeVisible({ timeout: 20_000 });
}

const saveButton = (page: Page) =>
  page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();

test.describe('Activity-Palette & Node-Kontext-Menü (Teil 68)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('68.1 — palette lists categorized activities, search filters, click-add inserts a node', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });

    // Open the Node Library tab (the categorized activity palette). The tab button's
    // innerText includes its icon glyph, so match loosely on the label text.
    await page.locator('aside button').filter({ hasText: /Nodes|Knoten/ }).first().click();
    // The expanded panel has no "Node Library" heading — its search box confirms it mounted.
    await expect(page.getByPlaceholder(/search nodes|nodes suchen/i)).toBeVisible();

    // Categorized: category headers render. Some categories ("Actions") are collapsed by
    // default, so we drive discovery via the search box, which force-expands matches.
    await expect(page.getByRole('button').filter({ hasText: /Triggers|Auslöser/i }).first()).toBeVisible();

    // Search narrows the list: "log" surfaces the Log Message entry and hides Run Script.
    const search = page.getByPlaceholder(/search|suchen/i).first();
    await search.fill('log');
    await expect(page.getByRole('button').filter({ hasText: /Log Message/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /Run Script/ })).toHaveCount(0);

    // Switch the search to "delay" → the Delay entry shows and Log Message is gone.
    await search.fill('delay');
    await expect(page.getByRole('button').filter({ hasText: /Delay/ }).first()).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /Log Message/ })).toHaveCount(0);

    // Click-add: clicking the Delay entry inserts a second node (a fresh step-<uuid>).
    await page.getByRole('button').filter({ hasText: /Delay/ }).first().click();
    await expect(page.locator(`.react-flow__node[data-id^="step-"]:not([data-id="${NODE_A}"])`))
      .toHaveCount(1, { timeout: 10_000 });

    // Save persists both nodes (canvas DOM is virtualized — the PUT body is the source of truth).
    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { activityType?: string; data?: { activityType?: string } }[] };
    expect(def.nodes).toHaveLength(2);
    const types = def.nodes.map((n) => n.data?.activityType ?? n.activityType);
    expect(types).toContain('runScript');
    expect(types).toContain('delay');
  });

  test('68.2 — node context menu shows Duplicate / Disable / Breakpoint / Delete; Escape closes', async ({ page }) => {
    // The breakpoint entry is expert-only; seed expert mode so the full menu renders.
    await seedExpertMode(page);
    await openEditor(page);

    await page.locator(`.react-flow__node[data-id="${NODE_A}"]`).click({ button: 'right', position: { x: 15, y: 15 } });

    // All four documented actions are present.
    await expect(page.getByRole('button', { name: /^Duplicate$/ })).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole('button', { name: /Disable step|Enable step/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Add breakpoint|Remove breakpoint/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /^Delete$/ })).toBeVisible();

    // Escape closes the menu without invoking anything.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: /^Duplicate$/ })).toHaveCount(0);
  });

  test('68.2b — context-menu Disable greys the node and the menu label flips to Enable', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });

    // Disable via the menu.
    await page.locator(`.react-flow__node[data-id="${NODE_A}"]`).click({ button: 'right', position: { x: 15, y: 15 } });
    await page.getByRole('button', { name: /Disable step/ }).click();

    // Re-open the menu — the toggle now reads "Enable step", proving the disabled flag flipped.
    await page.locator(`.react-flow__node[data-id="${NODE_A}"]`).click({ button: 'right', position: { x: 15, y: 15 } });
    await expect(page.getByRole('button', { name: /Enable step/ })).toBeVisible({ timeout: 5_000 });
    await page.keyboard.press('Escape');

    // Save → disabled:true persisted on the node.
    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { data: { disabled?: boolean } }[] };
    expect(def.nodes[0].data.disabled).toBe(true);
  });

  test('68.2c — context-menu Delete removes the node', async ({ page }) => {
    await openEditor(page);

    await page.locator(`.react-flow__node[data-id="${NODE_A}"]`).click({ button: 'right', position: { x: 15, y: 15 } });
    await page.getByRole('button', { name: /^Delete$/ }).click();

    await expect(page.locator('.react-flow__node')).toHaveCount(0, { timeout: 10_000 });
  });
});
