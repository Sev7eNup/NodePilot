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
    data: { label: 'Probe', activityType: 'runScript', config: { script: "# Minimal comment\n$probe = 'hello'\nif ($probe) { 0x2a; 7.5 }\nGet-Date" } },
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
    document.documentElement.classList.toggle('np-accent-remap', s !== 'light');
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

async function editorPalette(page: Page) {
  return page.evaluate(() => {
    const scope = document.querySelector('.np-designer')!;
    const probe = document.createElement('span');
    scope.appendChild(probe);
    probe.style.color = 'var(--np-code-comment)';
    const comment = getComputedStyle(probe).color;
    probe.style.color = 'var(--color-surface-lowest)';
    const surface = getComputedStyle(probe).color;
    const syntax = Object.fromEntries(['keyword', 'variable', 'string', 'number'].map((token) => {
      probe.style.color = `var(--np-code-${token})`;
      return [token, getComputedStyle(probe).color];
    }));
    probe.remove();
    return { comment, surface, syntax };
  });
}

async function expectMonacoSyntax(page: Page, syntax: Record<string, string>) {
  for (const [text, token] of [['if', 'keyword'], ['$probe', 'variable'], ["'hello'", 'string'], ['0x2a', 'number'], ['7.5', 'number']]) {
    await expect(page.locator('.monaco-editor .view-line span').getByText(text, { exact: true }).last()).toHaveCSS('color', syntax[token]);
  }
}

test.describe('Script editor against the minified bundle', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }));
  });

  for (const { skin, dark } of [
    { skin: 'dark-bank', dark: true }, { skin: 'dark', dark: true },
    { skin: 'light-minimal', dark: false }, { skin: 'dark-minimal', dark: true },
    { skin: 'dark-ion', dark: true },
  ]) {
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

  for (const base of ['light', 'dark'] as const) {
    test(`refreshes open Monaco when ${base} changes to Minimal and back`, async ({ page }) => {
      const errors: string[] = [];
      page.on('pageerror', (error) => errors.push(error.message));
      page.on('console', (message) => {
        if (message.text().includes('skin colors rejected by Monaco')) errors.push(message.text());
      });
      await page.goto(`/workflows/${WF_ID}`);
      await expect(page.locator('.react-flow__node[data-id="step-script"]')).toBeVisible();
      await applySkin(page, base, base === 'dark');
      await openScriptEditor(page);
      const comment = page.locator('.monaco-editor .view-line span').filter({ hasText: /#.*Minimal.*comment/ }).last();
      // Wait for lazy PowerShell tokenization before recording the original palette.
      await expect(comment).toHaveCSS('color', base === 'light' ? 'rgb(0, 128, 0)' : 'rgb(96, 139, 78)');
      const originalComment = await comment.evaluate((element) => getComputedStyle(element).color);
      const editor = page.locator('.monaco-editor').first();
      const originalSurface = await editor.evaluate((element) => getComputedStyle(element).backgroundColor);

      await applySkin(page, `${base}-minimal`, base === 'dark');
      const expected = await editorPalette(page);
      await expect(comment).toHaveCSS('color', expected.comment);
      await expect(editor).toHaveCSS('background-color', expected.surface);
      await expectMonacoSyntax(page, expected.syntax);
      expect(expected.comment).not.toBe(originalComment);
      await expect(page.locator('.monaco-editor .view-lines')).toContainText('Get-Date');

      await applySkin(page, base, base === 'dark');
      await expect(comment).toHaveCSS('color', originalComment);
      await expect(editor).toHaveCSS('background-color', originalSurface);
      expect(errors).toEqual([]);
    });
  }

  test('keeps Monaco and CodeMirror in place across Dark, ION and Minimal switches', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(error.message));
    page.on('console', (message) => {
      if (message.text().includes('skin colors rejected by Monaco')) errors.push(message.text());
    });
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node[data-id="step-script"]')).toBeVisible();
    await applySkin(page, 'dark', true);
    await openScriptEditor(page);

    const monaco = page.locator('.monaco-editor').first();
    const monacoComment = page.locator('.monaco-editor .view-line span').filter({ hasText: /#.*Minimal.*comment/ }).last();
    await expect(monacoComment).toHaveCSS('color', 'rgb(96, 139, 78)');
    const originalMonacoSurface = await monaco.evaluate((element) => getComputedStyle(element).backgroundColor);
    const codeMirror = page.locator('.cm-editor').first();
    const codeMirrorComment = codeMirror.locator('.cm-line span').filter({ hasText: /#.*Minimal.*comment/ }).last();
    const codeMirrorHandle = await codeMirror.elementHandle();
    expect(codeMirrorHandle).not.toBeNull();

    for (const skin of ['dark-ion', 'dark-minimal', 'dark-ion', 'dark']) {
      await applySkin(page, skin, true);
      const expected = await editorPalette(page);
      await expect(codeMirror).toHaveCSS('background-color', expected.surface);
      await expect(codeMirrorComment).toHaveCSS('color', expected.comment);
      if (skin === 'dark') {
        await expect(monacoComment).toHaveCSS('color', 'rgb(96, 139, 78)');
        await expect(monaco).toHaveCSS('background-color', originalMonacoSurface);
      } else {
        await expect(monacoComment).toHaveCSS('color', expected.comment);
        await expect(monaco).toHaveCSS('background-color', expected.surface);
        await expectMonacoSyntax(page, expected.syntax);
      }
      expect(await codeMirrorHandle!.evaluate((element) => element.isConnected)).toBe(true);
      await expect(codeMirror.locator('.cm-content')).toContainText('Get-Date');
      await expect(monaco.locator('.view-lines')).toContainText('Get-Date');
    }
    expect(errors).toEqual([]);
  });
});
