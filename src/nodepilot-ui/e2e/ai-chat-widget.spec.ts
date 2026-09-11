import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, mockCaps, capsJson, MOCK_USER } from './fixtures/mockApi';

const reply = (text: string) =>
  `event: delta\ndata: ${JSON.stringify({ text })}\n\nevent: done\ndata: ${JSON.stringify({ model: 'NodePilot test', durationMs: 800, completionTokens: 30 })}\n\n`;

async function openWidget(page: Page) {
  await page.getByRole('button', { name: 'Open NodePilot Assistant', exact: true }).click();
  const panel = page.getByRole('dialog', { name: 'NodePilot Assistant' });
  await expect(panel.getByRole('textbox')).toBeEnabled();
  return panel;
}

test.beforeEach(async ({ page }) => {
  await installDefaultMocks(page);
  await mockCaps(page, capsJson({ db: false, sourceCode: false }));
});

test('keeps the request and draft while navigating, minimizing and opening the full chat', async ({ page }) => {
  let release!: () => void;
  const waiting = new Promise<void>((resolve) => { release = resolve; });
  let requests = 0;
  await page.route('**/api/ai/knowledge/ask', async (route) => {
    requests++;
    await waiting;
    await route.fulfill({ contentType: 'text/event-stream', body: reply('Your workflow answer.') });
  });
  await page.goto('/');
  const panel = await openWidget(page);
  await panel.getByRole('textbox').fill('Explain workflows');
  await panel.getByRole('textbox').press('Enter');
  await expect(panel.getByRole('button', { name: /^Stop$/i })).toBeVisible();
  await panel.getByRole('textbox').fill('My follow-up');
  await panel.getByRole('button', { name: 'Minimize assistant' }).click();
  await page.locator('aside').getByRole('link', { name: /Workflows/i }).click();
  await expect(page).toHaveURL(/\/workflows$/);
  await openWidget(page);
  await expect(panel.getByRole('textbox')).toHaveValue('My follow-up');
  await panel.getByRole('button', { name: 'Open full chat' }).click();
  await expect(page).toHaveURL(/\/ai-chat$/);
  await expect(panel).toHaveCount(0);
  await expect(page.getByRole('textbox')).toHaveValue('My follow-up');
  release();
  await expect(page.getByText('Your workflow answer.')).toBeVisible();
  expect(requests).toBe(1);
  await page.reload();
  await expect(page.getByText('Your workflow answer.')).toBeVisible();
});

test('announces a completed reply when minimized and retains the thread on reopen', async ({ page }) => {
  let release!: () => void;
  const waiting = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/api/ai/knowledge/ask', async (route) => {
    await waiting;
    await route.fulfill({ contentType: 'text/event-stream', body: reply('Ready to read.') });
  });
  await page.goto('/workflows');
  const panel = await openWidget(page);
  await panel.getByRole('textbox').fill('Explain triggers');
  await panel.getByRole('textbox').press('Enter');
  await expect(panel.getByRole('button', { name: /^Stop$/i })).toBeVisible();
  await panel.getByRole('button', { name: 'Minimize assistant' }).click();
  release();
  await page.getByRole('button', { name: 'Open NodePilot Assistant — new reply' }).click();
  await expect(panel.getByText('Ready to read.')).toBeVisible();
});

test('keeps an error in the conversation and retries the same question', async ({ page }) => {
  const questions: string[] = [];
  await page.route('**/api/ai/knowledge/ask', (route) => {
    questions.push(route.request().postDataJSON().question);
    return questions.length === 1
      ? route.fulfill({ status: 503, json: { code: 'LLM_UNAVAILABLE', message: 'Temporarily unavailable' } })
      : route.fulfill({ contentType: 'text/event-stream', body: reply('Recovered answer.') });
  });
  await page.goto('/workflows');
  const panel = await openWidget(page);
  await panel.getByRole('textbox').fill('Help with triggers');
  await panel.getByRole('textbox').press('Enter');
  await expect(panel.getByRole('alert')).toBeVisible();
  await panel.getByRole('button', { name: /Try again/i }).click();
  await expect(panel.getByText('Recovered answer.')).toBeVisible();
  expect(questions).toEqual(['Help with triggers', 'Help with triggers']);
});

test('fits the mobile viewport, traps focus and restores the launcher focus', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/workflows');
  const panel = await openWidget(page);
  await expect(panel).toHaveAttribute('aria-modal', 'true');
  await expect(page.locator('#np-app-content')).toHaveAttribute('inert', '');
  await panel.screenshot({ path: testInfo.outputPath('widget-mobile.png') });
  await page.setViewportSize({ width: 390, height: 430 });
  await panel.getByRole('textbox').fill('Question with keyboard open');
  const input = await panel.getByRole('textbox').boundingBox();
  expect(input!.y + input!.height).toBeLessThanOrEqual(430);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await panel.getByRole('button', { name: /^Send$/i }).focus();
  await page.keyboard.press('Tab');
  await expect(panel.getByRole('button', { name: 'Open full chat' })).toBeFocused();
  await panel.screenshot({ path: testInfo.outputPath('widget-mobile-keyboard-height.png') });
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(page.locator('#np-app-content')).not.toHaveAttribute('inert', '');
  await expect(page.getByRole('button', { name: 'Open NodePilot Assistant', exact: true })).toBeFocused();
});

test('adapts to every skin and contains long Markdown without horizontal page overflow', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.route('**/api/ai/knowledge/ask', (route) =>
    route.fulfill({ contentType: 'text/event-stream', body: reply('A **workflow** connects activities.\n\n1. Choose a trigger.\n2. Add your activities.\n3. Publish when ready.\n\n```powershell\nGet-Service -Name Spooler | Select-Object Name, Status, DisplayName\n```') }));
  await page.goto('/workflows');
  for (const skin of ['light', 'light-grey', 'light-bank', 'dark', 'dark-lila', 'dark-bank', 'dark-nebula']) {
    await page.evaluate((theme) => {
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme }, version: 0 }));
    }, skin);
    await page.reload();
    const panel = await openWidget(page);
    if (skin === 'light') {
      await panel.screenshot({ path: testInfo.outputPath('widget-empty.png') });
      await panel.getByRole('textbox').fill('How do I build a workflow?');
      await panel.getByRole('textbox').press('Enter');
    }
    await expect(panel.getByText('Choose a trigger.')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
    expect(await panel.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`widget-${skin}.png`) });
  }
});

test('coordinates with the designer assistant without two open panels', async ({ page }) => {
  const id = 'a1a1a1a1-b2b2-c3c3-d4d4-e5e5e5e5e5e5';
  await page.route(`**/api/workflows/${id}`, (route) => route.fulfill({ json: {
    id, name: 'Widget test', description: '', isEnabled: false, version: 1,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    definitionJson: JSON.stringify({ nodes: [], edges: [] }),
  } }));
  await page.goto(`/workflows/${id}`);
  const panel = await openWidget(page);
  const designerToggle = page.getByRole('button', { name: /AI assistant/i, exact: true });
  await designerToggle.click();
  await expect(panel).toHaveCount(0);
  await expect(designerToggle).toHaveAttribute('aria-pressed', 'true');
  await openWidget(page);
  await expect(designerToggle).toHaveAttribute('aria-pressed', 'false');
});
