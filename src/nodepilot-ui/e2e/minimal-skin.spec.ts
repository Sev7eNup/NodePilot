import { test, expect, type Locator, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';
import { refitCanvas } from './fixtures/canvas';

// Production CSS, real theme controls and real body portals; all API data stays mocked.
const WF_ID = 'abababab-1414-4141-8141-abababababab';
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

async function chooseSettingsSkin(page: Page, label: string) {
  await page.goto('/settings');
  const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
  await appearance.getByRole('button', { name: label, exact: true }).click();
}

async function chooseDesignerSkin(page: Page, label: string) {
  await page.getByTestId('toggle-skin').click();
  await page.getByRole('menuitemradio', { name: label, exact: true }).click();
}

async function noOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
}

// Use computed colours, so minified hex aliases and actual selector specificity are exercised.
async function textContrast(locator: Locator) {
  return locator.evaluate((element) => {
    const rgb = (value: string) => value.match(/[\d.]+/g)!.slice(0, 3).map(Number);
    const luminance = (value: string) => rgb(value).map((channel) => {
      const v = channel / 255;
      return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
    }).reduce((total, channel, i) => total + channel * [0.2126, 0.7152, 0.0722][i], 0);
    let ancestor: Element | null = element;
    let background = 'rgba(0, 0, 0, 0)';
    while (ancestor && background === 'rgba(0, 0, 0, 0)') {
      background = getComputedStyle(ancestor).backgroundColor;
      ancestor = ancestor.parentElement;
    }
    const [low, high] = [luminance(getComputedStyle(element).color), luminance(background)].sort((a, b) => a - b);
    return (high + 0.05) / (low + 0.05);
  });
}

test.describe('Minimal skins across the application', () => {
  test.use({ viewport: { width: 1440, height: 1000 } });
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) => route.fulfill({ json: {
      id: WF_ID, name: 'Service health', description: '', isEnabled: false, version: 1,
      checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
      checkedOutAt: '2026-06-01T00:00:00.000Z', definitionJson: JSON.stringify(definition),
    } }));
  });

  for (const base of ['light', 'dark'] as const) {
    const skin = `${base}-minimal`;
    const label = base === 'light' ? 'Minimal Light' : 'Minimal Dark';

    test(`${skin}: persists and reaches shell, form portals and keyboard focus`, async ({ page }, testInfo) => {
      await chooseSettingsSkin(page, label);
      await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
      await page.reload();
      await expect(page.getByRole('heading', { name: /appearance/i })).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
      expect(await page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(base === 'dark');
      await page.goto('/workflows');
      await expect(page.locator('.np-folder-card')).toHaveCSS('background-image', 'none');
      await expect(page.locator('.np-folder-card')).toHaveCSS('box-shadow', 'none');
      const aiCreate = page.getByRole('button', { name: /new ai workflow/i });
      await expect(aiCreate).toBeVisible();
      expect(await textContrast(aiCreate)).toBeGreaterThanOrEqual(4.5);
      await page.goto('/machines');
      const add = page.getByRole('button', { name: /add machine/i });
      await expect(add).toBeVisible();
      await expect(page.locator('.np-sidebar')).toHaveCSS('background-image', 'none');
      expect(await textContrast(add)).toBeGreaterThanOrEqual(4.5);
      await add.click();
      const panel = page.locator('.np-modal-panel');
      await expect(panel).toBeVisible();
      await expect(panel).toHaveCSS('background-image', 'none');
      await expect(panel).toHaveCSS('box-shadow', 'none');
      await expect(panel).toHaveCSS('outline-width', '1px');
      expect(await textContrast(panel.getByRole('heading'))).toBeGreaterThanOrEqual(4.5);
      const name = panel.getByPlaceholder(/display name/i);
      await name.fill('Preview server');
      await name.press('Tab');
      const hostname = panel.getByPlaceholder(/hostname or ip/i);
      await expect(hostname).toBeFocused();
      await expect(hostname).toHaveCSS('outline-style', 'solid');
      await expect(hostname).toHaveCSS('outline-width', '2px');
      await hostname.fill('server.example.test');
      await panel.locator('select').selectOption({ index: 0 });
      await page.screenshot({ path: testInfo.outputPath(`${skin}-dialog.png`), fullPage: true });
      // Both phone width and the CSS viewport of a 1440x1000 display at 200% zoom.
      for (const viewport of [{ width: 390, height: 844 }, { width: 720, height: 500 }]) {
        await page.setViewportSize(viewport);
        await noOverflow(page);
        await panel.getByRole('button', { name: /^cancel$/i }).scrollIntoViewIfNeeded();
        await expect(panel.getByRole('button', { name: /^cancel$/i })).toBeInViewport();
      }
      await panel.getByRole('button', { name: /^cancel$/i }).click();
      await expect(panel).toHaveCount(0);
    });

    test(`${skin}: designer selection and geometry survive real skin switches`, async ({ page }, testInfo) => {
      await chooseSettingsSkin(page, base === 'light' ? 'Light' : 'Dark');
      await page.goto(`/workflows/${WF_ID}`);
      const check = page.locator('.react-flow__node[data-id="check"]');
      await expect(check).toBeVisible();
      await check.click();
      await refitCanvas(page, 'panel');
      const geometry = () => check.evaluate((element) => {
        const style = getComputedStyle(element);
        return { width: style.width, height: style.height, font: style.font, transform: style.transform };
      });
      const originalGeometry = await geometry();
      const preferences = await page.evaluate(() => localStorage.getItem('nodepilot-design'));
      await chooseDesignerSkin(page, label);
      await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
      await expect(check).toHaveClass(/selected/);
      expect(await geometry()).toEqual(originalGeometry);
      expect(await page.evaluate(() => localStorage.getItem('nodepilot-design'))).toBe(preferences);
      await expect(page.locator('.np-editor-header')).toHaveCSS('background-image', 'none');
      await expect(page.locator('.np-editor-header')).toHaveCSS('box-shadow', 'none');
      await expect(page.locator('.np-glow-bloom:visible')).toHaveCount(0);
      const minimap = page.locator('.react-flow__minimap');
      await expect(minimap).toHaveCSS('box-shadow', 'none');
      await expect(minimap).toHaveCSS('backdrop-filter', 'none');
      await expect(minimap).toHaveCSS('border-radius', '6px');
      const idle = page.locator('.react-flow__edge[data-id="start-check"] .react-flow__edge-path').first();
      await expect(idle).toBeAttached();
      const edgeColors = await idle.evaluate((element) => {
        const probe = document.createElement('span');
        const canvas = document.querySelector('.react-flow')!;
        canvas.appendChild(probe);
        probe.style.color = 'var(--np-edge-idle)';
        const expected = getComputedStyle(probe).color;
        probe.remove();
        return { stroke: getComputedStyle(element).stroke, expected };
      });
      expect(edgeColors.stroke).toBe(edgeColors.expected);
      await page.screenshot({ path: testInfo.outputPath(`${skin}-designer.png`), fullPage: true });
      await chooseDesignerSkin(page, base === 'light' ? 'Light' : 'Dark');
      expect(await geometry()).toEqual(originalGeometry);
      await chooseDesignerSkin(page, label);
      expect(await geometry()).toEqual(originalGeometry);
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(page.getByText(/read-only view/i)).toBeVisible();
      await expect(page.locator('.np-mobile-canvas')).toBeVisible();
      await expect(page.locator('.react-flow__edge-path').first()).toBeAttached();
      await noOverflow(page);
      await page.screenshot({ path: testInfo.outputPath(`${skin}-mobile.png`), fullPage: true });
    });

    test(`${skin}: login is flat and legible with local form controls`, async ({ page }, testInfo) => {
      await page.addInitScript((value) => localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: value }, version: 0 })), skin);
      await page.route('**/api/auth/me', (route) => route.fulfill({ status: 401, json: {} }));
      await page.route('**/api/auth/methods', (route) => route.fulfill({ json: { local: true, ldap: false, windows: false, oidc: false } }));
      await page.goto('/login');
      const card = page.locator('.np-login-card');
      await expect(card).toBeVisible();
      await expect(page.locator('.np-login')).toHaveCSS('background-image', 'none');
      await expect(card).toHaveCSS('box-shadow', 'none');
      await expect(card).toHaveCSS('background-image', 'none');
      await expect(page.locator('.np-login-aurora')).toBeHidden();
      const submit = page.locator('button[type="submit"]');
      await expect(submit).toHaveCSS('background-image', 'none');
      expect(await textContrast(submit)).toBeGreaterThanOrEqual(4.5);
      await page.locator('#np-login-username').fill('demo');
      await page.locator('#np-login-username').press('Tab');
      await expect(page.locator('#np-login-password')).toBeFocused();
      await expect(page.locator('#np-login-password')).toHaveCSS('outline-width', '2px');
      await page.setViewportSize({ width: 390, height: 844 });
      await noOverflow(page);
      await page.screenshot({ path: testInfo.outputPath(`${skin}-login.png`), fullPage: true });
    });
  }
});
