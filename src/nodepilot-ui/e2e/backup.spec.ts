import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * System configuration backup and restore (ADR 0001) on the /backup admin page (BackupPage).
 * Two tabs: "Create backup" picks sections plus a passphrase and downloads a sealed .npbackup,
 * "Restore" uploads one and shows an integrity-checked preview with a per-section conflict
 * policy before running. E2ETests.md covers Teil 9.x at the API level; this is the UI part.
 *
 * Hermetic: page.route() mocks only. /backup is Admin-only (App.tsx <AdminOnly>) and the default
 * MOCK_USER is Admin, so the page mounts. The SPA renders EN under Playwright. The file picker
 * is a real <input type=file>, so Playwright can drive it with setInputFiles.
 */

function manifest() {
  return JSON.stringify({
    sections: [
      { section: 'workflows', count: 5 },
      { section: 'machines', count: 3 },
    ],
  });
}

async function mockManifest(page: Page) {
  await page.route('**/api/backup/manifest', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: manifest() }),
  );
}

test.describe('Backup & Restore (/backup)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page); // MOCK_USER is Admin, so the page mounts
  });

  // ---------- Create-backup tab ----------
  test('backup tab: the manifest sections render with counts and are checked by default', async ({ page }) => {
    await mockManifest(page);
    await page.goto('/backup');

    const workflows = page.locator('label').filter({ hasText: /^Workflows/ });
    const machines = page.locator('label').filter({ hasText: /^Machines/ });
    await expect(workflows).toBeVisible({ timeout: 15_000 });
    await expect(workflows.getByText('5')).toBeVisible();
    await expect(machines.getByText('3')).toBeVisible();
    // Sections start all-selected so the default backup is complete.
    await expect(workflows.getByRole('checkbox')).toBeChecked();
    await expect(machines.getByRole('checkbox')).toBeChecked();
  });

  test('backup tab: a too-short passphrase is rejected with a client-side error (no request)', async ({ page }) => {
    await mockManifest(page);
    let exportHit = false;
    await page.route('**/api/backup/export', (route) => { exportHit = true; return route.fulfill({ status: 200, body: 'x' }); });

    await page.goto('/backup');
    await expect(page.locator('label').filter({ hasText: /^Workflows/ })).toBeVisible({ timeout: 15_000 });

    await page.locator('input[type="password"]').first().fill('tooshort'); // 8 < 12
    await page.getByRole('button', { name: /download backup|backup erstellen|herunterladen/i }).click();

    // Match the error specifically (the field hint also mentions "at least 12 characters").
    await expect(page.getByText(/must be at least 12 characters|muss mindestens 12 zeichen/i)).toBeVisible();
    expect(exportHit).toBe(false);
  });

  test('backup tab: mismatched passphrases are rejected before any request', async ({ page }) => {
    await mockManifest(page);
    let exportHit = false;
    await page.route('**/api/backup/export', (route) => { exportHit = true; return route.fulfill({ status: 200, body: 'x' }); });

    await page.goto('/backup');
    await expect(page.locator('label').filter({ hasText: /^Workflows/ })).toBeVisible({ timeout: 15_000 });

    const pw = page.locator('input[type="password"]');
    await pw.nth(0).fill('correct-horse-battery'); // ≥ 12
    await pw.nth(1).fill('different-passphrase-99');
    await page.getByRole('button', { name: /download backup|backup erstellen|herunterladen/i }).click();

    await expect(page.getByText(/do not match|stimmen nicht überein/i)).toBeVisible();
    expect(exportHit).toBe(false);
  });

  test('backup tab: a valid passphrase POSTs /backup/export with the selected sections', async ({ page }) => {
    await mockManifest(page);
    let exportBody: { sections?: string[]; passphrase?: string } | null = null;
    await page.route('**/api/backup/export', (route) => {
      exportBody = route.request().postDataJSON();
      return route.fulfill({
        status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="nodepilot-backup.npbackup"' },
        contentType: 'application/octet-stream',
        body: 'sealed-backup-bytes',
      });
    });

    await page.goto('/backup');
    await expect(page.locator('label').filter({ hasText: /^Workflows/ })).toBeVisible({ timeout: 15_000 });

    const pw = page.locator('input[type="password"]');
    await pw.nth(0).fill('correct-horse-battery');
    await pw.nth(1).fill('correct-horse-battery');
    await page.getByRole('button', { name: /download backup|backup erstellen|herunterladen/i }).click();

    await expect.poll(() => exportBody, { timeout: 10_000 }).not.toBeNull();
    expect(exportBody!.sections).toEqual(expect.arrayContaining(['workflows', 'machines']));
    expect(exportBody!.passphrase).toBe('correct-horse-battery');
    // Success confirmation after the download fires.
    await expect(page.getByText(/backup downloaded|backup heruntergeladen/i)).toBeVisible();
  });

  // ---------- Restore tab ----------
  test('restore tab: preview shows the integrity badge, the diff table and per-section policy', async ({ page }) => {
    await mockManifest(page);
    await page.route('**/api/backup/preview', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          integrityVerified: true,
          appVersion: '1.4.0',
          sections: [{ section: 'workflows', inBackup: 5, new: 2, conflicts: 3 }],
          warnings: [],
        }),
      }),
    );

    await page.goto('/backup');
    await page.getByRole('button', { name: /^restore$|^wiederherstellen$/i }).click();

    await page.locator('input[type="file"]').setInputFiles({
      name: 'cfg.npbackup', mimeType: 'application/octet-stream', buffer: Buffer.from('sealed'),
    });
    await page.locator('input[type="password"]').fill('correct-horse-battery');
    await page.getByRole('button', { name: /^preview$|^vorschau$/i }).click();

    await expect(page.getByText(/integrity verified|integrität verifiziert/i)).toBeVisible({ timeout: 10_000 });
    // The diff row surfaces the conflict count and offers a per-section conflict policy select.
    const policySelect = page.locator('table select').first();
    await expect(policySelect).toBeVisible();
    await expect(policySelect.locator('option', { hasText: /overwrite|überschreiben/i })).toHaveCount(1);
  });

  test('restore tab: a file alone previews nothing — the passphrase is required first', async ({ page }) => {
    // Envelope v4 encrypts and authenticates the whole payload, so there is no structure-only
    // preview to show before the passphrase is known. Picking a file must therefore issue no
    // request at all; anything else would imply the file can be read without it.
    await mockManifest(page);
    let previewCalls = 0;
    await page.route('**/api/backup/preview', (route) => {
      previewCalls += 1;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          integrityVerified: true, appVersion: '1.4.0',
          sections: [{ section: 'workflows', inBackup: 5, new: 2, conflicts: 3 }],
          warnings: [],
        }),
      });
    });

    await page.goto('/backup');
    await page.getByRole('button', { name: /^restore$|^wiederherstellen$/i }).click();
    await page.locator('input[type="file"]').setInputFiles({
      name: 'cfg.npbackup', mimeType: 'application/octet-stream', buffer: Buffer.from('sealed'),
    });

    const previewButton = page.getByRole('button', { name: /^preview$|^vorschau$/i });
    await expect(previewButton).toBeDisabled();
    expect(previewCalls).toBe(0);

    await page.locator('input[type="password"]').fill('correct-horse-battery');
    await expect(previewButton).toBeEnabled();
    await previewButton.click();

    await expect(page.locator('table select').first()).toBeVisible({ timeout: 10_000 });
    expect(previewCalls).toBe(1);
  });

  test('restore tab: choosing a policy and confirming POSTs /backup/restore and shows the result', async ({ page }) => {
    await mockManifest(page);
    await page.route('**/api/backup/preview', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          integrityVerified: true, appVersion: '1.4.0',
          sections: [{ section: 'workflows', inBackup: 5, new: 2, conflicts: 3 }],
          warnings: [],
        }),
      }),
    );
    let restoreHit = false;
    await page.route('**/api/backup/restore', (route) => {
      restoreHit = true;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          sections: [{ section: 'workflows', created: 2, overwritten: 3, skipped: 0, renamed: 0 }],
          settings: null, warnings: [],
        }),
      });
    });
    await page.goto('/backup');
    await page.getByRole('button', { name: /^restore$|^wiederherstellen$/i }).click();
    await page.locator('input[type="file"]').setInputFiles({
      name: 'cfg.npbackup', mimeType: 'application/octet-stream', buffer: Buffer.from('sealed'),
    });
    await page.locator('input[type="password"]').fill('correct-horse-battery');
    await page.getByRole('button', { name: /^preview$|^vorschau$/i }).click();
    await expect(page.getByText(/integrity verified|integrität verifiziert/i)).toBeVisible({ timeout: 10_000 });

    // Pick "Overwrite" for the conflicting section, then run the restore. The run button is the
    // second "Restore" on the page after the tab button; ConfirmHost asks for confirmation.
    await page.locator('table select').first().selectOption('overwrite');
    await page.getByRole('button', { name: /^restore$|^wiederherstellen$/i }).last().click();
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => restoreHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText(/restore complete|wiederherstellung abgeschlossen/i)).toBeVisible();
  });
});
