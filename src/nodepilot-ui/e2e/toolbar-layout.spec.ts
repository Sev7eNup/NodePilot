import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 79 — Toolbar-Layout-Umschalter (kompakt ⇄ klassisch).
 *
 * A persisted `toolbarLayout` flag (designStore) switches the editor header between the compact
 * grouped toolbar (default) and the classic inline-button row. The `toggle-toolbar-layout` button
 * lives in BOTH layouts so it's always reachable. Classic un-folds the View/Overlays/Tools
 * popovers into inline buttons and wraps whole clusters on narrow viewports (no horizontal
 * overflow). SPA renders ENGLISH under Playwright; hermetic page.route mocks.
 */

const WF_ID = 'e7171717-7171-7171-7171-717171717171';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'A', activityType: 'runScript', config: { script: 'x' } } },
      { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
        data: { label: 'B', activityType: 'log', config: { message: 'hi' } } },
    ],
    edges: [{ id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled', data: { label: '', condition: '', disabled: false } }],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Layout', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
  });
}

async function openEditor(page: Page) {
  await seedExpertMode(page);
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('Toolbar-Layout-Umschalter (Teil 79)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('79.1 — toggle switches compact ⇄ classic and un-folds the popovers inline', async ({ page }) => {
    await openEditor(page);

    // Compact by default: the "Display" canvas-settings popover + Overlays popover exist.
    await expect(page.getByTestId('canvas-settings-trigger')).toBeVisible();
    await expect(page.getByTestId('view-overlays-trigger')).toBeVisible();

    await page.getByTestId('toggle-toolbar-layout').click();

    // Classic: overlays are inline buttons now (not behind the Eye popover), the compact
    // popovers are gone, and the layout toggle is still present to switch back.
    await expect(page.getByTestId('toggle-dataflow-overlay')).toBeVisible();
    await expect(page.getByTestId('canvas-settings-trigger')).toHaveCount(0);
    await expect(page.getByTestId('view-overlays-trigger')).toHaveCount(0);
    await expect(page.getByTestId('toggle-toolbar-layout')).toBeVisible();

    // Toggle back.
    await page.getByTestId('toggle-toolbar-layout').click();
    await expect(page.getByTestId('canvas-settings-trigger')).toBeVisible();
  });

  test('79.2 — a persisted classic profile drives the classic layout and survives reload', async ({ page }) => {
    // A profile the app previously wrote (designStore persist) carries toolbarLayout:'classic'.
    // On load the editor must honour it — and keep honouring it across a reload. (The toggle's
    // write path + the missing-key default are covered by the designStore unit tests.)
    await page.addInitScript(() => {
      localStorage.setItem('nodepilot-design', JSON.stringify({
        state: { designerMode: 'expert', toolbarLayout: 'classic' }, version: 1,
      }));
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('toggle-dataflow-overlay')).toBeVisible();
    await expect(page.getByTestId('canvas-settings-trigger')).toHaveCount(0);

    await page.reload();
    await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('toggle-dataflow-overlay')).toBeVisible();
    await expect(page.getByTestId('canvas-settings-trigger')).toHaveCount(0);
  });

  test('79.3 — a persisted v1 profile without toolbarLayout hydrates to compact', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('nodepilot-design', JSON.stringify({
        state: { designerMode: 'expert', nodeStyle: 'classic' },
        version: 1,
      }));
    });
    await openEditor(page);
    // Missing key → compact default → the grouped popovers are present.
    await expect(page.getByTestId('canvas-settings-trigger')).toBeVisible();
    await expect(page.getByTestId('toggle-dataflow-overlay')).toHaveCount(0);
  });

  for (const width of [1024, 1440]) {
    test(`79.4 — classic toolbar wraps without horizontal overflow @ ${width}px, toggle stays reachable`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await openEditor(page);
      await page.getByTestId('toggle-toolbar-layout').click();
      await expect(page.getByTestId('toggle-dataflow-overlay')).toBeVisible();

      // No horizontal overflow — clusters wrap onto extra lines instead of clipping.
      const overflow = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow).toBeLessThanOrEqual(1);

      // The layout toggle stays within the viewport and clickable (switches back).
      const toggle = page.getByTestId('toggle-toolbar-layout');
      const box = await toggle.boundingBox();
      expect(box).not.toBeNull();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(width);
      await toggle.click();
      await expect(page.getByTestId('canvas-settings-trigger')).toBeVisible();
    });
  }
});
