import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Part 73 — Manually Overriding an Edge Label (lines 3928-3945).
 *
 * Selecting an edge opens the EdgePropertiesPanel ("Connection"). Its Label field
 * (placeholder "e.g. On Success, If True...") writes `data.label` on the edge. A custom label
 * that differs from the condition-derived auto-label surfaces an amber "Custom label overrides
 * the condition" hint with an "Auto:" preview + a "Use auto" button that clears it back to the
 * automatic label.
 *
 * Edge creation by handle-drag is React-Flow d3-drag (not synthesizable), so the edge is
 * PRE-SEEDED. We exercise: set custom label → override hint appears + PUT carries data.label;
 * "Use auto" → label cleared.
 *
 * Hermetic: page.route mocks. Workflow locked-by-me (State B). SPA renders ENGLISH.
 */

const WF_ID = 'e7373737-7373-7373-7373-737373737373';
const NODE_A = 'step-src73';
const NODE_B = 'step-dst73';
const EDGE_ID = 'edge-73';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: NODE_A, type: 'activity', position: { x: 60, y: 60 },
        data: { label: 'Producer', activityType: 'runScript', outputVariable: 'p', config: { script: 'x' } } },
      { id: NODE_B, type: 'activity', position: { x: 320, y: 60 },
        data: { label: 'Consumer', activityType: 'delay', config: { seconds: 1 } } },
    ],
    edges: [
      // Condition .success → auto-label would be "On Success". Seed label empty so the field
      // starts on the auto value; the test then types a custom label.
      { id: EDGE_ID, source: NODE_A, target: NODE_B, type: 'labeled',
        data: { label: '', condition: `${NODE_A}.success`, disabled: false } },
    ],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-EdgeLabel', description: '', isEnabled: false,
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
  await expect(page.locator('.react-flow__node').first()).toBeVisible({ timeout: 20_000 });
  await expect(page.locator('.react-flow__edge')).toHaveCount(1);
}

/** Click the seeded edge midpoint → opens the Connection panel. */
async function selectEdge(page: Page) {
  const heading = page.getByRole('heading', { name: /^connection$|^verbindung$/i });
  const interaction = page.locator(`.react-flow__edge[data-id="${EDGE_ID}"] .react-flow__edge-interaction`);
  await interaction.waitFor({ state: 'attached', timeout: 10_000 }); // SVG path: attached, not "visible"
  await page.waitForTimeout(500); // let the load-time fitView animation settle before measuring
  // The fitView animation can shift the edge between measuring its box and the click landing, so a
  // single centre click occasionally misses. Re-measure + click until the Connection panel opens.
  for (let i = 0; i < 8; i++) {
    const box = await interaction.boundingBox();
    if (box) {
      await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
      if (await heading.isVisible().catch(() => false)) return;
    }
    await page.waitForTimeout(250);
  }
  await expect(heading).toBeVisible({ timeout: 5_000 });
}

const saveButton = (page: Page) =>
  page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();

test.describe('Edge-Label Manuell Überschreiben (Teil 73)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('73.1 — typing a custom label persists data.label and shows the override hint', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });
    await selectEdge(page);

    const labelInput = page.getByPlaceholder(/On Success, If True/i);
    await expect(labelInput).toBeVisible();

    // Type a custom label that differs from the auto value ("On Success").
    await labelInput.fill('Deploy went green');
    await expect(labelInput).toHaveValue('Deploy went green');

    // The amber override hint appears, showing the auto label that would otherwise apply.
    await expect(page.getByText(/custom label overrides the condition/i)).toBeVisible();
    await expect(page.getByText(/Auto:/).first()).toBeVisible();
    await expect(page.getByText('On Success').first()).toBeVisible();

    // Save → the custom label round-trips on the edge.
    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { edges: { data?: { label?: string } }[] };
    expect(def.edges[0].data?.label).toBe('Deploy went green');
  });

  test('73.2 — "Use auto" clears the custom label back to the condition-derived auto', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await openEditor(page, (b) => { putBody = b; });
    await selectEdge(page);

    const labelInput = page.getByPlaceholder(/On Success, If True/i);
    await labelInput.fill('Custom thing');
    await expect(page.getByText(/custom label overrides the condition/i)).toBeVisible();

    // "Use auto" clears the label so the edge follows its condition automatically.
    await page.getByRole('button', { name: /use auto/i }).click();
    await expect(labelInput).toHaveValue('');
    await expect(page.getByText(/custom label overrides the condition/i)).toHaveCount(0);

    // Save → the persisted label is empty/undefined (auto applies).
    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { edges: { data?: { label?: string } }[] };
    expect(def.edges[0].data?.label ?? '').toBe('');
  });
});
