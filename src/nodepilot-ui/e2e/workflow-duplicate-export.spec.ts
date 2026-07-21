import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 50 — Workflow Duplicate, By-Name-Lookup & Bulk-Export.
 *
 * Hermetic: page.route() mocks only (no backend), per fixtures/mockApi.ts conventions.
 * EN locale under Playwright; selectors are bilingual / title-attribute based.
 *
 * Covers the UI surfaces of Teil 50:
 *   - 50.1 — Duplicate: the row's duplicate button fires POST /api/workflows/{id}/duplicate.
 *   - 50.5 — Bulk-Export: the "Export All" button GETs /api/workflows/export and triggers a
 *            browser download (downloadFromApi → anchor click).
 *   - Single export (per-row "Export as JSON") fires GET /api/workflows/{id}/export.
 *   - 50.6 — Operator can bulk-export (button visible & functional); the disabled state when
 *            there are zero workflows is also asserted.
 *
 * 50.2/50.3/50.4 (by-name lookup + by-name contract) are pure REST concerns with no UI entry
 * point in this SPA — they're exercised by the dotnet API tests, so they're skipped here.
 */

const ME = MOCK_USER; // Admin
const ME_OPERATOR = { id: '00000000-0000-0000-0000-0000000000a1', username: 'operator1', role: 'Operator' };

const WF_ID = 'a1111111-1111-1111-1111-111111111111';

function workflow(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_ID,
    name: 'E2E_Basic_Test',
    description: '',
    isEnabled: true,
    version: 1,
    activityCount: 2,
    triggerTypes: [] as string[],
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    folderId: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    successCount: 0,
    totalCount: 0,
    avgDurationMs: null,
    lastExecution: null,
    // No capabilities → row falls back to global role flags (Admin/Operator can edit).
    ...overrides,
  };
}

const EXPORT_ENVELOPE = JSON.stringify({
  schema: 'nodepilot-workflow-export/v1',
  exportedAt: '2026-06-18T00:00:00.000Z',
  workflows: [{ name: 'E2E_Basic_Test', definitionJson: '{"nodes":[],"edges":[]}' }],
});

test.describe('Workflow Duplicate, By-Name & Bulk-Export (Teil 50)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME) }),
    );
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
    );
  });

  // ---------- 50.1 — Duplicate ----------
  test('50.1 — duplicate button fires POST /api/workflows/{id}/duplicate', async ({ page }) => {
    let duplicateMethod: string | null = null;
    await page.route(`**/api/workflows/${WF_ID}/duplicate`, (route) => {
      duplicateMethod = route.request().method();
      return route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify(workflow({ id: 'dup-0000', name: 'E2E_Basic_Test (2)' })),
      });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'E2E_Basic_Test' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByRole('button', { name: /duplicate|duplizieren/i }).click();
    await expect.poll(() => duplicateMethod, { timeout: 10_000 }).toBe('POST');
  });

  // ---------- 50.5 — Bulk-Export ----------
  test('50.5 — "Export All" GETs /api/workflows/export and triggers a download', async ({ page }) => {
    let exportHit = false;
    await page.route('**/api/workflows/export', (route) => {
      exportHit = true;
      return route.fulfill({
        status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="nodepilot-workflows.json"' },
        contentType: 'application/json',
        body: EXPORT_ENVELOPE,
      });
    });

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'E2E_Basic_Test' })).toBeVisible({ timeout: 15_000 });

    // The button click both fires GET /export AND triggers an anchor-download — assert both.
    const downloadPromise = page.waitForEvent('download', { timeout: 10_000 });
    await page.getByRole('button', { name: /export all|alle exportieren/i }).click();

    const download = await downloadPromise;
    expect(exportHit).toBe(true);
    expect(download.suggestedFilename()).toBe('nodepilot-workflows.json');
  });

  test('50.5b — per-row "Export as JSON" GETs /api/workflows/{id}/export', async ({ page }) => {
    let perWorkflowExportHit = false;
    await page.route(`**/api/workflows/${WF_ID}/export`, (route) => {
      perWorkflowExportHit = true;
      return route.fulfill({
        status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="E2E_Basic_Test.workflow.json"' },
        contentType: 'application/json',
        body: EXPORT_ENVELOPE,
      });
    });

    await page.goto('/workflows');
    const row = page.getByRole('row').filter({ hasText: 'E2E_Basic_Test' });
    await expect(row).toBeVisible({ timeout: 15_000 });

    const downloadPromise = page.waitForEvent('download', { timeout: 10_000 });
    await row.getByRole('button', { name: /export as json|als json exportieren/i }).click();
    const download = await downloadPromise;
    expect(perWorkflowExportHit).toBe(true);
    expect(download.suggestedFilename()).toBe('E2E_Basic_Test.workflow.json');
  });

  // ---------- 50.6 — Bulk-Export permissions (UI surface) ----------
  test('50.6 — Operator can use Export All (button visible & functional)', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ME_OPERATOR) }),
    );
    let exportHit = false;
    await page.route('**/api/workflows/export', (route) => {
      exportHit = true;
      return route.fulfill({
        status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="nodepilot-workflows.json"' },
        contentType: 'application/json',
        body: EXPORT_ENVELOPE,
      });
    });

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: 'E2E_Basic_Test' })).toBeVisible({ timeout: 15_000 });

    const exportAll = page.getByRole('button', { name: /export all|alle exportieren/i });
    await expect(exportAll).toBeEnabled();
    const downloadPromise = page.waitForEvent('download', { timeout: 10_000 });
    await exportAll.click();
    await downloadPromise;
    expect(exportHit).toBe(true);
  });

  test('Export All is disabled when there are no workflows', async ({ page }) => {
    await page.route('**/api/workflows', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
    await page.goto('/workflows');
    // Empty-state copy renders → list is loaded, not still spinning.
    await expect(page.getByText(/no workflows|keine workflows/i)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /export all|alle exportieren/i })).toBeDisabled();
  });

  // ---------- 50.2 / 50.3 / 50.4 — By-Name lookup + contract (API-only) ----------
  test.skip('50.2/50.3/50.4 — by-name lookup & contract are REST-only (covered by dotnet API tests)', () => {
    // GET /api/workflows/by-name/{name} and .../contract have no dedicated UI entry point in
    // this SPA — the workflow list navigates by id, not by name. Exact-case 404 behaviour and
    // the contract shape (inputs/outputs/hasManualTrigger) are asserted server-side.
  });
});
