import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 67 — Import-Dialog mit Drag & Drop (lines 3787-3802).
 *
 * Hermetic: page.route() mocks only. The WorkflowsPage import affordance is an "Import" toolbar
 * button that triggers a hidden `<input type="file" accept="application/json,.json" multiple>`
 * (importInputRef). On change, each selected file is read, JSON-parsed, and POSTed to
 * `/api/workflows/import` (one envelope per file); the aggregated result is shown via a native
 * `alert()` summary (workflows:importedSummary / importedSummaryBatch).
 *
 * There is NO HTML5 drop-zone on WorkflowsPage — the "Drag & Drop" file-drop part of 67.1 is
 * not present in this surface (the page's drag-and-drop is for moving workflow ROWS into folder
 * tree nodes, not for importing files). So:
 *   - 67.1 — VERIFIED via setInputFiles: button + hidden input exist; selecting a file POSTs the
 *            import and the result summary (alert) appears. The file-DROP-onto-zone gesture is
 *            SKIPPED with a reason.
 *   - 67.2 — Multi-file import: two files → two import POSTs; one invalid-JSON file fails
 *            independently while the valid file still imports (per-file result aggregation).
 *
 * MOCK_USER is Admin → canEditRoot (Root falls back to canWrite when shared-folders empty),
 * so the Import button renders. SPA renders ENGLISH under Playwright.
 */

function envelope(name: string) {
  // Minimal nodepilot-workflow-export/v1 envelope the import handler will JSON.parse + POST.
  return JSON.stringify({
    format: 'nodepilot-workflow-export/v1',
    workflows: [{ name, definitionJson: '{"nodes":[],"edges":[]}' }],
  });
}

test.describe('Import-Dialog (Teil 67)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('67.1 — Import button + hidden file input; selecting a file POSTs /workflows/import and shows a result summary', async ({ page }) => {
    let importPosts = 0;
    await page.route('**/api/workflows/import', (route) => {
      importPosts += 1;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ created: 1, workflows: [{ id: 'imp-1', name: 'Imported_WF', originalName: 'Imported_WF' }], errors: [] }),
      });
    });

    await page.goto('/workflows');

    // Import button is present (Admin → can edit the import target folder).
    const importBtn = page.getByRole('button', { name: /^import$/i }).first();
    await expect(importBtn).toBeVisible({ timeout: 15_000 });

    // The hidden multiple-file input is wired to the button.
    const fileInput = page.locator('input[type="file"][accept="application/json,.json"]');
    await expect(fileInput).toHaveCount(1);

    // setInputFiles drives the change handler directly (HTML5 file-drop is not synthesizable).
    await fileInput.setInputFiles({
      name: 'wf.json',
      mimeType: 'application/json',
      buffer: Buffer.from(envelope('Imported_WF')),
    });

    // One import POST fired and the summary appears as a SUCCESS toast (a clean import
    // has no failures; the former blocking alert() was retired).
    await expect.poll(() => importPosts, { timeout: 10_000 }).toBe(1);
    await expect(page.getByTestId('toast-success')).toContainText(/import|wf\.json|1/i, { timeout: 10_000 });
  });

  test('67.1-drop — file-drop onto a drag-zone is skipped (no HTML5 drop-zone on WorkflowsPage + drop is not synthesizable)', async () => {
    test.skip(true, 'WorkflowsPage import is a hidden file-input triggered by the Import button; it has no HTML5 file-drop zone, and a real dataTransfer file drop is not synthesizable in Playwright. setInputFiles covers the import path (67.1).');
  });

  test('67.2 — multi-file import: a bad-JSON file fails independently while the valid file still imports', async ({ page }) => {
    let importPosts = 0;
    await page.route('**/api/workflows/import', (route) => {
      importPosts += 1;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ created: 1, workflows: [{ id: 'imp-x', name: 'Good_WF', originalName: 'Good_WF' }], errors: [] }),
      });
    });

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: /^import$/i }).first()).toBeVisible({ timeout: 15_000 });

    const fileInput = page.locator('input[type="file"][accept="application/json,.json"]');
    await fileInput.setInputFiles([
      { name: 'good.json', mimeType: 'application/json', buffer: Buffer.from(envelope('Good_WF')) },
      { name: 'broken.json', mimeType: 'application/json', buffer: Buffer.from('{ this is not valid json ') },
    ]);

    // Only the valid file reaches the server (the broken one fails client-side JSON.parse
    // before any POST). Because at least one file failed, the aggregated per-file summary
    // surfaces as a long-lived ERROR toast naming the failed file.
    await expect.poll(() => importPosts, { timeout: 10_000 }).toBe(1);
    const errorToast = page.getByTestId('toast-error');
    await expect(errorToast).toContainText(/broken\.json/i, { timeout: 10_000 });
    await expect(errorToast).toContainText(/good\.json|import/i);
  });
});
