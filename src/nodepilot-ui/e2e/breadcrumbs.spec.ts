import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 72 — WorkflowBreadcrumbs (Calls → Navigation) (lines 3910-3927).
 *
 * WorkflowBreadcrumbs renders a "Calls →" strip under the editor header for every static
 * outgoing workflow reference (`startWorkflow.config.workflowNameOrId` /
 * `forEach.config.childWorkflowNameOrId`). A resolvable ref → a clickable pill linking to the
 * child editor. An unresolvable ref → an amber ⚠ pill (not a link) with a "not found" tooltip.
 * Dynamic `{{…}}` refs are skipped entirely.
 *
 * Resolution reads GET /api/workflows (the full list) — we mock it to include (or omit) the
 * referenced child so both branches are deterministic.
 *
 * Hermetic: page.route mocks. SPA renders ENGLISH under Playwright.
 */

const PARENT_ID = 'e7272727-7272-7272-7272-727272727272';
const CHILD_ID = 'ce727272-7272-7272-7272-727272727272';
const CHILD_NAME = 'Nightly Child WF';

function definition(refName: string) {
  return JSON.stringify({
    nodes: [
      { id: 'step-call', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'Call child', activityType: 'startWorkflow', config: { workflowNameOrId: refName } } },
    ],
    edges: [],
  });
}

function parentJson(refName: string) {
  return JSON.stringify({
    id: PARENT_ID, name: 'Parent WF', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(refName), version: 1,
  });
}

/** Mock the workflows list. childPresent controls whether the ref resolves. */
async function openEditor(page: Page, refName: string, childPresent: boolean) {
  await page.route('**/api/workflows', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(
        childPresent
          ? [
              { id: PARENT_ID, name: 'Parent WF', description: '', isEnabled: false },
              { id: CHILD_ID, name: CHILD_NAME, description: 'the child', isEnabled: true },
            ]
          : [{ id: PARENT_ID, name: 'Parent WF', description: '', isEnabled: false }],
      ),
    }),
  );
  await page.route(`**/api/workflows/${PARENT_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: parentJson(refName) }),
  );
  await page.goto(`/workflows/${PARENT_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-call"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('WorkflowBreadcrumbs (Teil 72)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('72.1 — resolvable child renders a "Calls →" pill that links to the child editor', async ({ page }) => {
    await openEditor(page, CHILD_NAME, true);

    // The "Calls →" strip is present.
    await expect(page.getByText(/Calls/i).first()).toBeVisible({ timeout: 10_000 });

    // The pill is a Link to /workflows/<childId> carrying the child name.
    const pill = page.getByRole('link', { name: new RegExp(CHILD_NAME, 'i') });
    await expect(pill).toBeVisible();
    await expect(pill).toHaveAttribute('href', new RegExp(`/workflows/${CHILD_ID}`));

    // Clicking navigates to the child workflow editor route.
    await pill.click();
    await expect(page).toHaveURL(new RegExp(`/workflows/${CHILD_ID}`), { timeout: 10_000 });
  });

  test('72.2 — unresolvable child renders an amber ⚠ pill (not a link) with a not-found tooltip', async ({ page }) => {
    await openEditor(page, 'Ghost Workflow', false);

    await expect(page.getByText(/Calls/i).first()).toBeVisible({ timeout: 10_000 });

    // Broken ref → warning pill carrying the ref name, with a "not found" title; it is NOT a
    // link. The pill renders a Carbon WarningAltFilled <svg> icon (the icon-font migration
    // dropped the lucide markup), so match on the ref text + the icon, and assert the title.
    const broken = page.getByText('Ghost Workflow', { exact: false }).filter({ has: page.locator('svg') });
    await expect(broken).toBeVisible();
    await expect(broken).toHaveAttribute('title', /not found|broken/i);
    await expect(page.getByRole('link', { name: /Ghost Workflow/i })).toHaveCount(0);
  });

  test('72.3 — dynamic {{…}} reference does not appear as a breadcrumb', async ({ page }) => {
    // A template-valued ref resolves at runtime → WorkflowBreadcrumbs skips it. With only a
    // dynamic ref the strip has nothing to show and is not rendered at all.
    await openEditor(page, '{{globals.TARGET_WF}}', true);

    await expect(page.locator('.react-flow__node[data-id="step-call"]')).toBeVisible();
    await expect(page.getByText(/Calls/i)).toHaveCount(0);
    await expect(page.getByText('{{globals.TARGET_WF}}')).toHaveCount(0);
  });
});
