import { test, expect } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Part 67 — import dialog with drag and drop.
 *
 * Hermetic: page.route mocks only. The import affordance on WorkflowsPage is an "Import" toolbar
 * button that triggers a hidden `<input type="file" accept="application/json,.json" multiple>`
 * (importInputRef). Each selected file is read, JSON-parsed and POSTed to
 * `/api/workflows/import` as its own envelope; the aggregated result is shown as a toast.
 *
 * WorkflowsPage has no HTML5 drop zone, so the file-drop half of this part does not apply here:
 * the page's drag and drop moves workflow rows into folder tree nodes. The tests drive the hidden
 * input with setInputFiles instead. MOCK_USER is an Admin, so the Import button renders, and the
 * SPA renders English under Playwright.
 */

function envelope(name: string) {
  // Minimal nodepilot-workflow-export/v1 envelope that the import handler parses and posts.
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

    // The Import button renders because an Admin may edit the import target folder.
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

    // One import POST fires and the summary appears as a success toast, because a clean
    // import reports no per-file failures.
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

    // Only the valid file reaches the server; the broken one fails the client-side JSON.parse
    // before any POST. With at least one failure, the aggregated summary surfaces as a
    // long-lived error toast that names the failed file.
    await expect.poll(() => importPosts, { timeout: 10_000 }).toBe(1);
    const errorToast = page.getByTestId('toast-error');
    await expect(errorToast).toContainText(/broken\.json/i, { timeout: 10_000 });
    await expect(errorToast).toContainText(/good\.json|import/i);
  });
});
