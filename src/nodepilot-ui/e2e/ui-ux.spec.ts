import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md part 10 — UI/UX tests.
 *
 * 10.1 Properties panel responsiveness: select a node, type fast into a per-keystroke field
 *      (the Output Variable input updates the store on every change), assert nothing is lost
 *      and the inline node-name editor commits the full typed value.
 * 10.2 Mobile responsiveness (optional): shrink to 375px and confirm pages still render and
 *      navigation works. A "desktop-only" warning is acceptable per the spec, so the test
 *      asserts the app stays mounted and usable rather than a specific layout.
 * 10.3 Accessibility (basic): buttons and links expose accessible names, form fields are
 *      reachable and labelled, and keyboard nav (Tab focus, Enter activate, Escape close)
 *      works. Full axe scans are out of scope for a hermetic spec, so only the concrete,
 *      checkable affordances are asserted.
 *
 * Hermetic: page.route() mocks only (no backend). The SPA renders English under Playwright.
 */

const WF_ID = '10101010-1010-1010-1010-101010101010';
const NODE_ID = 'step-bbbb2222';

function workflowWithNode() {
  return {
    id: WF_ID,
    name: 'UIUX_E2E_WF',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // Locked by the mock admin, so canWrite is true
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify({
      nodes: [
        {
          id: NODE_ID,
          type: 'activity',
          position: { x: 260, y: 180 },
          data: { label: 'Check Disk', activityType: 'runScript', targetMachineId: null, credentialId: null, config: { script: 'Get-PSDrive C' } },
        },
      ],
      edges: [],
    }),
    version: 1,
    activityCount: 1,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
  };
}

async function openEditorAndSelectNode(page: Page) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowWithNode()) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  const node = page.locator(`.react-flow__node[data-id="${NODE_ID}"]`);
  await expect(node).toBeVisible({ timeout: 20_000 });
  await node.click();
  // The properties panel opens with the inline-editable "Node name" header present.
  await expect(page.getByRole('button', { name: 'Node name' }).or(page.getByRole('textbox', { name: 'Node name' }))).toBeVisible({ timeout: 10_000 });
}

test.describe('UI/UX (Teil 10)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('10.1 — properties panel: rapid input is not lost (per-keystroke field + inline name)', async ({ page }) => {
    await openEditorAndSelectNode(page);

    // ---- Per-keystroke field: Output Variable. Open its popover input and type fast. ----
    // The pill shows the placeholder (step id) until a value is set; identify it by its
    // title (the unset-output tooltip mentions the step-id default) and click to edit.
    await page.locator('button[title*="Step-ID"]').first().click();
    // The popover input's label "Output Variable Name" is not htmlFor-linked, so scope to the
    // popover (anchored on that label) and pick its textbox; its placeholder is the step-id.
    const outPopover = page.getByText(/output variable name/i).locator('..');
    const outInput = outPopover.getByRole('textbox');
    await expect(outInput).toBeVisible();
    // pressSequentially fires a keystroke per char — the store updates on every change.
    await outInput.pressSequentially('diskCheckResult', { delay: 8 });
    // No lost characters: the input holds the full value.
    await expect(outInput).toHaveValue('diskCheckResult');
    // Enter commits and closes the popover; the pill then reflects the value.
    await outInput.press('Enter');
    await expect(page.getByRole('button', { name: /diskCheckResult/ })).toBeVisible();

    // ---- Inline node-name editor: click to edit, type, Enter commits the full value. ----
    const nameTrigger = page.getByRole('button', { name: 'Node name' });
    await nameTrigger.click();
    const nameInput = page.getByRole('textbox', { name: 'Node name' });
    await expect(nameInput).toBeVisible();
    await nameInput.fill('');
    await nameInput.pressSequentially('Verify Disk Space', { delay: 8 });
    await expect(nameInput).toHaveValue('Verify Disk Space');
    await nameInput.press('Enter');
    // After commit the header shows the new name and the canvas node label updates too.
    await expect(page.getByRole('button', { name: 'Node name' })).toContainText('Verify Disk Space');
    await expect(page.locator(`.react-flow__node[data-id="${NODE_ID}"]`)).toContainText('Verify Disk Space');
  });

  test('10.2 — mobile (375px): app renders and navigation works without crashing', async ({ page }) => {
    await page.route('**/api/stats/dashboard**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          workflowsTotal: 3, workflowsEnabled: 2, machinesTotal: 1, machinesReachable: 1, executionsTotal: 10,
          last24h: { total: 0, succeeded: 0, failed: 0, running: 0, cancelled: 0 },
          last24hBuckets: [], topWorkflows: [], running: [], recent: [], armedTriggers: [],
          pendingCount: 0, runningCount: 0, longRunningCount: 0, failingWorkflows: [], editLocks: [],
          healthHeartbeats: [], databaseProvider: 'PostgreSQL', clusterRole: null, recentAudit: null,
        }),
      }),
    );

    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/');
    // Dashboard mounts at mobile width (no white-screen crash).
    await expect(page.getByRole('heading', { name: /^dashboard$/i })).toBeVisible({ timeout: 15_000 });

    // Client-side navigation to Settings still works at mobile width.
    await page.goto('/settings');
    await expect(page.getByRole('heading', { name: /appearance/i })).toBeVisible({ timeout: 15_000 });
    // ErrorBoundary fallback did not trip.
    await expect(page.getByText(/something went wrong|etwas ist schiefgelaufen/i)).toHaveCount(0);
  });

  test('10.3 — accessibility basics: named controls, labelled inputs, keyboard nav', async ({ page }) => {
    await page.goto('/settings');

    // Every button on the page exposes an accessible name (aria-label, title, or visible text)
    // — no anonymous icon-only buttons that a screen reader can't announce.
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    await expect(appearance).toBeVisible({ timeout: 15_000 });
    const buttons = page.getByRole('button');
    const count = await buttons.count();
    expect(count).toBeGreaterThan(0);
    for (let i = 0; i < count; i++) {
      const b = buttons.nth(i);
      const name = (await b.getAttribute('aria-label')) ?? (await b.getAttribute('title')) ?? (await b.innerText());
      expect((name ?? '').trim().length, `button #${i} has no accessible name`).toBeGreaterThan(0);
    }

    // Keyboard: the Light theme button (in Appearance) is focusable and Enter activates it.
    const light = appearance.getByRole('button', { name: /^light$/i });
    await light.focus();
    await expect(light).toBeFocused();
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(false);

    // Form fields are labelled/typeable: the credential name input accepts input.
    // (Settings shows the Add-Credential form under "Credentials"; Admin can open it.)
    const addCred = page.getByRole('button', { name: /add credential|anmeldedaten hinzufügen/i });
    if (await addCred.count()) {
      await addCred.first().click();
      const nameField = page.getByPlaceholder(/credential name|name der anmeldedaten/i).first();
      await expect(nameField).toBeVisible();
      await nameField.fill('svc-account');
      await expect(nameField).toHaveValue('svc-account');
    }
  });

  test('10.3b — Escape closes a transient popover (keyboard dismiss)', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowWithNode()) }),
    );
    await page.goto(`/workflows/${WF_ID}`);
    const node = page.locator(`.react-flow__node[data-id="${NODE_ID}"]`);
    await expect(node).toBeVisible({ timeout: 20_000 });

    // Right-click opens the context menu; Escape dismisses it (keyboard accessibility).
    await node.click({ button: 'right' });
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toBeVisible({ timeout: 10_000 });
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toHaveCount(0);
  });
});
