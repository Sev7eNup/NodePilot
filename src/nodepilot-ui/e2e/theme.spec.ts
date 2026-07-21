import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Part 17 — Theme & UX Features.
 *
 * 17.1 Dark/Light/System theme toggle  → Settings page, themeStore persists to
 *      localStorage ('nodepilot.theme') and toggles `document.documentElement.classList.dark`.
 * 17.2 Mini-Map                          → React-Flow <MiniMap> in the editor (visible; the
 *      Controls fit-view button as the "show/hide / navigate" affordance). Pan/click-drag on
 *      the minimap relies on RF internal pointer handling that synthetic Playwright events do
 *      not drive reliably → those interaction checks are skipped, presence is asserted.
 * 17.3 Node Context-Menu                 → right-click a node (a `contextmenu` event, NOT a
 *      drag) opens NodeContextMenu with Duplicate / Enable-Disable / Breakpoint / Delete;
 *      closes on Escape and on outside-click.
 * 17.4 Drag-Drop from Activity-Sidebar   → the sidebar lists categorized activities; the
 *      actual drag-onto-canvas uses HTML5 DnD on the RF pane which synthetic events can't
 *      drive, so the drop is exercised via the equivalent click-to-add path and the drag
 *      check is skipped.
 *
 * Hermetic: page.route() mocks only (no backend). SPA renders EN under Playwright.
 */

const WF_ID = 'e7e7e7e7-1717-1717-1717-171717171717';
const NODE_ID = 'step-aaaa1111';

function workflowWithNode() {
  return {
    id: WF_ID,
    name: 'Theme_E2E_WF',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked-by-me → canWrite (Admin + own lock)
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify({
      nodes: [
        {
          id: NODE_ID,
          type: 'activity',
          position: { x: 260, y: 180 },
          data: { label: 'Check Disk', activityType: 'runScript', targetMachineId: null, credentialId: null, config: { script: 'Get-PSDrive C' } },
        },
      ],
      edges: [],
    }),
    version: 1,
    activityCount: 1,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
  };
}

async function openEditor(page: Page) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowWithNode()) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  // Wait until the activity node has rendered on the React-Flow canvas.
  await expect(page.locator(`.react-flow__node[data-id="${NODE_ID}"]`)).toBeVisible({ timeout: 20_000 });
}

test.describe('Theme & UX-Features (Teil 17)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('17.1 — Dark/Light/System toggle flips html.dark instantly and persists across reload', async ({ page }) => {
    await page.goto('/settings');

    // Scope to the Settings "Appearance" card. The left sidebar also has theme controls
    // (icon-only) and there is a "System" settings tab elsewhere — both collide otherwise.
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    const dark = appearance.getByRole('button', { name: /^dark$/i });
    const light = appearance.getByRole('button', { name: /^light$/i });
    const system = appearance.getByRole('button', { name: /^system$/i });
    await expect(dark).toBeVisible({ timeout: 15_000 });
    await expect(light).toBeVisible();
    await expect(system).toBeVisible();

    // Choose Dark → <html> gains the `dark` class immediately (no reload).
    await dark.click();
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true);
    // Persisted to localStorage under the themeStore key.
    await expect.poll(() => page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"dark"');

    // Choose Light → class removed.
    await light.click();
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(false);
    await expect.poll(() => page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"light"');

    // Back to Dark, then reload → preference survives (applied before first paint).
    await dark.click();
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true);
    await page.reload();
    await expect(page.getByRole('heading', { name: /appearance/i })).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true);
    expect(await page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"dark"');
  });

  test('17.8 — language toggle switches the UI between English and German', async ({ page }) => {
    await page.goto('/settings');

    // The Appearance card holds both the theme and the language buttons. Its heading itself
    // localizes (Appearance ↔ Darstellung), which is the cleanest signal that i18n flipped.
    const appearance = page.getByRole('heading', { name: /appearance|darstellung/i }).locator('..');
    await expect(appearance).toBeVisible({ timeout: 15_000 });
    const de = appearance.getByRole('button', { name: /^deutsch$/i });
    const en = appearance.getByRole('button', { name: /^english$/i });
    await expect(de).toBeVisible();
    await expect(en).toBeVisible();

    // Switch to German → the card heading becomes "Darstellung".
    await de.click();
    await expect(page.getByRole('heading', { name: /^darstellung$/i })).toBeVisible();

    // Switch back to English → "Appearance" again.
    await en.click();
    await expect(page.getByRole('heading', { name: /^appearance$/i })).toBeVisible();
  });

  test('17.2 — Mini-Map is present in the editor (with fit-view control)', async ({ page }) => {
    await openEditor(page);

    // React-Flow renders the MiniMap as an <svg class="react-flow__minimap">.
    await expect(page.locator('.react-flow__minimap')).toBeVisible({ timeout: 15_000 });
    // It draws a node thumbnail for the single activity node.
    await expect(page.locator('.react-flow__minimap-node')).toHaveCount(1);
    // The Controls cluster (zoom in/out + fit view) is the show/navigate affordance.
    await expect(page.locator('.react-flow__controls')).toBeVisible();

    // Click-to-navigate / drag-to-pan on the minimap relies on RF internal pointer handling
    // that synthetic Playwright events don't drive reliably.
    test.info().annotations.push({ type: 'skip-note', description: 'minimap click/drag-to-pan needs real pointer events' });
  });

  test('17.3 — right-click a node opens the context menu; Escape closes it', async ({ page }) => {
    // The breakpoint menu item is expert-only; seed expert mode so the full menu renders.
    await seedExpertMode(page);
    await openEditor(page);

    const node = page.locator(`.react-flow__node[data-id="${NODE_ID}"]`);
    await node.click({ button: 'right' });

    // Menu items (NodeContextMenu renders fixed English labels).
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button', { name: /disable step|enable step/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /add breakpoint|remove breakpoint/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^delete$/i })).toBeVisible();

    // Escape closes it.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toHaveCount(0);
  });

  test('17.3b — context-menu action executes (Disable step); outside-click closes the menu', async ({ page }) => {
    await openEditor(page);

    const node = page.locator(`.react-flow__node[data-id="${NODE_ID}"]`);

    // Outside-click close: open then click empty canvas. Click near the TOP of the pane —
    // the editor's bottom output panel overlays the lower canvas region.
    await node.click({ button: 'right' });
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toBeVisible({ timeout: 10_000 });
    await page.locator('.react-flow__pane').click({ position: { x: 700, y: 90 } });
    await expect(page.getByRole('button', { name: /^duplicate$/i })).toHaveCount(0);

    // Select the node so the properties panel reflects its state (pill shows "Active").
    await node.click();
    await expect(page.getByRole('button', { name: /^Active$/ })).toBeVisible({ timeout: 10_000 });

    // Re-open the context menu and choose "Disable step" → the action runs and the node's
    // state flips, observable in the properties panel pill ("Active" → "Disabled").
    await node.click({ button: 'right' });
    await page.getByRole('button', { name: /disable step/i }).click();
    await expect(page.getByRole('button', { name: /^Disabled$/ })).toBeVisible();
  });

  test('17.4 — activity sidebar lists categorized activities; click-add drops a node', async ({ page }) => {
    await openEditor(page);

    // The sidebar defaults to the "Workflows" tab — switch to the "Nodes" (Node Library) tab.
    // The expanded panel has no "Node Library" heading; its search box confirms it mounted.
    await page.getByRole('button', { name: /^nodes$/i }).click();
    await expect(page.getByPlaceholder(/search nodes/i)).toBeVisible({ timeout: 15_000 });

    // Category headers (translated, EN) are present.
    await expect(page.getByRole('heading', { name: /^triggers$/i })).toBeVisible();
    await expect(page.getByRole('heading', { name: /^actions$/i })).toBeVisible();
    await expect(page.getByRole('heading', { name: /control flow/i })).toBeVisible();

    // Search box filters the catalogue.
    const search = page.getByPlaceholder(/search nodes/i);
    await expect(search).toBeVisible();
    await search.fill('runScript');
    // A matching activity entry is shown (label "Run Script").
    const runScriptItem = page.getByRole('button', { name: /run script/i }).first();
    await expect(runScriptItem).toBeVisible();

    // Drag-onto-canvas uses HTML5 DnD on the RF pane which synthetic events can't drive;
    // exercise the equivalent click-to-add path instead (same addNode() handler).
    await expect(page.locator('.react-flow__node')).toHaveCount(1);
    await runScriptItem.click();
    await expect(page.locator('.react-flow__node')).toHaveCount(2);
    test.info().annotations.push({ type: 'skip-note', description: 'HTML5 drag-drop onto RF canvas needs real DnD; click-add covers node creation' });
  });

  test('17.5 — dark revamp: dark-orange accent is scoped to the app shell, the designer is untouched', async ({ page }) => {
    // Boot in dark mode (themeStore reads this before first paint).
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark' }, version: 0 })),
    );

    // App shell (Workflows list): the `.np-shell` scope is present and the primary
    // "New Workflow" button (a hardcoded bg-blue-600) is remapped to the dark orange.
    await page.goto('/workflows');
    await expect(page.locator('.np-shell')).toHaveCount(1, { timeout: 15_000 });
    const newBtn = page.getByRole('button', { name: /new workflow|neuer workflow/i });
    await expect(newBtn).toBeVisible();
    const shellBtnRgb = await newBtn.evaluate((el) => getComputedStyle(el).backgroundColor);
    const [sr, , sb] = shellBtnRgb.match(/\d+/g)!.map(Number);
    expect(sr).toBeGreaterThan(150); // orange = high red …
    expect(sb).toBeLessThan(70);     // … and low blue (blue-600 would be ~235)
    const darkActiveNav = await page.locator('a.np-nav[aria-current="page"]').evaluate((el) => getComputedStyle(el).color);
    expect(darkActiveNav).not.toBe('rgb(255, 104, 117)'); // Bank Hell's coral remains skin-local

    // `on-primary-fixed` (text on the subtle accent surface) is explicitly warm here —
    // without the override it would inherit the base html.dark blue-tinted #dae2ff.
    const onFixed = await page.evaluate(() =>
      getComputedStyle(document.querySelector('.np-shell')!).getPropertyValue('--color-on-primary-fixed').trim().toLowerCase(),
    );
    expect(onFixed).toBe('#ffe6d4');

    // Designer route (/workflows/:id): rendered OUTSIDE the shell (no `.np-shell`). The
    // <html> base accent token stays blue — the React Flow canvas reads it via the shield,
    // so nodes/edges/selection are untouched. The designer CHROME, however, now carries its
    // own dark-orange accent scoped to `.np-designer`. Assert both.
    await openEditor(page);
    await expect(page.locator('.np-shell')).toHaveCount(0);
    const htmlPrimary = await page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--color-primary').trim().toLowerCase(),
    );
    expect(htmlPrimary).toBe('#aac7ff'); // base dark blue (canvas accent), untouched
    const designer = await page.evaluate(() => {
      const el = document.querySelector('.np-designer')!;
      const cs = getComputedStyle(el);
      return {
        primary: cs.getPropertyValue('--color-primary').trim().toLowerCase(),
        surface: cs.getPropertyValue('--color-surface').trim().toLowerCase(),
      };
    });
    expect(designer.primary).toBe('#fc8861'); // designer chrome accent = dark orange
    expect(designer.surface).toBe('#14110e'); // designer AREA backgrounds follow the skin (warm charcoal)
  });

  test('17.6 — dark-lila skin: applies data-skin + lilac accent and persists', async ({ page }) => {
    // Boot directly in the dark-lila skin (themeStore reads this before first paint).
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark-lila' }, version: 0 })),
    );
    await page.goto('/workflows');
    await expect(page.locator('.np-shell')).toHaveCount(1, { timeout: 15_000 });

    // Applied as a DARK base (so all html.dark tokens still resolve) + the skin marker.
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true);
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('dark-lila');

    // Shell accent token is the Voxwright lilac — NOT the orange dark default.
    const shellTokens = await page.evaluate(() => {
      const cs = getComputedStyle(document.querySelector('.np-shell')!);
      return {
        primary: cs.getPropertyValue('--color-primary').trim().toLowerCase(),
        onFixed: cs.getPropertyValue('--color-on-primary-fixed').trim().toLowerCase(),
      };
    });
    expect(shellTokens.primary).toBe('#9b7dff');
    // `on-primary-fixed` is lilac-tinted, not the inherited base #dae2ff blue.
    expect(shellTokens.onFixed).toBe('#ece7ff');

    // The sidebar rail uses a NEUTRAL grey gradient, not the dark-orange skin's warm
    // brown (#241e18 → rgb(36,30,24)). #2a2a2a resolves to rgb(42, 42, 42).
    const asideBg = await page.locator('.np-shell aside').first().evaluate((el) => getComputedStyle(el).backgroundImage);
    expect(asideBg).toContain('42, 42, 42');
    expect(asideBg).not.toContain('36, 30, 24');

    // Selectable from Settings → persists under the themeStore key.
    await page.goto('/settings');
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    await appearance.getByRole('button', { name: /dark lilac/i }).click();
    await expect.poll(() => page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"dark-lila"');
  });

  test('17.7 — light-grey skin: light base + lilac accent, remaps hardcoded blues', async ({ page }) => {
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'light-grey' }, version: 0 })),
    );
    await page.goto('/workflows');
    await expect(page.locator('.np-shell')).toHaveCount(1, { timeout: 15_000 });

    // Light base (no `.dark`) + the skin marker + the accent-remap marker.
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(false);
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-grey');
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('np-accent-remap'))).toBe(true);

    // Shell accent token = lilac, and the hardcoded bg-blue-600 "New Workflow" button is
    // remapped to the lilac fill (red channel ≈124, vs blue-600's ≈37 → proves the remap).
    const shellPrimary = await page.evaluate(() =>
      getComputedStyle(document.querySelector('.np-shell')!).getPropertyValue('--color-primary').trim().toLowerCase(),
    );
    expect(shellPrimary).toBe('#7c3aed');
    const newBtn = page.getByRole('button', { name: /new workflow|neuer workflow/i });
    await expect(newBtn).toBeVisible();
    const btnRgb = await newBtn.evaluate((el) => getComputedStyle(el).backgroundColor);
    const [r] = btnRgb.match(/\d+/g)!.map(Number);
    expect(r).toBeGreaterThan(90); // lilac fill, not the original blue-600
    const lightGreyActiveNav = await page.locator('a.np-nav[aria-current="page"]').evaluate((el) => getComputedStyle(el).color);
    expect(lightGreyActiveNav).not.toBe('rgb(255, 104, 117)'); // reference coral is Bank Hell-only
  });

  test('17.9 - light-bank skin: light base, semantic red tokens, flat CTA, and persisted picker selection', async ({ page }) => {
    // Boot directly in Bank Hell so the skin is applied before the app first renders.
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'light-sparkasse' }, version: 0 })),
    );
    await page.route((url) => url.pathname === '/api/stats/dashboard', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ workflowsTotal: 24, machinesTotal: 128, runningCount: 3 }),
    }));
    await page.goto('/workflows');
    await expect(page.locator('.np-shell')).toHaveCount(1, { timeout: 15_000 });

    // The skin keeps the light base, writes its marker, and opts into blue-to-red remapping.
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(false);
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-sparkasse');
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('np-accent-remap'))).toBe(true);

    // Bank Hell's semantic shell palette is intentionally explicit: neutral banking surfaces,
    // the established readable red control scale, and light red fixed surfaces for selected states.
    const shellTokens = await page.evaluate(() => {
      const shell = document.querySelector('.np-shell');
      if (!shell) throw new Error('Expected Bank Hell app shell');
      const cs = getComputedStyle(shell);
      return {
        surface: cs.getPropertyValue('--color-surface').trim().toLowerCase(),
        surfaceLowest: cs.getPropertyValue('--color-surface-lowest').trim().toLowerCase(),
        primary: cs.getPropertyValue('--color-primary').trim().toLowerCase(),
        primaryContainer: cs.getPropertyValue('--color-primary-container').trim().toLowerCase(),
        primaryFixed: cs.getPropertyValue('--color-primary-fixed').trim().toLowerCase(),
        primaryFixedDim: cs.getPropertyValue('--color-primary-fixed-dim').trim().toLowerCase(),
        onSurface: cs.getPropertyValue('--color-on-surface').trim().toLowerCase(),
        outlineVariant: cs.getPropertyValue('--color-outline-variant').trim().toLowerCase(),
      };
    });
    expect(shellTokens).toEqual({
      surface: '#f3f5f7',
      surfaceLowest: expect.stringMatching(/^#(?:fff|ffffff)$/),
      primary: '#c80000',
      primaryContainer: expect.stringMatching(/^#(?:e00|ee0000)$/),
      primaryFixed: '#fff1f1',
      primaryFixedDim: '#ffe3e3',
      onSurface: '#15191f',
      outlineVariant: '#e4e7ec',
    });

    await expect(page.locator('a.np-nav[href="/workflows"] .np-nav-badge')).toHaveText('24');

    // Sidebar fidelity is intentionally independent from the higher-contrast content tokens.
    // Assert the browser's computed result so selector order and later skin overrides cannot
    // silently regress the template typography, coral active plate, badges, or dimensions.
    const sidebarFidelity = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('aside.np-sidebar');
      const active = document.querySelector<HTMLElement>('a.np-nav[aria-current="page"]');
      const activeIcon = active?.querySelector<HTMLElement>('.np-nav-icon');
      const normal = document.querySelector<HTMLElement>('a.np-nav[href="/"]');
      const normalIcon = normal?.querySelector<HTMLElement>('.np-nav-icon');
      const badge = active?.querySelector<HTMLElement>('.np-nav-badge');
      const section = document.querySelector<HTMLElement>('.np-sb-section-title');
      if (!sidebar || !active || !activeIcon || !normal || !normalIcon || !badge || !section) {
        throw new Error('Expected complete expanded Bank Hell sidebar');
      }
      const sidebarStyle = getComputedStyle(sidebar);
      const activeStyle = getComputedStyle(active);
      return {
        width: sidebarStyle.width,
        fontFamily: sidebarStyle.fontFamily,
        normalText: getComputedStyle(normal).color,
        normalIcon: getComputedStyle(normalIcon).color,
        heading: getComputedStyle(section).color,
        badge: getComputedStyle(badge).color,
        activeText: activeStyle.color,
        activeBorder: activeStyle.borderColor,
        activeBackground: activeStyle.backgroundImage,
        activeIcon: getComputedStyle(activeIcon).color,
      };
    });
    expect(sidebarFidelity).toMatchObject({
      width: '292px',
      normalText: 'rgb(71, 85, 105)',
      normalIcon: 'rgb(102, 112, 133)',
      heading: 'rgb(146, 155, 173)',
      badge: 'rgb(139, 149, 166)',
      activeText: 'rgb(255, 104, 117)',
      activeBorder: 'rgba(242, 36, 53, 0.24)',
      activeIcon: 'rgb(255, 78, 93)',
    });
    expect(sidebarFidelity.fontFamily).toContain('Segoe UI');
    expect(sidebarFidelity.activeBackground).toContain('242, 36, 53');
    expect(sidebarFidelity.activeBackground).toContain('0.15');
    // Nav glyphs are Carbon icon <svg>s (the icon-font migration dropped the bootstrap
    // `<i class="bi-…">` markup), so assert the active item's icon element renders.
    await expect(page.locator('a.np-nav[aria-current="page"] .np-nav-icon > svg')).toBeVisible();
    await page.setViewportSize({ width: 1100, height: 900 });
    await expect(page.locator('aside.np-sidebar')).toHaveCSS('width', '264px');

    // A hardcoded Tailwind blue CTA is remapped to the familiar bright Bank Hell red
    // while retaining the original, flat compact-button treatment.
    const newBtn = page.getByRole('button', { name: /new workflow|neuer workflow/i });
    await expect(newBtn).toBeVisible();
    const cta = await newBtn.evaluate((el) => {
      const cs = getComputedStyle(el);
      return { backgroundImage: cs.backgroundImage.toLowerCase(), backgroundColor: cs.backgroundColor };
    });
    expect(cta.backgroundImage).toBe('none');
    const [red, green, blue] = cta.backgroundColor.match(/\d+(?:\.\d+)?/g)!.map(Number);
    expect(red).toBeGreaterThan(220);
    expect(green).toBeLessThan(35);
    expect(blue).toBeLessThan(35);

    // Choose the visible English translation from Settings (rather than relying on an
    // internal id), then prove the explicit picker choice survives a full reload.
    await page.goto('/settings');
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    await appearance.getByRole('button', { name: /^light$/i }).click();
    const lightBank = appearance.getByRole('button', { name: /^light bank$/i });
    await expect(lightBank).toBeVisible();
    await lightBank.click();
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-sparkasse');
    await expect.poll(() => page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"light-sparkasse"');
    await page.reload();
    await expect(page.getByRole('heading', { name: /appearance/i })).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('light-sparkasse');
  });

  test('17.10 — nebula skin: dark base + cyan accent, glowing sidebar, glass cards, designer chrome cyan / canvas neutral', async ({ page }) => {
    // Boot directly in the nebula skin (themeStore reads this before first paint).
    await page.addInitScript(() =>
      localStorage.setItem('nodepilot.theme', JSON.stringify({ state: { theme: 'dark-nebula' }, version: 0 })),
    );
    await page.goto('/workflows');
    await expect(page.locator('.np-shell')).toHaveCount(1, { timeout: 15_000 });

    // Applied as a DARK base (so all html.dark tokens still resolve) + the skin + remap markers.
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true);
    await expect.poll(() => page.evaluate(() => document.documentElement.dataset.skin)).toBe('dark-nebula');
    await expect.poll(() => page.evaluate(() => document.documentElement.classList.contains('np-accent-remap'))).toBe(true);

    // Shell accent tokens are the electric cyan — NOT the orange dark default.
    const shellTokens = await page.evaluate(() => {
      const cs = getComputedStyle(document.querySelector('.np-shell')!);
      return {
        primary: cs.getPropertyValue('--color-primary').trim().toLowerCase(),
        onFixed: cs.getPropertyValue('--color-on-primary-fixed').trim().toLowerCase(),
        glow: cs.getPropertyValue('--np-glow').trim().toLowerCase(),
      };
    });
    expect(shellTokens.primary).toBe('#4de4f7');
    expect(shellTokens.onFixed).toBe('#b8f4ff'); // cyan-tinted, not the base #dae2ff blue
    expect(shellTokens.glow).toBe('#22d3ee');     // the depth-layer glow token is live

    // The sidebar rail uses the deep blue-black nebula gradient (#10192b → rgb(16, 25, 43)),
    // not the dark-orange warm brown (#241e18 → 36, 30, 24) nor the lila grey (42, 42, 42).
    const asideBg = await page.locator('.np-shell aside').first().evaluate((el) => getComputedStyle(el).backgroundImage);
    expect(asideBg).toContain('16, 25, 43');
    expect(asideBg).not.toContain('36, 30, 24');

    // Selectable from Settings → persists under the themeStore key.
    await page.goto('/settings');
    const appearance = page.getByRole('heading', { name: /appearance/i }).locator('..');
    await appearance.getByRole('button', { name: /^nebula$/i }).click();
    await expect.poll(() => page.evaluate(() => localStorage.getItem('nodepilot.theme'))).toContain('"theme":"dark-nebula"');

    // Designer route: chrome carries the cyan accent while the React-Flow canvas stays neutral
    // (base dark blue) via the shared shield — nodes/edges untouched, matching the other skins.
    await openEditor(page);
    await expect(page.locator('.np-shell')).toHaveCount(0);
    const htmlPrimary = await page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--color-primary').trim().toLowerCase(),
    );
    expect(htmlPrimary).toBe('#aac7ff'); // base dark blue (canvas accent), untouched
    const designerPrimary = await page.evaluate(() =>
      getComputedStyle(document.querySelector('.np-designer')!).getPropertyValue('--color-primary').trim().toLowerCase(),
    );
    expect(designerPrimary).toBe('#4de4f7'); // designer chrome accent = cyan
  });

});
