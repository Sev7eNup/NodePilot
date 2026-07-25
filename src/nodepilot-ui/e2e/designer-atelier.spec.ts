import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Atelier design language — the designer's own skin-independent look (branch evaluation).
 *
 * `designStore.designerTheme` ('atelier' default | 'classic') puts `.wd-atelier` on the
 * `.np-designer` root and `.wd-atelier-on` on <html>; a `role="switch"` header button
 * (`toggle-atelier-theme`) flips it. The whole existing hermetic suite is pinned to classic
 * via installDefaultMocks — these specs are the only ones that exercise the Atelier path:
 * fresh-profile default, scope classes, canvas dot-grid, toggle round-trip, persistence.
 * SPA renders ENGLISH under Playwright; hermetic page.route mocks.
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

/** Seed the designer into the Atelier look (fresh profiles default to it, but the suite-wide
 *  classic pin from installDefaultMocks must be overridden AFTER it was installed). Like the
 *  pin, this only seeds while no full app-persisted state exists — so a mid-test toggle
 *  survives page.reload and persistence stays testable. */
async function seedAtelier(page: Page) {
  await page.addInitScript(() => {
    const raw = localStorage.getItem('nodepilot-design');
    let appWritten = false;
    try { appWritten = !!raw && JSON.parse(raw).state?.nodeStyle !== undefined; } catch { /* reseed */ }
    if (!appWritten) {
      localStorage.setItem('nodepilot-design', JSON.stringify({ state: { designerTheme: 'atelier' }, version: 1 }));
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

    // Canvas grid: free mode renders the unified dot grid (Premium + Classic share the
    // same dot branch since the crosshatch was retired).
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

    const readPalette = () => page.locator('.np-designer.wd-atelier').evaluate((el) => {
      const s = getComputedStyle(el);
      return { accent: s.getPropertyValue('--wd-accent').trim(), canvas: s.getPropertyValue('--wd-canvas').trim() };
    });

    // Default light skin: cobalt on a bright paper canvas (chrome sits a step darker).
    expect(await readPalette()).toEqual({ accent: '#3e63e8', canvas: '#faf8f3' });

    // Classic relationship in light skins: the floating chrome (header/sidebar/inspector,
    // painted with --wd-panel) is DARKER than the bright canvas ground, not lighter.
    const lightGround = await page.locator('.np-designer.wd-atelier').evaluate((el) => {
      const s = getComputedStyle(el);
      const lum = (hex: string) => {
        const n = hex.replace('#', '');
        return 0.299 * parseInt(n.slice(0, 2), 16) + 0.587 * parseInt(n.slice(2, 4), 16) + 0.114 * parseInt(n.slice(4, 6), 16);
      };
      return { canvasLum: lum(s.getPropertyValue('--wd-canvas').trim()), panelLum: lum(s.getPropertyValue('--wd-panel').trim()) };
    });
    expect(lightGround.canvasLum).toBeGreaterThan(lightGround.panelLum);

    // light-grey skin: lilac accent on a bright cream canvas.
    await page.evaluate(() => document.documentElement.setAttribute('data-skin', 'light-grey'));
    expect(await readPalette()).toEqual({ accent: '#7c3aed', canvas: '#faf7f2' });

    // dark-nebula skin: electric cyan on deep-space ground.
    await page.evaluate(() => {
      document.documentElement.classList.add('dark');
      document.documentElement.setAttribute('data-skin', 'dark-nebula');
    });
    expect(await readPalette()).toEqual({ accent: '#4de4f7', canvas: '#0d1322' });

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
    // installDefaultMocks pins classic for the whole hermetic suite — prove the pin works,
    // otherwise every visual assertion in the 60+ existing specs would silently run against
    // the Atelier tokens.
    await openEditor(page);
    await expect(page.locator('.np-designer')).toBeVisible();
    await expect(page.locator('.np-designer.wd-atelier')).toHaveCount(0);
    await expect(page.locator('pattern[id$="np-bg-dots"]')).toHaveCount(1);
  });
});
