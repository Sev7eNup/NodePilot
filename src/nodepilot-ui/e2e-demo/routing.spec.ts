import { expect, test } from '@playwright/test';

test('opens and reloads a workflow detail with working assets and root links', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('./workflows?lang=en');
  const id = await page.evaluate(async () => (await (await fetch('/api/workflows')).json())[0].id as string);
  await page.goto(`./workflows/${id}`);
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
  await expect(page.locator('.np-demo-bar__home')).toHaveAttribute('href', '/');
  expect(await page.locator('meta[name="robots"]').getAttribute('content')).toBe('noindex');
  expect(new URL(page.url()).hash).toBe('');
  // The designer has no documentation link; nested layout routes do.
  await page.goto('./metrics/workflows');
  await expect(page.locator('a[href="/docs/"]').first()).toBeVisible();
  expect(errors).toEqual([]);
});

test('converts legacy demo links while retaining language and execution query', async ({ page }) => {
  await page.goto('./?lang=de&id=old#/executions?id=new');
  await expect(page).toHaveURL(/\/demo\/executions\?lang=de&id=new$/);
  await expect(page.locator('.np-demo-bar')).toBeVisible();
  await page.reload();
  await expect(page).toHaveURL(/\/demo\/executions\?lang=de&id=new$/);
});

test('uses browser history without resetting the world', async ({ page }) => {
  await page.goto('./workflows?lang=en');
  await expect(page.locator('a[href="/demo/executions"]').first()).toBeVisible();
  await page.evaluate(() => { document.body.dataset.routingMarker = 'same-world'; });
  await page.locator('a[href="/demo/executions"]').first().click();
  await expect(page).toHaveURL(/\/demo\/executions$/);
  await page.goBack();
  await expect(page).toHaveURL(/\/demo\/workflows\?lang=en$/);
  await page.goForward();
  await expect(page).toHaveURL(/\/demo\/executions$/);
  await expect(page.locator('body')).toHaveAttribute('data-routing-marker', 'same-world');
});

test('serves HTML for demo paths but leaves missing assets as 404', async ({ request }) => {
  const page = await request.get('./workflows/deep-link');
  expect(page.status()).toBe(200);
  expect(page.headers()['content-type']).toContain('text/html');
  for (const path of ['./assets/missing.js', './assets/missing', './missing.png']) {
    expect((await request.get(path)).status()).toBe(404);
  }
});
