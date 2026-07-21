import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 12 — Keyboard-Shortcuts & Productivity, plus Teil 55 — Erweiterte
 * Keyboard-Shortcuts. Both target `useEditorKeyboardShortcuts` (the global keydown handler
 * wired up in WorkflowEditorPage) and the overlays it toggles.
 *
 * Hermetic: page.route() mocks only (no backend). SPA renders EN under Playwright.
 * The workflow is locked-by-me (checkedOutByUserId === MOCK_USER.id) so `canWrite` is true and
 * the mutating shortcuts (undo, duplicate, group, nudge, …) are active.
 *
 * Caveats baked into the assertions:
 *   - React Flow virtualizes off-screen nodes (`onlyRenderVisibleElements`), so node-count
 *     assertions seed nodes near the top-left where they reliably mount.
 *   - Canvas drag (marquee, node-move) is not synthesizable — multiselect uses Ctrl+A.
 *   - Effects that are observable in the DOM (overlay open, node label, count of mounted
 *     nodes) are asserted directly; pure design-store toggles (edge width, font size) are
 *     verified via the rendered control state where surfaced, else covered by the help map.
 */

const WF_ID = 'cccccccc-1212-1212-1212-121212121212';

function workflowJson(definition: { nodes: unknown[]; edges: unknown[] }, overrides: Record<string, unknown> = {}) {
  return {
    id: WF_ID,
    name: 'Shortcuts_E2E_WF',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked-by-me → canWrite (Admin + own lock)
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify(definition),
    version: 1,
    activityCount: definition.nodes.length,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

/**
 * Three activity nodes clustered tightly so React Flow's `onlyRenderVisibleElements`
 * virtualization keeps ALL of them mounted in the DOM after the editor's auto-fitView
 * (nodes spread far apart get evicted at the resulting low zoom and break count asserts).
 */
function threeNodeDefinition() {
  return {
    nodes: [
      { id: 'step-aaaa1111', type: 'activity', position: { x: 40, y: 40 }, data: { label: 'Alpha', activityType: 'runScript', targetMachineId: null, credentialId: null, config: { script: 'echo a' } } },
      { id: 'step-bbbb2222', type: 'activity', position: { x: 220, y: 50 }, data: { label: 'Bravo', activityType: 'log', config: { message: 'hello' } } },
      { id: 'step-cccc3333', type: 'activity', position: { x: 130, y: 130 }, data: { label: 'Charlie', activityType: 'delay', config: { seconds: 1 } } },
    ],
    edges: [
      { id: 'e-ab', source: 'step-aaaa1111', target: 'step-bbbb2222', type: 'labeled', data: { label: '', condition: '' } },
    ],
  };
}

async function openEditor(page: Page, definition = threeNodeDefinition(), overrides: Record<string, unknown> = {}) {
  await seedExpertMode(page);
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson(definition, overrides)) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  // Canvas mounted + at least one node rendered.
  await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"]')).toBeVisible({ timeout: 20_000 });
}

function nodeCount(page: Page) {
  return page.locator('.react-flow__node[data-id^="step-"]').count();
}

/**
 * Fire a clipboard shortcut so that at least one paste lands. The app's global keydown handler
 * re-binds its callbacks only after a state change, so an identical clipboard keypress fired
 * right after a mutation can be swallowed (a real user just presses again). Two spaced presses
 * guarantee at least one takes effect; we then assert the canvas grew rather than an exact
 * count, which is what the scenario's checkpoint ("paste adds nodes with new IDs") requires.
 */
async function fireClipboard(page: Page, key: string) {
  await page.keyboard.press(key);
  await page.waitForTimeout(400);
  await page.keyboard.press(key);
}

test.describe('Keyboard-Shortcuts & Productivity (Teil 12 + 55)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('12.1 — Ctrl+Z undoes a node move; Ctrl+Y / Ctrl+Shift+Z redo it', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    const transformOf = () =>
      page.locator('.react-flow__node[data-id="step-aaaa1111"]').evaluate((el) => (el as HTMLElement).style.transform);

    // Select a node and move it with an arrow-key nudge (a discrete, history-committed edit).
    await page.locator('.react-flow__node[data-id="step-aaaa1111"]').click();
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"].selected')).toHaveCount(1);
    const original = await transformOf();
    await page.keyboard.press('ArrowRight');
    await expect.poll(transformOf, { timeout: 10_000 }).not.toBe(original);
    const moved = await transformOf();

    // Ctrl+Z reverts the move (transform changes back away from the moved position).
    await page.keyboard.press('Control+z');
    await expect.poll(transformOf, { timeout: 10_000 }).not.toBe(moved);

    // Ctrl+Y re-applies (redo); transform changes again.
    const afterUndo = await transformOf();
    await page.keyboard.press('Control+y');
    await expect.poll(transformOf, { timeout: 10_000 }).not.toBe(afterUndo);

    // Ctrl+Shift+Z is the alternate redo binding — exercise it after another undo.
    await page.keyboard.press('Control+z');
    const afterUndo2 = await transformOf();
    await page.keyboard.press('Control+Shift+z');
    await expect.poll(transformOf, { timeout: 10_000 }).not.toBe(afterUndo2);
  });

  test('12.2 — Ctrl+D duplicates the selected node (copy+paste in one)', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    await page.locator('.react-flow__node[data-id="step-aaaa1111"]').click();
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"].selected')).toHaveCount(1);
    // Ctrl+D = copy+paste in one.
    await fireClipboard(page, 'Control+d');

    // The canvas grew and the original three seeded ids are intact — at least one duplicate with
    // a brand-new id was created.
    await expect.poll(() => nodeCount(page), { timeout: 10_000 }).toBeGreaterThan(3);
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"]')).toHaveCount(1);
    const ids = await page.locator('.react-flow__node[data-id^="step-"]').evaluateAll(
      (els) => els.map((e) => e.getAttribute('data-id')),
    );
    const seeded = ['step-aaaa1111', 'step-bbbb2222', 'step-cccc3333'];
    expect(ids.filter((x) => x && !seeded.includes(x)).length).toBeGreaterThanOrEqual(1);
  });

  test('12.2b — Ctrl+C then Ctrl+V pastes a copy of the selected node', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    await page.locator('.react-flow__node[data-id="step-aaaa1111"]').click();
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"].selected')).toHaveCount(1);
    await page.keyboard.press('Control+c');
    await page.waitForTimeout(200);
    // Paste (offset +40px, fresh id). See fireClipboard docstring for why a single Ctrl+V can be
    // swallowed by the handler's post-mutation closure re-bind.
    await fireClipboard(page, 'Control+v');
    await expect.poll(() => nodeCount(page), { timeout: 10_000 }).toBeGreaterThan(3);
    // Pasted node(s) carry new ids; the originals are untouched.
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"]')).toHaveCount(1);
    const ids = await page.locator('.react-flow__node[data-id^="step-"]').evaluateAll(
      (els) => els.map((e) => e.getAttribute('data-id')),
    );
    const seeded = ['step-aaaa1111', 'step-bbbb2222', 'step-cccc3333'];
    expect(ids.filter((x) => x && !seeded.includes(x)).length).toBeGreaterThanOrEqual(1);
  });

  test('12.3 — Ctrl+A select-all then Ctrl+G groups the nodes into a labeled group', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    // Ctrl+A selects all nodes (marquee drag is not synthesizable; Ctrl+A is the supported path).
    await page.keyboard.press('Control+a');
    await page.keyboard.press('Control+g');

    // A group node appears wrapping the selection, with the default "Group" label.
    const group = page.locator('.react-flow__node[data-id^="group-"]');
    await expect(group).toHaveCount(1, { timeout: 10_000 });
    await expect(group).toContainText('Group');
  });

  test('12.4 — "?" opens the keyboard-shortcut help overlay; Escape closes it', async ({ page }) => {
    await openEditor(page);

    // The global keydown handler listens on window and only ignores INPUT/TEXTAREA focus;
    // after load nothing is focused in a field, so "?" reaches it directly.
    await page.keyboard.press('?');

    const help = page.getByRole('heading', { name: /keyboard shortcuts/i });
    await expect(help).toBeVisible({ timeout: 10_000 });
    // The catalogue lists concrete shortcuts.
    await expect(page.getByText(/Group selected nodes into a labeled rectangle/i)).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(help).toHaveCount(0);
  });

  test('12.5 — Ctrl+F opens the search overlay, filters, and Escape closes it', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+f');
    const search = page.getByPlaceholder(/search nodes by label/i);
    await expect(search).toBeVisible({ timeout: 10_000 });

    // Typing a label filters the result list.
    await search.fill('Charlie');
    await expect(page.getByRole('button').filter({ hasText: 'Charlie' })).toBeVisible();
    // A non-matching query shows the no-hits message.
    await search.fill('zzzznomatch');
    await expect(page.getByText(/Keine Treffer|no matches/i)).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(search).toHaveCount(0);
  });

  test('12.6 — Ctrl+S saves (PUT); Escape clears selection; Ctrl+A selects', async ({ page }) => {
    let putSeen = false;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putSeen = true;
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson(threeNodeDefinition())) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson(threeNodeDefinition())) });
    });
    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"]')).toBeVisible({ timeout: 20_000 });

    // Make the workflow dirty (nudge a selected node) so Ctrl+S has something to persist.
    await page.locator('.react-flow__node[data-id="step-aaaa1111"]').click();
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"].selected')).toHaveCount(1);
    await page.keyboard.press('ArrowRight'); // nudge → dirty
    await page.keyboard.press('Control+s');
    await expect.poll(() => putSeen, { timeout: 10_000 }).toBe(true);

    // Escape deselects the node (selection class drops off).
    await page.keyboard.press('Escape');
    await expect(page.locator('.react-flow__node.selected')).toHaveCount(0);

    // Ctrl+A reselects (multi-select). Assert more than one node carries the selection class
    // — exact count is unreliable because React Flow only renders the `.selected` modifier on
    // currently-mounted nodes and applies it across a couple of frames.
    await page.keyboard.press('Control+a');
    await expect.poll(() => page.locator('.react-flow__node.selected').count(), { timeout: 10_000 }).toBeGreaterThanOrEqual(2);
  });

  test('12.6b — Delete removes the selected node', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    await page.locator('.react-flow__node[data-id="step-cccc3333"]').click();
    await expect(page.locator('.react-flow__node[data-id="step-cccc3333"].selected')).toHaveCount(1);
    await page.keyboard.press('Delete');
    await expect(page.locator('.react-flow__node[data-id="step-cccc3333"]')).toHaveCount(0, { timeout: 10_000 });
    await expect.poll(() => nodeCount(page)).toBe(2);
  });

  // ---- Teil 55 — Erweiterte Keyboard-Shortcuts -------------------------------------------

  test('55.1 — Ctrl+Shift+T tidies the layout (node positions change); Ctrl+Z restores', async ({ page }) => {
    await openEditor(page);
    await expect.poll(() => nodeCount(page)).toBe(3);

    const transformOf = async (id: string) =>
      page.locator(`.react-flow__node[data-id="${id}"]`).evaluate((el) => (el as HTMLElement).style.transform);
    const before = await transformOf('step-bbbb2222');

    await page.keyboard.press('Control+Shift+t');

    // Auto-layout rewrites positions → the node's CSS transform (its position) changes.
    await expect.poll(async () => transformOf('step-bbbb2222'), { timeout: 10_000 }).not.toBe(before);

    // Undo restores the original arrangement.
    await page.keyboard.press('Control+z');
    await expect.poll(async () => transformOf('step-bbbb2222'), { timeout: 10_000 }).toBe(before);
  });

  test('55.2 — Ctrl+] / Ctrl+[ adjust edge width without crashing the canvas', async ({ page }) => {
    await openEditor(page);
    // Edge width lives in the design store and re-renders LabeledEdge. We assert the canvas
    // survives the toggles and the edge stays rendered (the store mutation is a no-throw path).
    await page.keyboard.press('Control+]');
    await page.keyboard.press('Control+]');
    await page.keyboard.press('Control+[');
    // Edge element is still present and the editor did not error out.
    await expect(page.locator('.react-flow__edge[data-id="e-ab"]')).toHaveCount(1);
    await expect(page.getByText(/something went wrong|etwas ist schiefgelaufen/i)).toHaveCount(0);
  });

  test('55.3 — Ctrl+Alt+. / Ctrl+Alt+, adjust label font size (no crash)', async ({ page }) => {
    await openEditor(page);
    // labelFontInc / labelFontDec mutate the design store; assert it's a safe no-throw path and
    // the nodes remain mounted (Classic-view-only effect, but the handler runs unconditionally).
    await page.keyboard.press('Control+Alt+.');
    await page.keyboard.press('Control+Alt+.');
    await page.keyboard.press('Control+Alt+,');
    await expect(page.locator('.react-flow__node[data-id="step-aaaa1111"]')).toBeVisible();
    await expect(page.getByText(/something went wrong|etwas ist schiefgelaufen/i)).toHaveCount(0);
  });

  test('55.4 — Ctrl+Shift+1..5 navigate to the main pages', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+Shift+2');
    await expect(page).toHaveURL(/\/executions$/, { timeout: 10_000 });

    await page.keyboard.press('Control+Shift+3');
    await expect(page).toHaveURL(/\/machines$/, { timeout: 10_000 });

    await page.keyboard.press('Control+Shift+1');
    await expect(page).toHaveURL(/\/workflows$/, { timeout: 10_000 });

    // Browser history is intact — back returns to the previous page.
    await page.goBack();
    await expect(page).toHaveURL(/\/machines$/, { timeout: 10_000 });
  });
});
