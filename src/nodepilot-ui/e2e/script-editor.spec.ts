import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * The Monaco script editor, opened against the PRODUCTION bundle.
 *
 * This suite serves `npm run build && vite preview`, so its CSS is minified — and Lightning CSS
 * shortens colors inside custom properties (`#ffffff` ships as `#fff`). Monaco accepts only 6-
 * or 8-digit hex for token colors and throws on anything else, which took the whole designer
 * page down through the error boundary. The dev server does not minify, so no dev-mode spec and
 * no unit test can reproduce it; only this configuration can.
 *
 * `dark-bank` is the skin that triggers it: its `--color-on-surface` is `#ffffff`, which becomes
 * the editor's foreground.
 */

const WF_ID = 'f7f7f7f7-7777-7777-7777-777777777777';

const DEFINITION = JSON.stringify({
  nodes: [{
    id: 'step-script',
    type: 'activity',
    position: { x: 60, y: 60 },
    data: { label: 'Probe', activityType: 'runScript', config: { script: 'Get-Date' } },
  }],
  edges: [],
});

function workflowJson() {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-ScriptEditor',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: DEFINITION,
    version: 1,
  });
}

async function applySkin(page: Page, skin: string, dark: boolean) {
  await page.evaluate(({ skin: s, dark: d }) => {
    document.documentElement.classList.toggle('dark', d);
    document.documentElement.setAttribute('data-skin', s);
  }, { skin, dark });
}

/** Opens the script editor of the seeded runScript node and asserts Monaco actually rendered. */
async function openScriptEditor(page: Page) {
  await page.locator('.react-flow__node[data-id="step-script"]').click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(/run script/i).first()).toBeVisible({ timeout: 10_000 });
  await page.getByRole('button', { name: 'Open Editor' }).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  // .monaco-editor only exists once the editor has been constructed with a theme.
  await expect(page.locator('.monaco-editor').first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/Editor konnte nicht geladen werden|Editor failed to load/i)).toHaveCount(0);
}

test.describe('Script editor against the minified bundle', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }));
  });

  for (const { skin, dark } of [{ skin: 'dark-bank', dark: true }, { skin: 'dark', dark: true }]) {
    test(`opens under the ${skin} skin without an uncaught error`, async ({ page }) => {
      // Subscribed before navigating: the theme is applied while the dialog mounts, so a throw
      // would land here and nowhere else. A console listener alone would not see it.
      const pageErrors: string[] = [];
      page.on('pageerror', (err) => pageErrors.push(err.message));
      // The dialog catches a rejected theme and falls back to Monaco's built-in one, so a
      // regression would no longer throw — it would only log this. Assert on both.
      const themeWarnings: string[] = [];
      page.on('console', (msg) => {
        if (msg.text().includes('skin colors rejected by Monaco')) themeWarnings.push(msg.text());
      });

      await page.goto(`/workflows/${WF_ID}`);
      await expect(page.locator('.react-flow__node[data-id="step-script"]')).toBeVisible({ timeout: 15_000 });
      await applySkin(page, skin, dark);

      await openScriptEditor(page);

      expect(pageErrors, `uncaught errors under ${skin}`).toEqual([]);
      expect(themeWarnings, `theme rejected under ${skin}`).toEqual([]);
    });
  }
});
