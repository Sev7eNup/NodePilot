import type { Page } from '@playwright/test';

/**
 * Re-fit the graph after the right inspector opened.
 *
 * React Flow keeps its viewport transform when the pane shrinks, so nodes that `fitView` centred
 * on the full-width canvas can end up beside the remaining pane. `onlyRenderVisibleElements` then
 * unmounts them and any locator for such a node goes stale. Fitting the view pulls the whole graph
 * back into the pane that is left.
 *
 * `focus` says who holds the keyboard: with `'canvas'` the `Home` shortcut fits the view and
 * leaves focus where the following key presses need it. With `'panel'` — a chat or inspector has
 * the caret — the shortcut would move that caret instead, so the React Flow control is clicked.
 */
export async function refitCanvas(page: Page, focus: 'canvas' | 'panel' = 'canvas') {
  if (focus === 'panel') await page.locator('.react-flow__controls-fitview').click();
  else await page.keyboard.press('Home');
  // Fit view is animated; measuring a node before it settles yields stale coordinates.
  await page.waitForTimeout(500);
}

/**
 * Zoom the canvas in through the React Flow controls, for tests that need nodes big enough to
 * aim at a single port. Fitting a graph into the pane left over beside the inspector can shrink
 * a node to a couple of dozen pixels, where the ports are no longer separable by a click.
 */
export async function zoomIn(page: Page, steps = 1) {
  for (let i = 0; i < steps; i++) {
    await page.locator('.react-flow__controls-zoomin').click();
    await page.waitForTimeout(250);
  }
}
