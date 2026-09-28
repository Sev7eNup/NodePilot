import { expect, test } from '@playwright/test';

const IDS = ['build', 'decision', 'parallel', 'service', 'live', 'versions', 'machine', 'maintenance'] as const;

for (const id of IDS) {
  test(`${id} opens directly in both languages`, async ({ page }) => {
    for (const lang of ['de', 'en']) {
      await page.goto(`./?tour=${id}&lang=${lang}`);
      await expect(page.locator('.np-tour:not([hidden])')).toHaveAttribute('data-tour', id);
      await expect(page.locator('.np-tour:not([hidden])')).not.toHaveAttribute('data-stage', 'reset');
    }
  });
}

async function runFromEditor(page: import('@playwright/test').Page, placeholder?: string, value?: string) {
  if (placeholder) await expect(page.locator('.react-flow__node').filter({ hasText: 'Manual Trigger' }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Run' }).click();
  const dialog = page.getByRole('dialog');
  if (placeholder && value) await dialog.getByPlaceholder(placeholder, { exact: true }).fill(value);
  if (await dialog.isVisible()) await dialog.getByRole('button', { name: 'Run', exact: true }).click();
}

test('decision executes only the critical branch at 8 GB', async ({ page }) => {
  await page.goto('./?tour=decision&lang=en');
  await runFromEditor(page, '8', '8');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done', { timeout: 20000 });
  await page.locator('[data-tour-action="result"]').click();
  await expect(page.locator('#root')).toContainText('Critical disk-space alert queued.');
});

test('parallel branches both finish before waitAll', async ({ page }) => {
  await page.goto('./?tour=parallel&lang=en');
  await runFromEditor(page);
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done', { timeout: 20000 });
  await page.locator('[data-tour-action="gantt"]').click();
  await expect(page.locator('.np-execution-panel')).toContainText('checksum');
  await expect(page.locator('.np-execution-panel')).toContainText('remove');
  await expect(page.getByTestId('gantt-chart')).toBeVisible();
  expect((await page.locator('.np-execution-panel').boundingBox())!.height).toBeGreaterThan(300);
  await expect(page.locator('[data-tour-action="next-task"]')).toHaveAttribute('href', /tour=service/);
});

test('service is started and verified before recovery mail', async ({ page }) => {
  await page.goto('./?tour=service&lang=en');
  await runFromEditor(page, 'Stopped', 'Stopped');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done', { timeout: 20000 });
});

test('live execution is cancelled from Live Ops', async ({ page }) => {
  await page.goto('./?tour=live&lang=en');
  await runFromEditor(page);
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'running');
  await page.locator('[data-tour-action="open-ops"]').click();
  await page.getByTitle(/Live Ops Check.*Running/i).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done');
});

test('version answer requires selecting the older version', async ({ page }) => {
  await page.goto('./?tour=versions&lang=en');
  await page.getByRole('button', { name: 'More designer actions' }).click();
  await page.getByRole('menuitem', { name: /diff against a previous version/i }).click();
  await page.locator('.np-anim-backdrop').getByText(/Version \d+/, { exact: true }).first().click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'answer');
  await page.locator('[data-tour-action="script"]').click();
  await expect(page.locator('.np-tour [role="status"]')).toBeVisible();
  await page.locator('[data-tour-action="return"]').click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done');
});

test('machine search and status answer complete the task', async ({ page }) => {
  await page.goto('./?tour=machine&lang=en');
  await page.locator('#root input[type="text"]').first().fill('LAB01');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'answer');
  await expect(page.locator('#root')).toContainText('Unknown');
  await expect(page.locator('[data-tour-action="none"]')).toHaveText('Unknown · 0 workflows');
  await page.locator('[data-tour-action="used"]').click();
  await expect(page.locator('.np-tour [role="status"]')).toBeVisible();
  await page.locator('[data-tour-action="none"]').click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done');
});

test('editing Patch night completes the maintenance task', async ({ page }) => {
  await page.goto('./?tour=maintenance&lang=en');
  const row = page.locator('tr').filter({ hasText: 'Patch night' });
  await row.getByTitle('Edit').click();
  await page.locator('input[type="time"]').first().fill('23:00');
  await page.getByRole('button', { name: 'Update', exact: true }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done');
});

test('build connects, publishes and runs the prepared workflow', async ({ page }) => {
  await page.goto('./?tour=build&lang=en');
  const source = (id: string) => page.locator(`.react-flow__node[data-id="${id}"] .react-flow__handle-right`).first();
  const target = (id: string) => page.locator(`.react-flow__node[data-id="${id}"] .react-flow__handle-left`).first();
  await source('trigger').dragTo(target('script'), { force: true });
  await source('script').dragTo(target('result'), { force: true });
  await expect(page.locator('.react-flow__edge')).toHaveCount(2);
  await page.getByRole('button', { name: 'Save in place (stays in edit lock)' }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'publish');
  await page.getByRole('button', { name: 'Publish', exact: true }).first().click();
  const checklist = page.getByRole('dialog');
  if (await checklist.isVisible()) await checklist.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'run');
  await expect(page.locator('.np-tour-progress')).toContainText('Step 3 / 4');
  // The demo world notifies the tour before the publish response/refetch reaches React.
  // Wait for the designer to acknowledge publication before clicking Run.
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible();
  await runFromEditor(page);
  await expect(page.locator('.np-tour-progress')).toContainText('Step 3 / 4');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'done', { timeout: 20000 });
  await expect(page.locator('.np-tour-progress')).toContainText('Task 1 / 10');
});

test('a task survives client navigation and starts fresh after reload', async ({ page }) => {
  await page.goto('./?tour=machine&lang=en');
  await page.locator('a[href="/demo/workflows"]').first().click();
  await expect(page.locator('[data-tour-action="resume"]')).toBeVisible();
  await page.locator('[data-tour-action="resume"]').click();
  await expect(page).toHaveURL(/\/demo\/machines\?tour=machine&lang=en/);
  await page.locator('#root input[type="text"]').first().fill('LAB01');
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'answer');
  await page.reload();
  await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'work');
});

for (const width of [390, 768, 1440]) {
  test(`guided panel fits ${width}px without horizontal overflow`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 });
    await page.goto('./?tour=machine&lang=de');
    await expect(page.locator('.np-tour')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('#root input[type="text"]').first().fill('LAB01');
    await expect(page.locator('.np-tour')).toHaveAttribute('data-stage', 'answer');
  });
}
