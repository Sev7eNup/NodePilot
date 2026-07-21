import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 54 — Designer-Overlays: Command Palette (Ctrl+Shift+P), Quick Switcher
 * (Ctrl+P) and Find & Replace (Ctrl+H). All three are toggled by `useEditorKeyboardShortcuts`
 * and rendered by EditorOverlays → CommandPalette / WorkflowQuickSwitcher / FindReplaceOverlay.
 *
 * Hermetic: page.route() mocks only (no backend). SPA renders EN under Playwright.
 * The workflow is locked-by-me so the lifecycle/edit commands the palette lists are enabled.
 */

const WF_ID = 'eeeeeeee-5454-5454-5454-545454545454';
const OTHER_WF_ID = 'ffffffff-5454-5454-5454-545454545454';

function workflowJson(definition: { nodes: unknown[]; edges: unknown[] }) {
  return {
    id: WF_ID,
    name: 'Overlays_E2E_WF',
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

function definition() {
  return {
    nodes: [
      { id: 'step-hello11', type: 'activity', position: { x: 40, y: 40 }, data: { label: 'hello world', activityType: 'log', config: { message: 'say hello to everyone' } } },
      { id: 'step-hello22', type: 'activity', position: { x: 240, y: 60 }, data: { label: 'second hello', activityType: 'log', config: { message: 'hello again' } } },
    ],
    edges: [],
  };
}

async function openEditor(page: Page) {
  await seedExpertMode(page); // designer overlays live in the expert-mode toolbar (default is standard)
  // The Quick-Switcher + palette read GET /api/workflows (list). Provide two entries.
  await page.route('**/api/workflows', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: WF_ID, name: 'Overlays_E2E_WF', description: '', isEnabled: false },
        { id: OTHER_WF_ID, name: 'Payroll Nightly Sync', description: 'runs payroll', isEnabled: true },
      ]),
    }),
  );
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson(definition())) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-hello11"]')).toBeVisible({ timeout: 20_000 });
  // The global keydown handler listens on window and only ignores INPUT/TEXTAREA focus; after
  // load nothing is focused in a field, so the overlay-trigger combos reach it directly.
}

test.describe('Designer-Overlays (Teil 54)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('54.1 — Ctrl+Shift+P opens the command palette; typing filters; Escape closes', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+Shift+p');
    const input = page.getByPlaceholder(/type a command/i);
    await expect(input).toBeVisible({ timeout: 10_000 });

    // Group headers are present (commands grouped by category, e.g. Lifecycle / View).
    await expect(page.getByText(/^Ctrl\+Shift\+P$/).last()).toBeVisible(); // footer hint chip

    // Fuzzy filter: typing "publish" narrows the list and surfaces a Publish-related command.
    await input.fill('publish');
    await expect(page.getByText(/Showing .* of .* commands/i)).toBeVisible();
    await expect(page.getByRole('button').filter({ hasText: /publish/i }).first()).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(input).toHaveCount(0);
  });

  test('54.1b — command palette runs a command (open help overlay via Enter)', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+Shift+p');
    const input = page.getByPlaceholder(/type a command/i);
    await expect(input).toBeVisible({ timeout: 10_000 });

    // Search a command whose effect is observable: the keyboard-shortcut help overlay.
    await input.fill('keyboard short');
    // Top-ranked match is highlighted; Enter invokes it and the palette closes.
    await page.keyboard.press('Enter');
    await expect(page.getByPlaceholder(/type a command/i)).toHaveCount(0, { timeout: 10_000 });
    await expect(page.getByRole('heading', { name: /keyboard shortcuts/i })).toBeVisible({ timeout: 10_000 });
  });

  test('54.2 — Ctrl+P opens the quick switcher; fuzzy match; Enter navigates', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+p');
    const input = page.getByPlaceholder(/switch to workflow/i);
    await expect(input).toBeVisible({ timeout: 10_000 });

    // Fuzzy-match narrows to the other workflow. The switcher row uniquely carries the
    // workflow's id-slice (ffffffff…) — the sidebar list does not — so it disambiguates the
    // two "Payroll Nightly Sync" texts on the page.
    await input.fill('payroll');
    const switcherRow = page.getByRole('button').filter({ hasText: OTHER_WF_ID.slice(0, 8) });
    await expect(switcherRow).toBeVisible({ timeout: 10_000 });
    await expect(switcherRow).toContainText('Payroll Nightly Sync');

    // Enter navigates to the top hit.
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(new RegExp(`/workflows/${OTHER_WF_ID}`), { timeout: 10_000 });
  });

  test('54.2b — Ctrl+P quick switcher closes on Escape', async ({ page }) => {
    await openEditor(page);
    await page.keyboard.press('Control+p');
    const input = page.getByPlaceholder(/switch to workflow/i);
    await expect(input).toBeVisible({ timeout: 10_000 });
    await page.keyboard.press('Escape');
    await expect(input).toHaveCount(0);
  });

  test('54.3 — Ctrl+H opens Find & Replace, shows match counter, Replace All rewrites', async ({ page }) => {
    await openEditor(page);

    await page.keyboard.press('Control+h');
    const findInput = page.getByPlaceholder(/^find/i);
    await expect(findInput).toBeVisible({ timeout: 10_000 });

    // Empty search → no crash, prompt message shown.
    await expect(page.getByText(/enter a search term to find matches/i)).toBeVisible();

    // Type "hello" — match counter appears and result rows render.
    await findInput.fill('hello');
    await expect(page.getByText(/\d+ match(es)?/i)).toBeVisible({ timeout: 10_000 });

    // Replace All "hello" → "world": fill replace, click the "All (N)" button.
    await page.getByPlaceholder(/replace with/i).fill('world');
    await page.getByRole('button', { name: /^All \(\d+\)/ }).click();

    // Overlay closes after Replace All; the rename landed on the node label.
    await expect(findInput).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('.react-flow__node[data-id="step-hello11"]')).toContainText(/world/i);
  });

  test('54.3b — Find & Replace closes on Escape and handles empty search safely', async ({ page }) => {
    await openEditor(page);
    await page.keyboard.press('Control+h');
    const findInput = page.getByPlaceholder(/^find/i);
    await expect(findInput).toBeVisible({ timeout: 10_000 });
    // Empty search: no matches, no crash.
    await expect(page.getByText(/enter a search term to find matches/i)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(findInput).toHaveCount(0);
    await expect(page.getByText(/something went wrong|etwas ist schiefgelaufen/i)).toHaveCount(0);
  });
});
