import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 75 — Quick-Interaktionen im Designer (lines 3957-3996).
 *
 *   75.1 QuickEditPopup — double-click a node → inline popup with the activity's primary field
 *        (log → Message, restApi → URL, runScript → Monaco dialog, sql → Query). Enter/Save
 *        commits, Escape discards.
 *   75.2 QuickConnectPicker — drag from a node handle into empty canvas → picker. This relies on
 *        React-Flow d3-drag of a connection line, which Playwright cannot synthesize → skipped
 *        with reason. (The picker UI itself is the same ActivityPickerGrid covered by 75.3/68.)
 *   75.3 EdgeInserter — the inline "+" affordance on an edge opens the ActivityPickerGrid; a pick
 *        splits A→B into A→NEW→B. The "+" appears on edge-hover then is clicked — synthesizable.
 *   75.4 SubWorkflowPreviewModal — opened from the node-body "Preview" link on a startWorkflow
 *        node. That link only renders when the node has a non-empty summary line; startWorkflow
 *        has no summarizer (summarizeActivityConfig returns ''), so the body affordance never
 *        renders hermetically and the modal cannot be opened → skipped with reason. (The
 *        sub-workflow navigation affordance that IS reachable — the "Calls →" breadcrumb — is
 *        covered by breadcrumbs.spec.ts / Teil 72.)
 *
 * Hermetic: page.route mocks. Workflow locked-by-me (State B). SPA renders ENGLISH.
 */

const WF_ID = 'e7575757-7575-7575-7575-757575757575';

function definition() {
  return JSON.stringify({
    nodes: [
      // Top-left log node so the QuickEdit popup target is clear of the bottom-right MiniMap.
      { id: 'step-log', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'Logger', activityType: 'log', config: { message: 'hello' } } },
      { id: 'step-b', type: 'activity', position: { x: 380, y: 40 },
        data: { label: 'Consumer', activityType: 'delay', config: { seconds: 1 } } },
    ],
    edges: [
      { id: 'edge-ab', source: 'step-log', target: 'step-b', type: 'labeled',
        data: { label: '', condition: '', disabled: false } },
    ],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Quick', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
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
  await expect(page.locator('.react-flow__node[data-id="step-log"]')).toBeVisible({ timeout: 20_000 });
  await page.waitForTimeout(400); // let the post-load fitView (50ms setTimeout) settle so dblclick/hover land accurately
}

const saveButton = (page: Page) =>
  page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();

test.describe('Quick-Interaktionen im Designer (Teil 75)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('75.1 — double-click a log node opens QuickEditPopup; Save commits the Message', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });

    await page.locator('.react-flow__node[data-id="step-log"]').dblclick({ position: { x: 20, y: 12 } });

    // The QuickEditPopup is the floating z-[9999] popup. Scope to it so the right-panel
    // PropertiesPanel (also open because the dblclick selects the node) doesn't shadow it.
    const popup = page.locator('div.z-\\[9999\\]');
    await expect(popup).toBeVisible({ timeout: 5_000 });
    // Header shows the primary-field label for `log` = "Message".
    await expect(popup.getByText(/Message/).first()).toBeVisible();
    const input = popup.locator('.input-field');
    await expect(input).toHaveValue('hello');

    // Edit + Save commits the new message onto the node config.
    await input.fill('edited via quick-edit');
    await popup.getByRole('button', { name: /^Save/ }).click();

    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { id: string; data: { config?: { message?: string } } }[] };
    const log = def.nodes.find((n) => n.id === 'step-log')!;
    expect(log.data.config?.message).toBe('edited via quick-edit');
  });

  test('75.1b — QuickEditPopup Escape discards the edit', async ({ page }) => {
    await openEditor(page);

    await page.locator('.react-flow__node[data-id="step-log"]').dblclick({ position: { x: 20, y: 12 } });
    const popup = page.locator('div.z-\\[9999\\]');
    await expect(popup).toBeVisible({ timeout: 5_000 });
    await popup.locator('.input-field').fill('throwaway');
    await page.keyboard.press('Escape');

    // Popup closes; nothing persisted (we re-open and confirm the original value is intact).
    await expect(popup).toHaveCount(0);
    await page.locator('.react-flow__node[data-id="step-log"]').dblclick({ position: { x: 20, y: 12 } });
    const popup2 = page.locator('div.z-\\[9999\\]');
    await expect(popup2.locator('.input-field')).toHaveValue('hello');
  });

  test('75.3 — the edge "+" affordance opens the ActivityPicker; a pick splits the edge', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });

    // Hover the edge to reveal its inline "+" insert button, then click it.
    const interaction = page.locator(`.react-flow__edge[data-id="edge-ab"] .react-flow__edge-interaction`);
    const box = await interaction.boundingBox();
    if (!box) throw new Error('edge interaction path not found');
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    const plus = page.getByRole('button', { name: /Insert step on this edge/i });
    await expect(plus).toBeVisible({ timeout: 10_000 });
    await plus.click({ force: true });

    // The ActivityPickerGrid ("Add node") opens — the same picker the EdgeInserter wraps.
    await expect(page.getByText(/^Add node$|^Node hinzufügen$/).first()).toBeVisible({ timeout: 5_000 });

    // Pick "Delay / Wait" → the original A→B edge is replaced by A→NEW→B (3 nodes, 2 edges).
    await page.getByRole('button').filter({ hasText: /Delay/ }).first().click();

    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: unknown[]; edges: { source: string; target: string }[] };
    expect(def.nodes).toHaveLength(3);          // log + b + inserted
    expect(def.edges).toHaveLength(2);          // split into two hops
    // No direct log→b edge survives — it was split.
    expect(def.edges.some((e) => e.source === 'step-log' && e.target === 'step-b')).toBe(false);
  });

  test('75.2 — QuickConnectPicker via handle-drag (skipped: not synthesizable)', async () => {
    test.skip(true, 'QuickConnect requires a React-Flow d3-drag from a node handle into empty canvas; Playwright cannot synthesize that connection drag. The picker UI it shows is the same ActivityPickerGrid exercised in 75.3.');
  });

  test('75.4 — SubWorkflowPreviewModal via node Preview link (skipped: link not rendered hermetically)', async () => {
    test.skip(true, 'The SubWorkflowPreviewModal opens only from the startWorkflow node-body Preview link, which renders only when the node has a non-empty summary. startWorkflow has no summarizer (summarizeActivityConfig => ""), so the affordance never renders in the hermetic harness. The reachable sub-workflow navigation — the "Calls →" breadcrumb — is covered by Teil 72.');
  });
});
