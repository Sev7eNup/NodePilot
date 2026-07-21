import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 36 — Activity Catalog (lines 3055-3071).
 *
 * The backend `GET /api/activity-catalog` is the source of truth, but the frontend ships the
 * catalog as a build-time generated constant (`lib/activityCatalog.generated.ts`) that the
 * editor's left "Node Library" sidebar renders into categories + draggable activity entries.
 * That sidebar IS the only UI surface for the catalog, so these specs assert it renders the
 * documented categories and the headline activity types (36.1) and that the catalog is equally
 * visible to a Viewer — read-only, just non-draggable (36.2).
 *
 * Hermetic: per fixtures/mockApi.ts the predicate catch-all answers every unmocked /api/* with
 * an empty 200. The workflow is mocked locked-by-me (checkedOutByUserId === MOCK_USER.id) so the
 * editor opens editable and the palette entries are interactive; for the Viewer test the role is
 * overridden and the palette is still rendered but disabled.
 *
 * SPA renders ENGLISH under Playwright → bilingual /regex/i + role selectors only.
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
 * Scope assertions to the left sidebar's Node-Library panel (it owns the categories). The expanded
 * panel renders no "Node Library" heading — that label exists only as the collapsed-tab tooltip —
 * so we anchor on the sidebar <aside> that owns the activity search box.
 */
function library(page: Page) {
  return page.locator('aside').filter({ has: page.getByPlaceholder(/search nodes|nodes suchen/i) });
}

/**
 * The editor's left sidebar defaults to the "Workflows" tab; the activity catalog lives behind
 * the "Nodes" tab. Click it and wait for the activity search box to confirm the catalog mounted.
 */
async function openNodeLibrary(page: Page) {
  await page.getByRole('button', { name: /^nodes$|^knoten$/i }).click();
  await expect(page.getByPlaceholder(/search nodes|nodes suchen/i)).toBeVisible({ timeout: 15_000 });
}

/**
 * Category sections are collapsible and the editor seeds the "Actions" bucket collapsed by
 * default (localStorage `nodepilot.designer.collapsedCategories` = {Actions:true}). The header
 * is a button carrying aria-expanded; click it when collapsed so the activity entries render.
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

    // Categories (the five documented buckets — bilingual labels, rendered as headers).
    await expect(lib.getByText(/^triggers$/i)).toBeVisible();
    await expect(lib.getByText(/^actions$|^aktionen$/i)).toBeVisible();
    await expect(lib.getByText(/control flow|kontrollfluss/i)).toBeVisible();
    await expect(lib.getByText(/^logic$|^logik$/i)).toBeVisible();

    // The "Actions" bucket is collapsed by default — expand it so its entries render.
    await expandCategory(page, /actions|aktionen/i);

    // Headline activity types from the contract (Teil 36.1). Labels are i18n-translated, so we
    // match the EN label text the preview build renders. These cover an Engine-local
    // (Run Script/Delay), control-flow (Junction/Start Workflow), and Remote-ish slice.
    // The accessible name of each palette entry is "<material-icon-text> <label>"; we match the
    // label substring. Use precise labels so the entries don't collide with Snippet buttons (a
    // snippet description can mention "Junction" etc.).
    await expect(lib.getByRole('button', { name: /run script/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /send email/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /sql query/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /http request/i })).toBeVisible(); // restApi
    await expect(lib.getByRole('button', { name: /delay \/ wait/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /junction \/ merge/i })).toBeVisible();
    await expect(lib.getByRole('button', { name: /log message/i })).toBeVisible();

    // Control-flow + trigger entries from other (default-expanded) categories, proving those
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
    // Categories + entries still render for a Viewer — the endpoint is read-only.
    await expect(lib.getByText(/^actions$|^aktionen$/i)).toBeVisible();
    await expandCategory(page, /actions|aktionen/i);
    const runScript = lib.getByRole('button', { name: /run script/i });
    await expect(runScript).toBeVisible();
    // Read-only: the palette entry is disabled (cannot drag/add into a productive workflow).
    await expect(runScript).toBeDisabled();
  });
});
