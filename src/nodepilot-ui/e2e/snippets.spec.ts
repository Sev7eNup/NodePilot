import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 74 — Workflow-Snippets (NodeLibrary) (lines 3946-3956).
 *
 * The Node Library tab has a "Snippets" section (SnippetsSection in NodeLibrary.tsx) listing
 * reusable mini-patterns (WORKFLOW_SNIPPETS: ForEach, HTTP retry, Parallel fan-out, Try-Catch).
 * Clicking a snippet calls `addSnippet` → insertSnippet() which appends the snippet's nodes +
 * edges with freshly-generated ids (no clash). Viewers see the snippets disabled.
 *
 * Click-add is the real code path (the entries are also drag-sources, but d3-drag isn't
 * synthesizable). For a Viewer the section renders but each button is disabled.
 *
 * Hermetic: page.route mocks. SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'e7474747-7474-7474-7474-747474747474';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-seed', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'Seed', activityType: 'log', config: { message: 'seed' } } },
    ],
    edges: [],
  });
}

function workflowJson(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Snippets', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
    ...overrides,
  });
}

const saveButton = (page: Page) =>
  page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();

async function openNodesTab(page: Page) {
  await expect(page.locator('.react-flow__node[data-id="step-seed"]')).toBeVisible({ timeout: 20_000 });
  await page.locator('aside button').filter({ hasText: /Nodes|Knoten/ }).first().click();
  // Snippets section is rendered (default expanded).
  await expect(page.getByRole('button').filter({ hasText: /Snippets/i }).first()).toBeVisible();
}

test.describe('Workflow-Snippets (NodeLibrary) (Teil 74)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('74.1 — snippets list and clicking one inserts its nodes + edges with fresh ids', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });
    await page.goto(`/workflows/${WF_ID}`);
    await openNodesTab(page);

    // Known snippets are listed.
    await expect(page.getByText(/ForEach over a list/i)).toBeVisible();
    await expect(page.getByText(/Try-Catch around script/i)).toBeVisible();
    await expect(page.getByText(/Parallel fan-out/i)).toBeVisible();

    // Insert the Try-Catch snippet (3 nodes + 3 edges) by clicking it.
    await page.getByRole('button').filter({ hasText: /Try-Catch around script/i }).click();

    // Save → the PUT carries the original seed node + the snippet's 3 nodes / 3 edges.
    await saveButton(page).click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: { id: string }[]; edges: { id: string }[] };
    expect(def.nodes).toHaveLength(4);          // 1 seed + 3 from snippet
    expect(def.edges.length).toBeGreaterThanOrEqual(3);
    // Fresh, unique ids (no clash with the seed or among themselves).
    const ids = def.nodes.map((n) => n.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('step-seed');
  });

  test('74.2 — a Viewer sees the snippet buttons disabled', async ({ page }) => {
    // Viewer role + workflow not locked-by-me → canWrite is false; SnippetsSection renders the
    // buttons disabled with a "not in edit mode" title.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }) }),
    );
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson({ isEnabled: true, checkedOutByUserId: null, checkedOutAt: null }) }),
    );
    await page.goto(`/workflows/${WF_ID}`);
    await openNodesTab(page);

    const snippetBtn = page.getByRole('button').filter({ hasText: /ForEach over a list/i }).first();
    await expect(snippetBtn).toBeVisible();
    await expect(snippetBtn).toBeDisabled();
  });
});
