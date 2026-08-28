import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 60 — BulkEditPanel & ActivityTypeFilter (lines 3650-3669).
 *
 * Hermetic: page.route() mocks only. The SPA renders EN under Playwright, so selectors are
 * role-based and match both languages.
 *
 * Editor State-B: the workflow is locked by the current user (checkedOutByUserId ===
 * MOCK_USER.id), so the canvas is editable, the right panel can show the BulkEditPanel, and
 * the header filter is live.
 *
 * A React-Flow marquee box-select drag cannot be synthesized with Playwright mouse events, so
 * multi-select uses Ctrl+A, which the editor binds to select-all. Nodes are clustered top-left
 * so the seeded graph sits inside the initial viewport.
 *
 *   60.1 — Ctrl+A selects at least two nodes and EditorRightPanel swaps to BulkEditPanel.
 *          Asserts the panel heading, the "N activities selected" count, and that the Apply
 *          machine bulk action becomes enabled once a machine is chosen.
 *   60.2 — The EditorHeader filter button opens ActivityTypeFilter; checking a type hides the
 *          matching nodes (React-Flow sets hidden:true and the node leaves the DOM), the
 *          toolbar badge shows the count, and "Clear all" / "Show all again" restores them.
 */

const WF_ID = '60606060-0000-0000-0000-000000000060';

const MACHINE = {
  id: 'm-bulk-1', name: 'WIN-BULK', hostname: 'win-bulk.local', winRmPort: 5985, useSsl: false,
  isReachable: true, activeRunCount: 0, usedByWorkflowCount: 0, recentStepCount: 0,
  recentFailedStepCount: 0, defaultCredentialId: null, tags: null, lastConnectivityCheck: null,
};

// Four nodes clustered top-left: two runScript (remote), two log. Distinct types so the
// ActivityTypeFilter lists both and filtering one is observable.
function definition() {
  return {
    nodes: [
      { id: 'rs-1', type: 'activity', position: { x: 20, y: 20 }, data: { label: 'Script One', activityType: 'runScript', config: { script: 'Get-Date' } } },
      { id: 'rs-2', type: 'activity', position: { x: 60, y: 90 }, data: { label: 'Script Two', activityType: 'runScript', config: { script: 'Get-Host' } } },
      { id: 'lg-1', type: 'activity', position: { x: 100, y: 160 }, data: { label: 'Log One', activityType: 'log', config: { message: 'a' } } },
      { id: 'lg-2', type: 'activity', position: { x: 140, y: 230 }, data: { label: 'Log Two', activityType: 'log', config: { message: 'b' } } },
    ],
    edges: [],
  };
}

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'BulkEdit_WF',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify(definition()),
    version: 1,
    activityCount: 4,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  });
}

async function openEditor(page: Page) {
  await seedExpertMode(page); // bulk edit and the type filter live in the expert-mode toolbar
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.route('**/api/machines', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([MACHINE]) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  // Wait for the canvas to seed all four nodes.
  await expect(page.locator('.react-flow__node')).toHaveCount(4, { timeout: 20_000 });
}

test.describe('BulkEditPanel & ActivityTypeFilter (Teil 60)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('60.1 — Ctrl+A multi-select opens the BulkEditPanel with the selection count + bulk actions', async ({ page }) => {
    await openEditor(page);

    // Focus the canvas via a node click, then select-all.
    await page.locator('.react-flow__node[data-id="rs-1"]').click({ position: { x: 12, y: 12 } });
    await expect(page.locator('.react-flow__node[data-id="rs-1"]')).toHaveClass(/selected/, { timeout: 5_000 });
    await page.keyboard.press('Control+a');

    // Two or more selected nodes open the BulkEditPanel with a "4 activities selected" count.
    await expect(page.getByRole('heading', { name: /bulk edit|mehrfach/i })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/4\s+activit(y|ies)\s+selected/i)).toBeVisible();

    // Bulk-action fields are present. The target-machine Apply stays disabled until a machine
    // is picked.
    const applyMachine = page.getByRole('button', { name: /apply machine|maschine anwenden/i });
    await expect(applyMachine).toBeDisabled();
    // The first combobox in the panel is the target-machine picker; selecting the mocked
    // machine enables its Apply button, so the bulk action can target the whole selection.
    await page.getByRole('combobox').first().selectOption(MACHINE.id);
    await expect(applyMachine).toBeEnabled({ timeout: 5_000 });

    // Timeout and state carry their own Apply buttons, so the panel covers more than machines.
    await expect(page.getByRole('button', { name: /apply timeout|timeout anwenden/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /apply state|status anwenden/i })).toBeVisible();
  });

  test('60.1b — bulk "Apply machine" patches all selected nodes and Save PUTs the new targetMachineId', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });
    await page.route('**/api/machines', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([MACHINE]) }),
    );

    await seedExpertMode(page); // bulk edit lives in the expert-mode toolbar
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node')).toHaveCount(4, { timeout: 20_000 });

    await page.locator('.react-flow__node[data-id="rs-1"]').click({ position: { x: 12, y: 12 } });
    await page.keyboard.press('Control+a');
    await expect(page.getByRole('heading', { name: /bulk edit|mehrfach/i })).toBeVisible({ timeout: 10_000 });

    // Pick the machine and apply it to all selected nodes.
    await page.getByRole('combobox').first().selectOption(MACHINE.id);
    await page.getByRole('button', { name: /apply machine|maschine anwenden/i }).click();

    // Persist and inspect the saved definition: every activity node now carries the machine id.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { data: { targetMachineId?: string } }[] };
    const withMachine = def.nodes.filter((n) => n.data.targetMachineId === MACHINE.id);
    // Bulk-apply hit all four activity nodes.
    expect(withMachine).toHaveLength(4);
  });

  test('60.2 — ActivityTypeFilter hides a type, badge shows the count, "Clear all" restores', async ({ page }) => {
    await openEditor(page);

    // Open the filter dropdown in the header view cluster. The button accessible name is its
    // title "Filter activity types" only while no badge is shown: once a type is hidden, the
    // badge digit becomes the button text and thus its accessible name. Anchor on the title
    // attribute so the locator stays stable for the whole test.
    const filterBtn = page.locator('button[title="Filter activity types"]');
    await expect(filterBtn).toBeVisible({ timeout: 10_000 });
    await filterBtn.click();

    // The dropdown lists the two types present, with per-type counts.
    await expect(page.getByText(/hide activity types/i)).toBeVisible({ timeout: 5_000 });
    const logRow = page.locator('label').filter({ hasText: 'log' });
    await expect(logRow).toBeVisible();

    // Hide the "log" type → its two nodes leave the DOM (React-Flow hidden:true), leaving 2 runScript.
    await logRow.getByRole('checkbox').check();
    await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 10_000 });

    // The toolbar filter badge surfaces the active-filter count (1 type hidden).
    await expect(filterBtn).toContainText('1');

    // "Clear all" / "Show all again" restores every node.
    await page.getByRole('button', { name: /clear all|show all again/i }).first().click();
    await expect(page.locator('.react-flow__node')).toHaveCount(4, { timeout: 10_000 });
  });
});
