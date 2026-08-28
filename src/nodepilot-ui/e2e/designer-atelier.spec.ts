import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Atelier design language: the designer's own skin-independent look.
 *
 * `designStore.designerTheme` ('atelier' default | 'classic') puts `.wd-atelier` on the
 * `.np-designer` root and `.wd-atelier-on` on <html>; a `role="switch"` header button
 * (`toggle-atelier-theme`) flips it. installDefaultMocks pins the rest of the hermetic suite to
 * classic, so these specs are the only ones covering the Atelier path: fresh-profile default,
 * scope classes, canvas dot grid, toggle round-trip and persistence.
 * The SPA renders English under Playwright; all APIs are page.route mocks.
 */

const WF_ID = 'a4e11e50-0000-4000-8000-a4e11e50a4e1';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'A', activityType: 'runScript', config: { script: 'x' } } },
      { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
        data: { label: 'B', activityType: 'log', config: { message: 'hi' } } },
    ],
    edges: [{ id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled', data: { label: '', condition: '', disabled: false } }],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Atelier', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
  });
}

/** Seed the designer into the Atelier look, overriding the suite-wide classic pin that
 *  installDefaultMocks applies. Like that pin, it only seeds while no full app-persisted state
 *  exists, so a mid-test toggle survives page.reload and persistence stays testable. */
async function seedAtelier(page: Page) {
  await page.addInitScript(() => {
    const raw = localStorage.getItem('nodepilot-design');
    let appWritten = false;
    try { appWritten = !!raw && JSON.parse(raw).state?.nodeStyle !== undefined; } catch { /* reseed */ }
    if (!appWritten) {
      // Repeats the node-scale pin from installDefaultMocks: this seed replaces the whole key,
      // so without it these specs would run at the `lg` default and drift away from the canvas
      // geometry the rest of the suite uses.
      localStorage.setItem('nodepilot-design', JSON.stringify({
        state: { designerTheme: 'atelier', nodeScaleIndex: 1 }, version: 2,
      }));
    }
  });
}

async function openEditor(page: Page) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('Atelier-Designsprache', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('atelier.1 — Atelier-Modus setzt Scope-Klassen und rendert das Punktraster', async ({ page }) => {
    await seedAtelier(page);
    await openEditor(page);

    // Scope classes: designer root + <html> portal marker.
    await expect(page.locator('.np-designer.wd-atelier')).toBeVisible();
    await expect(page.locator('html.wd-atelier-on')).toHaveCount(1);

    // Canvas grid: free mode renders the dot grid, which the Premium and Classic looks share.
    await expect(page.locator('pattern[id$="np-bg-dots"]')).toHaveCount(1);

    // Token proof: the editor header resolves the Atelier cobalt accent, not the base blue.
    const accent = await page.locator('.np-editor-header').evaluate((el) =>
      getComputedStyle(el).getPropertyValue('--wd-accent').trim(),
    );
    expect(accent).toBe('#3e63e8');
  });

  test('atelier.5 — Farb-Skins adaptieren den Atelier-Look (Akzent + Grundton je Skin)', async ({ page }) => {
    await seedAtelier(page);
    await openEditor(page);

    // Custom properties come back as authored rather than as a normalised colour, and the
    // production build shortens them: `#ffffff` reads as `#fff` off `vite preview` but not off
    // the dev server. Expand every value before comparing; the luminance maths also needs six
    // digits.
    const readTokens = () => page.locator('.np-designer.wd-atelier').evaluate((el) => {
      const s = getComputedStyle(el);
      const hex = (name: string) => {
        const v = s.getPropertyValue(name).trim();
        return /^#[0-9a-f]{3}$/i.test(v) ? `#${[...v.slice(1)].map((c) => c + c).join('')}` : v;
      };
      const lum = (v: string) => {
        const n = v.replace('#', '');
        return 0.299 * parseInt(n.slice(0, 2), 16) + 0.587 * parseInt(n.slice(2, 4), 16) + 0.114 * parseInt(n.slice(4, 6), 16);
      };
      const canvas = hex('--wd-canvas');
      const panel = hex('--wd-panel');
      return { accent: hex('--wd-accent'), canvas, panel, canvasLum: lum(canvas), panelLum: lum(panel) };
    });

    const readPalette = async () => {
      const { accent, canvas } = await readTokens();
      return { accent, canvas };
    };
    const readGround = readTokens;

    // Default light skin: cobalt accent on the shell's ground, `--color-surface-low`.
    expect(await readPalette()).toEqual({ accent: '#3e63e8', canvas: '#f3f4f6' });

    // In light skins the floating chrome (header, sidebar and inspector, painted with
    // --wd-panel) is a white plate on a grey ground, exactly like `.np-card` on
    // `bg-surface-low`.
    const lightGround = await readGround();
    expect(lightGround.panel).toBe('#ffffff');
    expect(lightGround.panelLum).toBeGreaterThan(lightGround.canvasLum);

    // light-grey skin: lilac accent on that skin's own ground.
    await page.evaluate(() => document.documentElement.setAttribute('data-skin', 'light-grey'));
    expect(await readPalette()).toEqual({ accent: '#7c3aed', canvas: '#f5f0eb' });
    expect((await readGround()).panel).toBe('#ffffff');

    // light-bank too: the rule holds for every light skin, not just the default one.
    await page.evaluate(() => document.documentElement.setAttribute('data-skin', 'light-bank'));
    expect(await readPalette()).toEqual({ accent: '#c80000', canvas: '#f8fafc' });
    const bankGround = await readGround();
    expect(bankGround.panel).toBe('#ffffff');
    expect(bankGround.panelLum).toBeGreaterThan(bankGround.canvasLum);

    // dark-nebula skin: electric cyan on deep-space ground.
    await page.evaluate(() => {
      document.documentElement.classList.add('dark');
      document.documentElement.setAttribute('data-skin', 'dark-nebula');
    });
    expect(await readPalette()).toEqual({ accent: '#4de4f7', canvas: '#0d1322' });

    // Dark skins keep the opposite relationship: the chrome lifts off a deeper canvas floor.
    // Asserting it here keeps the light rule above from spreading to the dark skins.
    const darkGround = await readGround();
    expect(darkGround.panelLum).toBeGreaterThan(darkGround.canvasLum);

    // Status colours stay skin-stable: success is identical across skins (dark family here).
    const success = await page.locator('.np-designer.wd-atelier').evaluate((el) =>
      getComputedStyle(el).getPropertyValue('--color-success').trim(),
    );
    expect(success).toBe('#4cc38a');
  });

  test('atelier.2 — Umschalter wechselt zu Classic und zurück (Switch, kein Checkbox)', async ({ page }) => {
    await seedAtelier(page);
    await openEditor(page);

    const toggle = page.getByTestId('toggle-atelier-theme');
    await expect(toggle).toHaveAttribute('role', 'switch');
    await expect(toggle).toHaveAttribute('aria-checked', 'true');

    await toggle.click();
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);
    await expect(page.locator('html.wd-atelier-on')).toHaveCount(0);
    await expect(toggle).toHaveAttribute('aria-checked', 'false');

    await toggle.click();
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(1);
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
  });

  test('atelier.3 — Wahl überlebt den Reload (persistiertes designStore)', async ({ page }) => {
    await seedAtelier(page);
    await openEditor(page);

    await page.getByTestId('toggle-atelier-theme').click();
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);

    await page.reload();
    await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);
    await expect(page.getByTestId('toggle-atelier-theme')).toHaveAttribute('aria-checked', 'false');
  });

  test('atelier.4 — Suite-Pin: ohne Atelier-Seed rendert der Editor klassisch', async ({ page }) => {
    // installDefaultMocks pins classic for the whole hermetic suite. If that pin broke, the
    // visual assertions in every other spec would silently run against the Atelier tokens.
    await openEditor(page);
    await expect(page.locator('.np-designer')).toBeVisible();
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);
    await expect(page.locator('pattern[id$="np-bg-dots"]')).toHaveCount(1);
  });

  test('atelier.6 — auch klassisch: helle Skins zeigen weisse Chrome auf dem Seitengrund', async ({ page }) => {
    // The ground/chrome relationship belongs to the light base, not to the Atelier look, so it
    // has to hold with the toggle off as well.
    await openEditor(page);
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);

    const read = () => page.evaluate(() => {
      const bg = (sel: string) => {
        const el = document.querySelector(sel);
        return el ? getComputedStyle(el).backgroundColor : 'missing';
      };
      return { canvas: bg('.np-canvas'), dock: bg('.wd-dock:not(.wd-dock--rail)'), inspector: bg('.np-anim-panel') };
    });

    for (const skin of ['light', 'light-grey', 'light-bank']) {
      await page.evaluate((s) => document.documentElement.setAttribute('data-skin', s), skin);
      const seen = await read();
      expect(seen.dock, `${skin} dock`).toBe('rgb(255, 255, 255)');
      if (seen.inspector !== 'missing') expect(seen.inspector, `${skin} inspector`).toBe('rgb(255, 255, 255)');
      expect(seen.canvas, `${skin} canvas must not be the plate colour`).not.toBe('rgb(255, 255, 255)');
    }

    // Dark keeps its own relationship; the rule is light-only by design.
    await page.evaluate(() => {
      document.documentElement.classList.add('dark');
      document.documentElement.setAttribute('data-skin', 'dark');
    });
    expect((await read()).dock).not.toBe('rgb(255, 255, 255)');
  });
});
