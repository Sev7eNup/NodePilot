import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 69 — Editor-Toolbar View-Toggles (lines 3841-3865).
 *
 * The editor toolbar carries the canvas-display settings (CanvasSettingsPanel, opened from the
 * "Display" popover) plus the inspect-overlay toggles (OverlaysMenu in EditorHeader.tsx). Each
 * control drives a designStore flag. Since the redesign the display controls are labeled rows —
 * a switch (role="switch"/aria-checked), a segmented control (role="radio") or a stepper — so we
 * assert on ARIA state / testids rather than on `title` strings. SPA renders ENGLISH under
 * Playwright.
 *
 * The canvas-display controls live inside the "Display" (canvas-settings) dialog; the inspection
 * overlays live inside their own "Overlays" popover. Open the relevant menu before asserting.
 *
 * Hermetic: page.route mocks. Workflow locked-by-me so the full toolbar (incl. canWrite-gated
 * Layout cluster) renders, though the view toggles themselves are visible for any role.
 */

const WF_ID = 'e6969696-6969-6969-6969-696969696969';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'A', activityType: 'runScript', config: { script: 'x' } } },
      { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
        data: { label: 'B', activityType: 'log', config: { message: 'hi' } } },
    ],
    edges: [
      { id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled',
        data: { label: '', condition: '', disabled: false } },
    ],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Toggles', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
  });
}

async function openEditor(page: Page) {
  await seedExpertMode(page); // view-toggles live in the expert-mode toolbar (default is standard)
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

// The canvas-display controls (edge animation, node-style, ports, node-scale, …) now live
// inside the "Display" settings dialog. Open it before asserting on any of them.
async function openCanvasSettings(page: Page) {
  await page.getByTestId('canvas-settings-trigger').click();
  await expect(page.getByRole('dialog')).toBeVisible();
}

test.describe('Editor-Toolbar View-Toggles (Teil 69)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('69.1 — edge-animation switch flips its aria-checked', async ({ page }) => {
    await openEditor(page);
    await openCanvasSettings(page);

    const animate = page.getByTestId('canvas-setting-edge-animation');
    await expect(animate).toBeVisible();
    const before = await animate.getAttribute('aria-checked');
    await animate.click();
    await expect(animate).not.toHaveAttribute('aria-checked', before!);
    // Click again restores the original state — the switch is reversible.
    await animate.click();
    await expect(animate).toHaveAttribute('aria-checked', before!);
  });

  test('69.2 — node-style segmented control switches classic/card', async ({ page }) => {
    await openEditor(page);
    await openCanvasSettings(page);

    const group = page.getByTestId('canvas-setting-node-style');
    await expect(group).toBeVisible();
    const classic = group.getByRole('radio', { name: 'Classic' });
    const card = group.getByRole('radio', { name: 'Card' });

    await card.click();
    await expect(card).toHaveAttribute('aria-checked', 'true');
    await expect(classic).toHaveAttribute('aria-checked', 'false');

    await classic.click();
    await expect(classic).toHaveAttribute('aria-checked', 'true');
  });

  test('69.3 — flexible-ports switch flips its aria-checked', async ({ page }) => {
    await openEditor(page);
    await openCanvasSettings(page);

    const flex = page.getByTestId('canvas-setting-flexible-ports');
    await expect(flex).toBeVisible();
    await expect(flex).toHaveAttribute('aria-checked', 'false'); // default off
    await flex.click();
    await expect(flex).toHaveAttribute('aria-checked', 'true');
  });

  test('69.4 — data-flow overlay toggle flips its active class', async ({ page }) => {
    await openEditor(page);

    // The data-flow toggle is a switch row inside the "Overlays" popover.
    await page.getByTestId('view-overlays-trigger').click();
    const df = page.getByTestId('toggle-dataflow-overlay');
    await expect(df).toBeVisible();
    await expect(df).toHaveAttribute('aria-checked', 'false');
    await df.click();
    await expect(df).toHaveAttribute('aria-checked', 'true');   // switch row gains the active state
    await df.click();
    await expect(df).toHaveAttribute('aria-checked', 'false');
  });

  test('69.5 — node-scale stepper changes the scale label', async ({ page }) => {
    await openEditor(page);
    await openCanvasSettings(page);

    // The classic-only node-scale stepper shows XS/S/M/L/… between − (Smaller) and + (Larger).
    const stepper = page.getByTestId('canvas-setting-node-scale');
    // designStore is persisted; if a prior session left Card view, flip back so the stepper shows.
    if (!(await stepper.isVisible())) {
      await page.getByTestId('canvas-setting-node-style').getByRole('radio', { name: 'Classic' }).click();
    }
    await expect(stepper).toBeVisible();

    const label = stepper.getByRole('status');
    const before = await label.innerText();
    await stepper.getByLabel('Larger nodes').click();
    await expect(label).not.toHaveText(before, { timeout: 5_000 });
  });

  test('69.6 — compact display panel stays inside the minimum desktop viewport without scrolling', async ({ page }) => {
    // Below 1024px the product intentionally replaces the editor with MobileWorkflowView.
    // Exercise the narrowest real viewport that can render this desktop-only expert menu.
    await page.setViewportSize({ width: 1024, height: 900 });
    await openEditor(page);
    await openCanvasSettings(page);

    const dialog = page.getByTestId('canvas-settings-dialog');
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(7);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(1017);

    const scrollState = await page.getByTestId('canvas-settings-scroll').evaluate((element) => ({
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
    }));
    expect(scrollState.scrollHeight).toBeLessThanOrEqual(scrollState.clientHeight + 1);
  });
});
