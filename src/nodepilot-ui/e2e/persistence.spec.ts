import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * Covers section 8 of docs/testing/E2ETests.md: saving and persistence.
 *
 * Hermetic: page.route() mocks only (predicate catch-all from fixtures/mockApi.ts). The workflow
 * is mocked as locked by the current user (checkedOutByUserId === MOCK_USER.id, isEnabled:false),
 * so the editor opens editable and both Save and autosave are live. The SPA renders English.
 *
 * The save PUT body is the source of truth for the graph: the React Flow canvas is virtualized
 * (onlyRenderVisibleElements), so the assertions read the captured PUT, not the canvas DOM.
 */

const WF_ID = '8888bbbb-8888-8888-8888-888888888888';
const NODE_ID = 'step-persist';

function definition(label: string): string {
  return JSON.stringify({
    nodes: [{
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label, activityType: 'delay', config: { seconds: 5 } },
    }],
    edges: [],
  });
}

function workflowJson(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Persist',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition('Wait Step'),
    version: 1,
    ...overrides,
  });
}

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

/** Captures PUTs to the workflow; GET always returns the seeded fixture. */
function routeWorkflow(page: Page) {
  const state: { putCount: number; lastPut: { name?: string; definitionJson?: string } | null } = {
    putCount: 0, lastPut: null,
  };
  void page.route(`**/api/workflows/${WF_ID}`, (route) => {
    if (route.request().method() === 'PUT') {
      state.putCount += 1;
      state.lastPut = route.request().postDataJSON();
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
  });
  return state;
}

/** Opens the editor, waits for the seeded node and selects it so the PropertiesPanel opens. */
async function openAndSelect(page: Page) {
  await seedExpertMode(page);
  await page.goto(`/workflows/${WF_ID}`);
  await expect(node(page, NODE_ID)).toBeVisible({ timeout: 20_000 });
  await node(page, NODE_ID).click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(/delay/i).first()).toBeVisible({ timeout: 10_000 });
}

/** The Save button (icon-only, named by its title). The amber dot inside it marks unsaved edits. */
function saveButton(page: Page) {
  return page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();
}

/** Edits the delay seconds field, which makes the editor dirty and changes the definition. */
async function editSeconds(page: Page, value: string) {
  const secondsInput = page.locator('input[type="number"]').first();
  await expect(secondsInput).toBeVisible();
  await secondsInput.fill(value);
}

test.describe('Speichern & Persistierung (Teil 8)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  // ---------- 8.1a — manual save via the Save button ----------
  test('8.1a — Save button PUTs the edited definition and clears the dirty indicator', async ({ page }) => {
    const state = routeWorkflow(page);
    await openAndSelect(page);

    // A pristine load has no dirty dot; an edit makes one appear inside the Save button.
    const save = saveButton(page);
    const dirtyDot = save.locator('span.bg-warning');
    await expect(dirtyDot).toHaveCount(0);

    await editSeconds(page, '17');
    await expect(dirtyDot).toHaveCount(1, { timeout: 5_000 });

    await save.click();

    // PUT fires, carries the edited seconds, and the dirty dot clears (isDirty=false onSuccess).
    await expect.poll(() => {
      if (!state.lastPut?.definitionJson) return null;
      const def = JSON.parse(state.lastPut.definitionJson) as { nodes: { data: { config: { seconds: number } } }[] };
      return def.nodes[0].data.config.seconds;
    }, { timeout: 10_000 }).toBe(17);
    await expect(dirtyDot).toHaveCount(0, { timeout: 5_000 });
  });

  // ---------- 8.1b — Ctrl+S keyboard save ----------
  test('8.1b — Ctrl+S triggers a save PUT', async ({ page }) => {
    const state = routeWorkflow(page);
    await openAndSelect(page);

    await editSeconds(page, '23');
    // Move focus out of the number input so Ctrl+S isn't swallowed by the field, then save.
    await page.locator('.react-flow__pane').click({ position: { x: 5, y: 5 } });
    await page.keyboard.press('Control+s');

    await expect.poll(() => {
      if (!state.lastPut?.definitionJson) return null;
      const def = JSON.parse(state.lastPut.definitionJson) as { nodes: { data: { config: { seconds: number } } }[] };
      return def.nodes[0].data.config.seconds;
    }, { timeout: 10_000 }).toBe(23);
  });

  // ---------- 8.1c — autosave (timer-based, ~5 s after last change) ----------
  test('8.1c — autosave fires a PUT ~5 s after a dirty edit without any save click', async ({ page }) => {
    test.slow(); // timer-based: the editor saves only 5 s after the last graph mutation.
    const state = routeWorkflow(page);
    await openAndSelect(page);

    // Wait for the mount-time annotation effects (machine coloring, var flow, workflow-enabled
    // tagging) to settle: each re-derives `nodes`, and the autosave timer restarts on every
    // `nodes` change. Editing once the canvas is quiet gives the 5 s timer a clean run.
    await page.waitForTimeout(2500);

    await editSeconds(page, '31');
    // No Save click here: the editor autosaves on its own about 5 s after this single change.
    await expect.poll(() => state.putCount, { timeout: 25_000 }).toBeGreaterThanOrEqual(1);
    const def = JSON.parse(state.lastPut!.definitionJson!) as { nodes: { data: { config: { seconds: number } } }[] };
    expect(def.nodes[0].data.config.seconds).toBe(31);
    // Dirty indicator clears after the autosave round-trip succeeds.
    await expect(saveButton(page).locator('span.bg-warning')).toHaveCount(0, { timeout: 5_000 });
  });

  // ---------- 8.2 — versioning: history surfaced + save drives a new snapshot ----------
  test('8.2 — diff modal surfaces version history; a save sends the next-snapshot definition', async ({ page }) => {
    const state = routeWorkflow(page);

    // Version history endpoint: two prior versions plus the current one, which the UI filters out.
    const VERSIONS = [
      { version: 2, isCurrent: true, createdAt: '2026-06-02T10:00:00.000Z', createdBy: 'e2e-admin', changeNote: 'edit two' },
      { version: 1, isCurrent: false, createdAt: '2026-06-01T10:00:00.000Z', createdBy: 'e2e-admin', changeNote: 'initial' },
    ];
    await page.route(`**/api/workflows/${WF_ID}/versions`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(VERSIONS) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/versions/1`, (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ definition: { nodes: [], edges: [] } }),
      }),
    );

    await openAndSelect(page);

    // History is reachable through the editor's Tools menu: the Diff row opens the modal.
    await page.getByTestId('tools-menu-trigger').click();
    const diffBtn = page.getByRole('menuitem', { name: /diff against a previous version|diff gegen vorherige version/i });
    await expect(diffBtn).toBeVisible({ timeout: 10_000 });
    await diffBtn.click();
    await expect(page.getByRole('heading', { name: /workflow diff/i })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('Version 1', { exact: true })).toBeVisible();
    // Current version (2) is excluded from the restore-able timeline.
    await expect(page.getByText('Version 2', { exact: true })).toHaveCount(0);

    // Close the modal, edit and save. The backend snapshots this PUT as the next version, so
    // the assertion checks that the mutation carries the changed definition.
    await page.getByRole('button', { name: /^close$/i }).click();
    await expect(page.getByRole('heading', { name: /workflow diff/i })).toHaveCount(0);

    await editSeconds(page, '99');
    await saveButton(page).click();

    await expect.poll(() => {
      if (!state.lastPut?.definitionJson) return null;
      const def = JSON.parse(state.lastPut.definitionJson) as { nodes: { data: { config: { seconds: number } } }[] };
      return def.nodes[0].data.config.seconds;
    }, { timeout: 10_000 }).toBe(99);
  });
});
