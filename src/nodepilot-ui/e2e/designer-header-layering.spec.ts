import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * Regression for the editor header stacking context: header popovers must sit above
 * canvas-local overlays such as the folder path breadcrumb.
 */

const WF_ID = 'eeeeeeee-4545-4545-4545-454545454545';
const ROOT_ID = '00000000-0000-0000-0000-000000000001';
const VRZ_ID = 'f4545454-0000-0000-0000-000000000001';
const MAINT_ID = 'f4545454-0000-0000-0000-000000000002';
const CM_ID = 'f4545454-0000-0000-0000-000000000003';
const NODE_ID = 'step-layer-probe';
const NOW = '2026-07-08T00:00:00.000Z';

function definition() {
  return {
    nodes: [
      {
        id: NODE_ID,
        type: 'activity',
        position: { x: 40, y: 40 },
        data: { label: 'Layer Probe', activityType: 'log', config: { message: 'probe' } },
      },
    ],
    edges: [],
  };
}

function workflowJson() {
  return {
    id: WF_ID,
    name: 'Layering Probe',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: NOW,
    definitionJson: JSON.stringify(definition()),
    version: 1,
    activityCount: 1,
    triggerTypes: [] as string[],
    createdAt: NOW,
    updatedAt: NOW,
    folderId: CM_ID,
    folderPath: '/VRZ/Maintenance/CM',
    capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
  };
}

function folder(id: string, parentFolderId: string | null, name: string, path: string, depth: number) {
  return {
    id,
    parentFolderId,
    name,
    path,
    depth,
    createdAt: NOW,
    createdByUserId: MOCK_USER.id,
    workflowCount: id === CM_ID ? 1 : 0,
    capabilities: { canRead: true, canRun: true, canEdit: true, canAdmin: true },
  };
}

async function openEditor(page: Page) {
  await seedExpertMode(page);
  await page.setViewportSize({ width: 1024, height: 760 });

  const workflow = workflowJson();
  await page.route((url) => url.pathname === '/api/shared-workflow-folders', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        folder(ROOT_ID, null, 'Root', '/', 0),
        folder(VRZ_ID, ROOT_ID, 'VRZ', '/VRZ', 1),
        folder(MAINT_ID, VRZ_ID, 'Maintenance', '/VRZ/Maintenance', 2),
        folder(CM_ID, MAINT_ID, 'CM', '/VRZ/Maintenance/CM', 3),
      ]),
    }),
  );
  await page.route((url) => url.pathname === '/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow]) }),
  );
  await page.route((url) => url.pathname === `/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflow) }),
  );

  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator(`.react-flow__node[data-id="${NODE_ID}"]`)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('folder-path-breadcrumb')).toBeVisible({ timeout: 10_000 });
}

test.describe('Designer header layering', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('header popovers stack above the canvas folder-path breadcrumb layer', async ({ page }) => {
    await openEditor(page);

    // Open a header popover (the skin switcher) so a `[role="menu"]` is present. The skin
    // switcher now lives in the right toolbar zone, so it no longer geometrically overlaps
    // the top-left breadcrumb — but the layering INVARIANT still must hold: the header is its
    // own stacking context (z-45) sitting above the canvas breadcrumb layer (z-30), and its
    // popovers (z-50) render above it. That ordering is what the original stacking-context
    // regression was about.
    await page.getByTestId('toggle-skin').click();
    await expect(page.getByRole('menu')).toBeVisible();

    const z = await page.evaluate(() => {
      const menu = document.querySelector('[role="menu"]');
      const breadcrumb = document.querySelector('[data-testid="folder-path-breadcrumb"]');
      if (!menu || !breadcrumb) throw new Error('missing menu or breadcrumb');
      return {
        menuZIndex: getComputedStyle(menu).zIndex,
        headerZIndex: getComputedStyle(document.querySelector('.np-editor-header')!).zIndex,
        breadcrumbParentZIndex: getComputedStyle(breadcrumb.parentElement!).zIndex,
      };
    });

    expect(z.menuZIndex).toBe('50');
    expect(z.headerZIndex).toBe('45');
    expect(z.breadcrumbParentZIndex).toBe('30');
    // The header stacking context (z-45) sits above the canvas breadcrumb (z-30), so every
    // header popover renders above the breadcrumb regardless of geometric position.
    expect(Number(z.headerZIndex)).toBeGreaterThan(Number(z.breadcrumbParentZIndex));
  });
});
