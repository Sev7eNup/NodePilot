import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, mockCaps, capsJson, MOCK_USER } from './fixtures/mockApi';
import { refitCanvas } from './fixtures/canvas';

const WF_ID = 'abababab-1515-4151-8151-abababababab';
const definition = {
  nodes: [
    { id: 'start', type: 'activity', position: { x: 40, y: 50 }, data: { label: 'Start', activityType: 'manualTrigger', config: {} } },
    { id: 'check', type: 'activity', position: { x: 40, y: 190 }, data: { label: 'Check service', activityType: 'serviceManagement', config: { serviceName: 'Spooler', action: 'status' } } },
    { id: 'result', type: 'activity', position: { x: 40, y: 330 }, data: { label: 'Result', activityType: 'returnData', config: {} } },
    { id: 'error', type: 'activity', position: { x: 230, y: 330 }, data: { label: 'Report failure', activityType: 'log', config: { message: 'Service unavailable' } } },
  ],
  edges: [
    { id: 'start-check', source: 'start', target: 'check', type: 'labeled', data: { label: '', condition: '', disabled: false } },
    { id: 'check-result', source: 'check', target: 'result', type: 'labeled', data: { label: 'Success', condition: '', disabled: false } },
    { id: 'check-error', source: 'check', target: 'error', type: 'labeled', data: { label: 'Failure', condition: 'failed', disabled: false } },
  ],
};

async function chooseDesignerSkin(page: Page, name: string) {
  await page.getByTestId('toggle-skin').click();
  await page.getByRole('menuitemradio', { name, exact: true }).click();
}

async function noOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
}

test.describe('ION dark production skin', () => {
  test.use({ viewport: { width: 1440, height: 1000 } });
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) => route.fulfill({ json: {
      id: WF_ID, name: 'Service health', description: '', isEnabled: false, version: 1,
      checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
      checkedOutAt: '2026-06-01T00:00:00.000Z', definitionJson: JSON.stringify(definition),
    } }));
  });

  test('selection persists and reaches shell, body portals and keyboard focus', async ({ page }, testInfo) => {
    await page.goto('/settings');
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    await appearance.getByRole('button', { name: 'ION Dark', exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-skin', 'dark-ion');
    await page.reload();
    await expect(page.getByRole('heading', { name: /appearance/i })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-skin', 'dark-ion');
    await expect(page.locator('html')).toHaveClass(/dark/);
    await expect(page.locator('.np-sidebar img[alt="NodePilot logo"]')).toHaveAttribute('src', '/appicon-dark-nebula.png');
    await page.goto('/workflows');
    await expect(page.locator('.np-folder-card')).toHaveCSS('background-image', /linear-gradient/);
    await expect(page.locator('.np-sidebar')).toHaveCSS('background-image', /linear-gradient/);
    await page.screenshot({ path: testInfo.outputPath('ion-workflows.png'), fullPage: true, animations: 'disabled' });
    await page.goto('/machines');
    const add = page.getByRole('button', { name: /add machine/i });
    await expect(add).toHaveCSS('color', 'rgb(5, 26, 39)');
    // Check every rendered gradient stop, not just its fallback background colour.
    const contrast = await add.evaluate((element) => {
      const cs = getComputedStyle(element);
      const luminance = (color: string) => color.match(/[\d.]+/g)!.slice(0, 3).map(Number).map((c) => {
        const v = c / 255;
        return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
      }).reduce((sum, c, i) => sum + c * [0.2126, 0.7152, 0.0722][i], 0);
      const ink = luminance(cs.color);
      return (cs.backgroundImage.match(/rgb\([^)]+\)/g) ?? [cs.backgroundColor]).map((color) => {
        const fill = luminance(color);
        return (Math.max(ink, fill) + 0.05) / (Math.min(ink, fill) + 0.05);
      });
    });
    expect(Math.min(...contrast)).toBeGreaterThanOrEqual(4.5);
    await add.click();
    const panel = page.locator('.np-modal-panel');
    await expect(panel).toBeVisible();
    await expect(panel).toHaveCSS('background-image', /linear-gradient/);
    await expect(panel).toHaveCSS('color', 'rgb(238, 246, 255)');
    await expect(panel).toHaveCSS('outline-color', 'rgb(103, 131, 158)');
    const name = panel.getByPlaceholder(/display name/i);
    await name.fill('ION demo');
    await name.press('Tab');
    const hostname = panel.getByPlaceholder(/hostname or ip/i);
    await expect(hostname).toBeFocused();
    await expect(hostname).toHaveCSS('outline-width', '2px');
    await expect(hostname).toHaveCSS('outline-color', 'rgb(101, 228, 255)');
    await hostname.fill('server.example.test');
    await panel.locator('select').selectOption({ index: 0 });
    await page.screenshot({ path: testInfo.outputPath('ion-dialog.png'), fullPage: true, animations: 'disabled' });
    for (const viewport of [{ width: 390, height: 844 }, { width: 720, height: 500 }]) {
      await page.setViewportSize(viewport);
      await noOverflow(page);
      await panel.getByRole('button', { name: /^cancel$/i }).scrollIntoViewIfNeeded();
      await expect(panel.getByRole('button', { name: /^cancel$/i })).toBeInViewport();
    }
    await panel.getByRole('button', { name: /^cancel$/i }).click();
    await expect(panel).toHaveCount(0);
  });

  test('designer switches preserve geometry, selection and preferences; mobile graph inherits ION', async ({ page }, testInfo) => {
    await page.addInitScript(() => localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark' }, version: 0 })));
    await page.goto(`/workflows/${WF_ID}`);
    const check = page.locator('.react-flow__node[data-id="check"]');
    await expect(check).toBeVisible();
    await check.click();
    await refitCanvas(page, 'panel');
    const geometry = () => check.evaluate((element) => {
      const cs = getComputedStyle(element);
      return { width: cs.width, height: cs.height, font: cs.font, transform: cs.transform };
    });
    const original = await geometry();
    const preferences = await page.evaluate(() => localStorage.getItem('nodepilot-design'));
    for (const [label, skin] of [['ION Dark', 'dark-ion'], ['Minimal Dark', 'dark-minimal'], ['Dark', 'dark'], ['ION Dark', 'dark-ion']]) {
      await chooseDesignerSkin(page, label);
      await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
      await expect(check).toHaveClass(/selected/);
      expect(await geometry()).toEqual(original);
      expect(await page.evaluate(() => localStorage.getItem('nodepilot-design'))).toBe(preferences);
    }
    await expect(page.locator('.np-editor-header')).toHaveCSS('background-image', /linear-gradient/);
    // Canvas and body portal styles must resolve the same accent despite Atelier shields.
    for (const selector of ['html', '.np-designer', '.react-flow']) {
      expect(await page.locator(selector).first().evaluate((el) => getComputedStyle(el).getPropertyValue('--color-primary').trim().toLowerCase())).toBe('#65e4ff');
    }
    const idle = page.locator('.react-flow__edge[data-id="start-check"] .react-flow__edge-path').first();
    await expect(idle).toHaveCSS('stroke', 'rgb(120, 149, 177)');
    await page.screenshot({ path: testInfo.outputPath('ion-designer.png'), fullPage: true, animations: 'disabled' });
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByText(/read-only view/i)).toBeVisible();
    await expect(page.locator('.np-mobile-canvas')).toBeVisible();
    await expect(page.locator('.react-flow__edge-path').first()).toBeAttached();
    await noOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('ion-mobile.png'), fullPage: true, animations: 'disabled' });
  });

  test('login controls remain legible and reduced motion suppresses decorative animation', async ({ page }, testInfo) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.addInitScript(() => localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark-ion' }, version: 0 })));
    await page.route('**/api/auth/me', (route) => route.fulfill({ status: 401, json: {} }));
    await page.route('**/api/auth/methods', (route) => route.fulfill({ json: { local: true, ldap: false, windows: false, oidc: false } }));
    await page.goto('/login');
    await expect(page.locator('.np-login-card')).toHaveCSS('background-image', /linear-gradient/);
    expect(await page.locator('.np-login-aurora').evaluate((el) => getComputedStyle(el, '::before').animationName)).toBe('none');
    await expect(page.locator('button[type="submit"]')).toHaveCSS('color', 'rgb(5, 26, 39)');
    await page.locator('#np-login-username').fill('demo');
    await page.locator('#np-login-username').press('Tab');
    await expect(page.locator('#np-login-password')).toBeFocused();
    await expect(page.locator('#np-login-password')).toHaveCSS('outline-width', '2px');
    await page.setViewportSize({ width: 390, height: 844 });
    await noOverflow(page);
    await page.screenshot({ path: testInfo.outputPath('ion-login.png'), fullPage: true, animations: 'disabled' });
  });

  test('assistant inherits the skin and keeps its mobile sheet and keyboard focus', async ({ page }, testInfo) => {
    await mockCaps(page, capsJson({ db: false, sourceCode: false }));
    await page.addInitScript(() => localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark-ion' }, version: 0 })));
    await page.goto('/workflows');
    const launcher = page.getByRole('button', { name: 'Open NodePilot Assistant', exact: true });
    await launcher.click();
    const panel = page.getByRole('dialog', { name: 'NodePilot Assistant' });
    await expect(panel).toHaveCSS('background-image', /linear-gradient/);
    await expect(panel).toHaveCSS('color', 'rgb(238, 246, 255)');
    await panel.getByRole('textbox').fill('Local draft');
    await page.screenshot({ path: testInfo.outputPath('ion-assistant.png'), fullPage: true, animations: 'disabled' });
    await page.setViewportSize({ width: 390, height: 430 });
    await expect(panel).toHaveCSS('border-radius', '0px');
    await expect(panel.getByRole('textbox')).toHaveValue('Local draft');
    const input = await panel.getByRole('textbox').boundingBox();
    expect(input!.y + input!.height).toBeLessThanOrEqual(430);
    await noOverflow(page);
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(launcher).toBeFocused();
  });
});
