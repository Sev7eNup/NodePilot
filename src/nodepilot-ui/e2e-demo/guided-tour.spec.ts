import { expect, test } from '@playwright/test';

for (const lang of ['de', 'en']) {
  test(`task overview navigation works from both tour panels in ${lang}`, async ({ page }) => {
    const overviewPath = lang === 'de' ? '/walkthrough/' : '/en/walkthrough/';
    // The demo-only test server does not host the website. Serve its destination here
    // so this checks an actual document navigation rather than only the link's href.
    await page.route(`**${overviewPath}`, route => route.fulfill({ contentType: 'text/html', body: '<h1>Task overview</h1>' }));
    for (const mode of ['diagnose', 'machine']) {
      await page.goto(`./?tour=${mode}&lang=${lang}`);
      if (mode === 'diagnose') await page.locator('[data-tour-action="permission"]').click();
      else {
        await page.locator('#root input[type="text"]').first().fill('LAB01');
        await page.locator('[data-tour-action="none"]').click();
      }
      await page.getByRole('link', { name: lang === 'de' ? 'Alle zehn Aufgaben' : 'All ten tasks', exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`${overviewPath}$`));
      await expect(page.getByRole('heading', { name: 'Task overview' })).toBeVisible();
    }
  });
}

test('runs the guided file task through the real dialog and investigates the failed copy', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('./?tour=file&lang=de');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'start');
  await expect(page.getByRole('button', { name: 'Ausführen', exact: true })).toBeVisible();
  await page.screenshot({ path: test.info().outputPath('guided-start.png'), fullPage: true });
  await page.getByRole('button', { name: 'Ausführen', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByPlaceholder('30', { exact: true }).fill('60');
  await dialog.getByRole('button', { name: /ausführen/i }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'success', { timeout: 20000 });
  await page.locator('[data-tour-action="result"]').click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'result');
  await expect(page.locator('#root')).toContainText('"pollIntervalSeconds": 60');
  await expect(page.locator('#root')).toContainText('C:\\Apps\\FileWorker\\Test\\config\\settings.json');
  await page.screenshot({ path: test.info().outputPath('guided-result.png'), fullPage: true });
  await expect(page.locator('.np-tour [role="status"]')).toHaveText('✓ Aufgabe abgeschlossen');
  await expect(page.locator('[data-tour-action="next-task"]')).toHaveAttribute('href', /tour=decision/);
  await page.locator('[data-tour-action="failure"]').click();
  await expect(page.locator('#root')).toContainText('Access denied');
  await expect(page.locator('#root')).toContainText('File Copy: configuration');
  await page.locator('[data-tour-action="script"]').click();
  await expect(page.locator('.np-tour [role="status"]')).toBeVisible();
  await page.locator('[data-tour-action="permission"]').click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done');
  await page.screenshot({ path: test.info().outputPath('guided-failure.png'), fullPage: true });
  await page.locator('[data-tour-action="explore"]').click();
  await expect(page.locator('.np-tour')).toBeHidden();
  expect(new URL(page.url()).searchParams.has('tour')).toBe(false);
  expect(errors).toEqual([]);
});

test('opens the diagnosis directly in English, survives navigation and resets on reload', async ({ page }) => {
  await page.goto('./?tour=diagnose&lang=en');
  await expect(page.locator('.np-tour')).toContainText('Find the cause');
  await expect(page.locator('.np-tour h2')).toHaveText('Find the cause');
  await expect(page.locator('#root')).toContainText('Access denied');
  await page.getByRole('link', { name: 'Workspace', exact: true }).click();
  await page.locator('[data-tour-action="resume"]').click();
  await expect(page.locator('#root')).toContainText('Access denied');
  await page.locator('[data-tour-action="permission"]').click();
  await expect(page.locator('.np-tour')).not.toContainText('You started a workflow');
  await page.reload();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'diagnose');
  await page.locator('[data-tour-action="close"]').click();
  await expect(page.locator('.np-tour')).toBeHidden();
});

test('offers the native run dialog on a phone and keeps the guide within the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('./?tour=file&lang=en');
  await expect(page.locator('.np-tour')).toContainText('workflow list');
  const card = page.getByTestId('mobile-card-list').locator('.np-card').filter({ hasText: 'Configuration File Delivery' });
  await card.getByRole('button', { name: 'Run Now' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByPlaceholder('30', { exact: true }).fill('45');
  await dialog.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'success', { timeout: 20000 });
  await page.locator('[data-tour-action="result"]').click();
  await expect(page.locator('#root')).toContainText('"pollIntervalSeconds": 45');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: test.info().outputPath('guided-phone.png'), fullPage: true });
});
