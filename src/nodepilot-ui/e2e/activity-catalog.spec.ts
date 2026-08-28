import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md section 36 — Activity Catalog.
 *
 * The catalog ships to the frontend as a generated constant (`lib/activityCatalog.generated.ts`)
 * and its only UI surface is the editor's left "Node Library" sidebar. These specs check that the
 * sidebar renders the documented categories and headline activity types (36.1) and that a Viewer
 * sees the same catalog read-only (36.2).
 *
 * Hermetic: unmocked /api/* calls return an empty 200 via the catch-all in fixtures/mockApi.ts.
 * The workflow is mocked as checked out by the current user so the editor opens editable.
 * The SPA renders English under Playwright, so use bilingual /regex/i and role selectors only.
 */

const WF_ID = 'cccccccc-3636-3636-3636-000000000036';

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Catalog',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    ...overrides,
  });
}

/**
 * Scopes assertions to the left sidebar's Node Library panel, which owns the categories. The
 * expanded panel has no "Node Library" heading (that label is only the collapsed-tab tooltip),
 * so the anchor is the sidebar <aside> containing the activity search box.
 */
function library(page: Page) {
  return page.locator('aside').filter({ has: page.getByPlaceholder(/search nodes|nodes suchen/i) });
}

/**
 * The editor's left sidebar defaults to the "Workflows" tab; the activity catalog lives behind
 * the "Nodes" tab. Clicks it and waits for the search box, which confirms the catalog mounted.
 */
async function openNodeLibrary(page: Page) {
  await page.getByRole('button', { name: /^nodes$|^knoten$/i }).click();
  await expect(page.getByPlaceholder(/search nodes|nodes suchen/i)).toBeVisible({ timeout: 15_000 });
}

/**
 * Category sections are collapsible and the editor seeds the "Actions" bucket collapsed by
 * default (localStorage `nodepilot.designer.collapsedCategories`). The header is a button
 * carrying aria-expanded; click it when collapsed so the activity entries render.
 */
async function expandCategory(page: Page, label: RegExp) {
  const header = library(page).getByRole('button', { name: label }).first();
  if ((await header.getAttribute('aria-expanded')) === 'false') {
    await header.click();
  }
}

test.describe('Activity Catalog (Teil 36)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
  });

  test('36.1 — Node Library renders catalog categories and headline activity types', async ({ page }) => {
    await page.goto(`/workflows/${WF_ID}`);
    await openNodeLibrary(page);

    const lib = library(page);

    // Documented category buckets, rendered as headers with bilingual labels.
    await expect(lib.getByText(/^triggers$/i)).toBeVisible();
    await expect(lib.getByText(/^actions$|^aktionen$/i)).toBeVisible();
    await expect(lib.getByText(/control flow|kontrollfluss/i)).toBeVisible();
    await expect(lib.getByText(/^logic$|^logik$/i)).toBeVisible();

    // The "Actions" bucket is collapsed by default, so expand it to render its entries.
    await expandCategory(page, /actions|aktionen/i);

    // Headline activity types from the contract (36.1), covering Engine-local, control-flow and
    // remote activities. Labels are i18n-translated, so match the English text the build renders.
    // The accessible name of a palette entry is "<material-icon-text> <label>"; match the label
    // substring precisely so entries do not collide with Snippet buttons, whose descriptions can
    // mention the same words.
    await expect(lib.getByRole('button', { name: /run script/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /send email/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /sql query/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /http request/i })).toBeVisible(); // restApi
    await expect(lib.getByRole('button', { name: /delay \/ wait/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /junction \/ merge/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /log message/i })).toBeVisible();

    // Control-flow and trigger entries from the default-expanded categories, showing those
    // buckets are populated too.
    await expect(lib.getByRole('button', { name: /start workflow/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /manual trigger/i })).toBeVisible();
  });

  test('36.1b — palette search narrows the catalog to a single activity', async ({ page }) => {
    await page.goto(`/workflows/${WF_ID}`);
    await openNodeLibrary(page);
    const lib = library(page);

    const search = lib.getByRole('textbox').first();
    await search.fill('sql');
    await expect(lib.getByRole('button', { name: /sql query/i })).toBeVisible();
    // A non-matching activity is filtered out.
    await expect(lib.getByRole('button', { name: /run script/i })).toHaveCount(0);
  });

  test('36.2 — catalog is visible (read-only) for a Viewer', async ({ page }) => {
    // Override the role to Viewer; the editor opens read-only but still renders the catalog.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, username: 'e2e-viewer', role: 'Viewer' }),
      }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await openNodeLibrary(page);
    const lib = library(page);
    // Categories and entries still render for a Viewer; only editing is blocked.
    await expect(lib.getByText(/^actions$|^aktionen$/i)).toBeVisible();
    await expandCategory(page, /actions|aktionen/i);
    const runScript = lib.getByRole('button', { name: /run script/i });
    await expect(runScript).toBeVisible();
    // Read-only: the palette entry is disabled and cannot be dragged into the workflow.
    await expect(runScript).toBeDisabled();
  });
});
