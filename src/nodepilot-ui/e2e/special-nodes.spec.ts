import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 13 — Spezielle Node-Types (Sticky Note + Group).
 *
 * Hermetic: page.route() mocks only (no backend). SPA renders EN under Playwright.
 * The workflow is locked-by-me so `canWrite` is true and double-click-to-edit + grouping work.
 *
 * Sticky-note and group nodes are pre-seeded via `definitionJson` (type 'stickyNote' / 'group')
 * — they render as `.react-flow__node` and editing/collapse is driven through their DOM, which
 * is fully synthesizable. (Drag-add from the palette uses HTML5 drag-and-drop, which Playwright
 * cannot synthesize against the canvas; the group-create-via-Ctrl+G path is exercised here too.)
 */

const WF_ID = 'dddddddd-1313-1313-1313-131313131313';

function workflowJson(definition: { nodes: unknown[]; edges: unknown[] }) {
  return {
    id: WF_ID,
    name: 'SpecialNodes_E2E_WF',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked-by-me → canWrite
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify(definition),
    version: 1,
    activityCount: definition.nodes.length,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
  };
}

async function openEditor(page: Page, definition: { nodes: unknown[]; edges: unknown[] }) {
  await seedExpertMode(page);
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson(definition)) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node').first()).toBeVisible({ timeout: 20_000 });
  await page.waitForTimeout(400); // let the post-load fitView (50ms setTimeout) settle before dblclick
}

test.describe('Special Node-Types (Teil 13)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('13.1 — sticky note renders distinctly and is double-click editable', async ({ page }) => {
    const NOTE_ID = 'note-12345678';
    await openEditor(page, {
      nodes: [
        { id: NOTE_ID, type: 'stickyNote', position: { x: 60, y: 60 }, style: { width: 220, height: 120 }, data: { label: 'Note', activityType: 'note', text: '', disabled: true } },
      ],
      edges: [],
    });

    const note = page.locator(`.react-flow__node[data-id="${NOTE_ID}"]`);
    await expect(note).toBeVisible({ timeout: 20_000 });
    // Distinct from an activity node: shows the placeholder until edited.
    await expect(note).toContainText('Double-click to edit');

    // Double-click opens the inline textarea (editable, plain text).
    await note.dblclick();
    const textarea = note.locator('textarea');
    await expect(textarea).toBeVisible({ timeout: 10_000 });
    await textarea.fill('Diese Region macht X');
    // Ctrl+Enter commits the value back into the node.
    await textarea.press('Control+Enter');
    await expect(note).toContainText('Diese Region macht X');
    // Textarea closed after commit.
    await expect(note.locator('textarea')).toHaveCount(0);
  });

  test('13.1b — sticky note has no connection handles (documentation-only)', async ({ page }) => {
    const NOTE_ID = 'note-87654321';
    await openEditor(page, {
      nodes: [
        { id: NOTE_ID, type: 'stickyNote', position: { x: 60, y: 60 }, style: { width: 220, height: 120 }, data: { label: 'Note', activityType: 'note', text: 'just a comment', disabled: true } },
      ],
      edges: [],
    });

    const note = page.locator(`.react-flow__node[data-id="${NOTE_ID}"]`);
    await expect(note).toBeVisible({ timeout: 20_000 });
    // No source/target handles → it can never be an edge endpoint.
    await expect(note.locator('.react-flow__handle')).toHaveCount(0);
  });

  test('13.2 — group node wraps children, label is editable, and collapse/expand toggles', async ({ page }) => {
    const GROUP_ID = 'group-abcdabcd';
    await openEditor(page, {
      nodes: [
        { id: GROUP_ID, type: 'group', position: { x: 40, y: 40 }, style: { width: 360, height: 220 }, data: { label: 'Group', color: 'blue', collapsed: false, disabled: true, activityType: 'group' } },
        { id: 'step-child111', type: 'activity', parentId: GROUP_ID, position: { x: 30, y: 60 }, data: { label: 'Inner Step', activityType: 'log', config: { message: 'x' } } },
      ],
      edges: [],
    });

    const group = page.locator(`.react-flow__node[data-id="${GROUP_ID}"]`);
    await expect(group).toBeVisible({ timeout: 20_000 });
    await expect(group).toContainText('Group');
    // The child node mounts inside the group.
    await expect(page.locator('.react-flow__node[data-id="step-child111"]')).toBeVisible();

    // Label is editable: double-click the label → input → rename.
    await group.getByTitle(/double-click to rename/i).dblclick();
    const labelInput = group.locator('input');
    await expect(labelInput).toBeVisible({ timeout: 10_000 });
    await labelInput.fill('Data Processing');
    await labelInput.press('Enter');
    await expect(group).toContainText('Data Processing');

    // Collapse via the header button (title "Collapse group"), then expand again.
    await group.getByTitle(/collapse group/i).click();
    await expect(group.getByTitle(/expand group/i)).toBeVisible({ timeout: 10_000 });
    await group.getByTitle(/expand group/i).click();
    await expect(group.getByTitle(/collapse group/i)).toBeVisible({ timeout: 10_000 });
  });

  test('13.2b — Ctrl+G groups selected activity nodes into a group node', async ({ page }) => {
    await openEditor(page, {
      nodes: [
        { id: 'step-g1', type: 'activity', position: { x: 40, y: 40 }, data: { label: 'One', activityType: 'log', config: {} } },
        { id: 'step-g2', type: 'activity', position: { x: 220, y: 60 }, data: { label: 'Two', activityType: 'log', config: {} } },
        { id: 'step-g3', type: 'activity', position: { x: 120, y: 200 }, data: { label: 'Three', activityType: 'log', config: {} } },
      ],
      edges: [],
    });
    await expect(page.locator('.react-flow__node[data-id="step-g1"]')).toBeVisible({ timeout: 20_000 });

    // Select all (marquee is not synthesizable) then Ctrl+G.
    await page.keyboard.press('Control+a');
    await page.keyboard.press('Control+g');

    const group = page.locator('.react-flow__node[data-id^="group-"]');
    await expect(group).toHaveCount(1, { timeout: 10_000 });
    await expect(group).toContainText('Group');
    // The three originals are still on the canvas (now parented to the group).
    await expect(page.locator('.react-flow__node[data-id^="step-g"]')).toHaveCount(3);
  });

  test('13.2c — deleting a group keeps its child nodes (wrapper-only delete)', async ({ page }) => {
    const GROUP_ID = 'group-deadbeef';
    await openEditor(page, {
      nodes: [
        { id: GROUP_ID, type: 'group', position: { x: 40, y: 40 }, style: { width: 360, height: 220 }, data: { label: 'Group', color: 'blue', collapsed: false, disabled: true, activityType: 'group' } },
        { id: 'step-keep111', type: 'activity', parentId: GROUP_ID, position: { x: 30, y: 60 }, data: { label: 'Keep Me', activityType: 'log', config: {} } },
      ],
      edges: [],
    });
    const group = page.locator(`.react-flow__node[data-id="${GROUP_ID}"]`);
    await expect(group).toBeVisible({ timeout: 20_000 });

    // Select the group frame (click its dashed border area, away from the header buttons) and Delete.
    await group.click({ position: { x: 180, y: 180 } });
    await page.keyboard.press('Delete');

    // onBeforeDelete unparents children: the group is gone but the child survives.
    await expect(group).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('.react-flow__node[data-id="step-keep111"]')).toHaveCount(1);
  });
});
