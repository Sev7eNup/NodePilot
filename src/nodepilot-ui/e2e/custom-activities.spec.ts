import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Custom Activities ("Custom Nodes") — plugin CRUD on /custom-activities.
 *
 * Backed by `CustomActivitiesPage.tsx`: a definition is created disabled (Draft);
 * Admin+Operator may edit/delete while disabled, but once an Admin enables it only an
 * Admin can mutate it (the server enforces this — the UI mirrors it via
 * `canEdit = canWrite && (canAdmin || !isEnabled)`). Power (enable/disable) is
 * `canAdmin`-only; New/Import/Export/History are `canWrite`-only; Trash is always
 * rendered but disabled when `!canEdit`. There is no secret input type — secrets come
 * via `{{globals.X}}` (governance per CLAUDE.md "Custom Activities").
 *
 * No real backend — every /api call is a per-test `page.route` mock layered over the
 * hermetic catch-all in `fixtures/mockApi.ts`. The preview build renders EN, but every
 * visible-text selector is bilingual (`/en|de/i`) and anchored on the verified EN
 * string from `src/i18n/locales/en/customActivities.json`.
 *
 * CodeMirror typing: this suite uses the proven click → Ctrl+A → keyboard.type pattern
 * (see `step-test.spec.ts` 28.x); `.fill()` on the contenteditable `.cm-content` is not
 * reliable across preview builds, so we go straight to the fallback.
 *
 * API surface (all prefixed `/api` by `api/client.ts`):
 *  GET    /custom-activities?includeDisabled=true   → CatalogEntry[]
 *  GET    /custom-activities/{id}                   → FullDef
 *  POST   /custom-activities                        → { definition, warnings[] }   (body carries `key`)
 *  PUT    /custom-activities/{id}                   → { definition, warnings[] }   (body has NO `key`)
 *  POST   /custom-activities/{id}/enable|disable
 *  GET    /custom-activities/{id}/versions          → VersionEntry[]  (previous snapshots only)
 *  POST   /custom-activities/{id}/rollback/{v}
 *  GET    /custom-activities/export                 → export envelope (array)
 *  POST   /custom-activities/import                 → imported[] (nodes land disabled)
 */

// --- mock factories -------------------------------------------------------------

function entryJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'ca-00000000-0000-0000-0000-000000000001',
    key: 'disk-check',
    type: 'custom:disk-check',
    name: 'Disk Check',
    description: 'Checks free disk space.',
    icon: 'extension',
    color: null,
    runsRemote: true,
    timeout: '00:01:00',
    inputs: [],
    outputs: [],
    isEnabled: false,
    version: 1,
    ...overrides,
  };
}

function fullDefJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'ca-00000000-0000-0000-0000-000000000001',
    key: 'disk-check',
    type: 'custom:disk-check',
    name: 'Disk Check',
    description: 'Checks free disk space.',
    icon: 'extension',
    color: null,
    runsRemote: true,
    timeout: '00:01:00',
    inputs: [],
    outputs: [],
    isEnabled: false,
    version: 1,
    scriptTemplate: 'Write-Output "ok"',
    engine: 'auto',
    isolated: false,
    memoryLimitMb: null,
    maxProcesses: null,
    defaultTimeoutSeconds: 60,
    successExitCodes: null,
    concurrencyToken: 'tok-1',
    updatedAt: '2026-07-01T00:00:00.000Z',
    updatedBy: 'e2e-admin',
    ...overrides,
  };
}

/** One historical snapshot row from GET /{id}/versions (newest first in the real API). */
function versionEntryJson(version: number, overrides: Record<string, unknown> = {}) {
  return {
    version,
    name: 'Disk Check',
    description: null,
    engine: 'auto',
    runsRemote: true,
    createdAt: `2026-07-0${version}T00:00:00.000Z`,
    createdBy: 'e2e-admin',
    changeNote: null,
    ...overrides,
  };
}

/** POST/PUT response envelope — `{ definition, warnings }`. Empty warnings → modal closes. */
function saveResponseJson(overrides: Record<string, unknown> = {}) {
  return {
    definition: fullDefJson(),
    warnings: [] as { rule: string; message: string }[],
    ...overrides,
  };
}

// The create/edit ModalShell renders its heading as a direct child of the panel, so the
// heading's parent (`..`) IS the panel. (VersionsModal nests its heading in a flex div →
// grandparent `../..`; that modal is addressed per-locator in its tests.)
function createEditPanel(page: Page, heading: RegExp) {
  return page.getByRole('heading', { name: heading }).locator('..');
}

// --- tests ----------------------------------------------------------------------

test.describe('Custom Activities', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('1 — renders the list with Live + Draft rows and version numbers', async ({ page }) => {
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([
            entryJson({ id: 'ca-live', name: 'Health Probe', key: 'health-probe', isEnabled: true, version: 2 }),
            entryJson({ id: 'ca-draft', name: 'Cleanup Task', key: 'cleanup-task', isEnabled: false, version: 1 }),
          ]),
        }),
    );

    await page.goto('/custom-activities');

    // Page subtitle (customActivities:subtitle) — confirms mount.
    await expect(page.getByText(/reusable, powershell-backed activities|wiederverwendbare, powershell-basierte activities/i)).toBeVisible({
      timeout: 15_000,
    });

    // Status badges: customActivities:status.enabled = "Live" / status.draft = "Draft" (DE: "Entwurf").
    // Scope to #np-main-scroll: the sidebar "Live-Ops" nav item also renders a "Live" badge, so an
    // unscoped /^live$/i matches 2 elements (strict-mode violation).
    const main = page.locator('#np-main-scroll');
    await expect(main.getByText(/^live$/i)).toBeVisible();
    await expect(main.getByText(/draft|entwurf/i)).toBeVisible();

    // Version cell renders `v{version}`.
    await expect(page.getByText(/^v2$/)).toBeVisible();
    await expect(page.getByText(/^v1$/)).toBeVisible();
  });

  test('2 — creates a node (script + input param + icon picker) and POSTs the body', async ({ page }) => {
    const rows: ReturnType<typeof entryJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) => {
        const req = route.request();
        if (req.method() === 'GET') {
          return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
        }
        if (req.method() === 'POST') {
          postedBody = req.postDataJSON();
          const body = postedBody as { key: string; name: string; icon: string };
          const created = entryJson({ id: 'new-1', key: body.key, name: body.name, icon: body.icon, version: 1, isEnabled: false });
          rows.push(created);
          return route.fulfill({
            status: 201,
            contentType: 'application/json',
            body: JSON.stringify(saveResponseJson({ definition: fullDefJson({ id: 'new-1', key: body.key, name: body.name, icon: body.icon }) })),
          });
        }
        return route.fallback();
      },
    );

    await page.goto('/custom-activities');
    // Empty state first (customActivities:empty).
    await expect(page.getByText(/no custom nodes yet|noch keine custom nodes/i)).toBeVisible({ timeout: 15_000 });

    // customActivities:new = "New Custom Node" (DE: "Neue Custom Node").
    await page.getByRole('button', { name: /new custom node|neue custom node/i }).click();

    const panel = createEditPanel(page, /create custom node|custom node erstellen/i);
    await expect(panel).toBeVisible();

    // Name field — labeled "Name" (fields.name). Key field placeholder is "disk-check" (unique).
    await panel.getByText(/^name$/i).locator('..').getByRole('textbox').fill('Probe A');
    await panel.getByPlaceholder('disk-check').fill('probe-a');

    // Icon picker — click the Icon field button (fields.icon = "Icon"), pick "bolt" by title.
    await panel.getByText(/^icon$/i).locator('..').getByRole('button').click();
    await expect(page.getByRole('heading', { name: /choose an icon|icon wählen/i })).toBeVisible();
    await page.getByTitle('bolt').click();

    // PowerShell template — CodeMirror. Click → select-all → type (fallback pattern, see header).
    const cm = panel.locator('.cm-content');
    await cm.click();
    await page.keyboard.press('ControlOrMeta+A');
    await page.keyboard.type('Write-Output "hi"');

    // Add one input param (param.addInput = "Add input"), fill its name (param.name placeholder = "Name").
    await panel.getByRole('button', { name: /add input|input hinzufügen/i }).click();
    await panel.getByPlaceholder(/^name$/i).fill('computerName');

    // Submit (common:create = "Create").
    await panel.getByRole('button', { name: /^create$|^anlegen$/i }).click();

    await expect.poll(() => postedBody, { timeout: 10_000 }).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'Probe A',
      key: 'probe-a',
      icon: 'bolt',
      scriptTemplate: 'Write-Output "hi"',
      inputs: [{ name: 'computerName' }],
    });

    // Modal closes (empty warnings → onSuccess closes) and the new row appears.
    await expect(panel).toHaveCount(0);
    await expect(page.getByText('Probe A')).toBeVisible();
  });

  test('3 — edit: key input is disabled; rename PUTs new name without a key field', async ({ page }) => {
    const id = 'ca-edit';
    let putBody: Record<string, unknown> | null = null;
    const rows = [entryJson({ id, name: 'Old Name', key: 'old-key', isEnabled: false, version: 1 })];

    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route((url) => url.pathname === `/api/custom-activities/${id}`, (route) => {
      if (route.request().method() === 'GET') {
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(fullDefJson({ id, name: 'Old Name', key: 'old-key', isEnabled: false })),
        });
      }
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(saveResponseJson({ definition: fullDefJson({ id, name: 'Renamed Node' }) })),
        });
      }
      return route.fallback();
    });

    await page.goto('/custom-activities');
    await expect(page.getByText('Old Name')).toBeVisible({ timeout: 15_000 });

    // Edit button — icon-only, title = actions.edit ("Edit") on an editable (Draft) row.
    await page.getByTitle(/^edit$|^bearbeiten$/i).click();

    const panel = createEditPanel(page, /edit custom node|custom node bearbeiten/i);
    await expect(panel).toBeVisible();

    // Key input is disabled in edit mode (immutable slug). Placeholder "disk-check" is unique.
    const keyInput = panel.getByPlaceholder('disk-check');
    await expect(keyInput).toBeDisabled();
    await expect(keyInput).toHaveValue('old-key');

    // Rename.
    await panel.getByText(/^name$/i).locator('..').getByRole('textbox').fill('Renamed Node');
    await panel.getByRole('button', { name: /^update$|^aktualisieren$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ name: 'Renamed Node' });
    // PUT body must NOT carry `key` (immutable on update).
    expect(putBody).not.toHaveProperty('key');

    await expect(panel).toHaveCount(0);
  });

  test('4a — Admin enable: Power click POSTs .../enable', async ({ page }) => {
    const id = 'ca-enable';
    let enableHit = false;
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([entryJson({ id, name: 'Go-Live', key: 'go-live', isEnabled: false, version: 1 })]),
        }),
    );
    await page.route((url) => url.pathname === `/api/custom-activities/${id}/enable`, (route) => {
      enableHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/custom-activities');
    // exact:true — the row also renders the key as <code>go-live</code>, and getByText is
    // case-insensitive by default so 'Go-Live' would match BOTH the name span and the code.
    await expect(page.getByText('Go-Live', { exact: true })).toBeVisible({ timeout: 15_000 });

    // Draft row → Power title = actions.enable ("Enable" / DE "Aktivieren").
    await page.getByTitle(/^enable$|^aktivieren$/i).click();

    await expect.poll(() => enableHit, { timeout: 10_000 }).toBe(true);
  });

  test('4b — Operator: Power hidden; Edit enabled on Draft row, disabled on Live row', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Operator' }) }),
    );
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([
            entryJson({ id: 'ca-d', name: 'Alpha Draft', key: 'alpha', isEnabled: false, version: 1 }),
            entryJson({ id: 'ca-l', name: 'Beta Live', key: 'beta', isEnabled: true, version: 3 }),
          ]),
        }),
    );

    await page.goto('/custom-activities');
    await expect(page.getByText('Alpha Draft')).toBeVisible({ timeout: 15_000 });

    // Power (enable/disable) is canAdmin-only → Operator sees none.
    await expect(page.getByTitle(/^enable$|^disable$|^aktivieren$|^deaktivieren$/i)).toHaveCount(0);

    // Alpha (Draft) is editable by an Operator → Edit button title = "Edit", not disabled.
    const alphaEdit = page.locator('tr', { hasText: 'Alpha Draft' }).getByTitle(/^edit$|^bearbeiten$/i);
    await expect(alphaEdit).toBeVisible();
    await expect(alphaEdit).toBeEnabled();

    // Beta (Live) is NOT editable by an Operator → Edit button title = lockedHint, disabled.
    // lockedHint (en): "This node is live (enabled). Only an administrator can edit it …"
    const betaLocked = page.locator('tr', { hasText: 'Beta Live' }).getByTitle(/this node is live|ist live/i);
    await expect(betaLocked).toBeVisible();
    await expect(betaLocked).toBeDisabled();
  });

  test('5 — version-history modal lists previous snapshots; current version is not a rollback target', async ({ page }) => {
    const id = 'ca-ver';
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([entryJson({ id, name: 'My Node', key: 'my-node', isEnabled: true, version: 3 })]),
        }),
    );
    await page.route((url) => url.pathname === `/api/custom-activities/${id}/versions`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([versionEntryJson(2), versionEntryJson(1)]),
      }),
    );

    await page.goto('/custom-activities');
    await expect(page.getByText('My Node')).toBeVisible({ timeout: 15_000 });

    // History button — icon-only, title = actions.versions ("Version history" / DE "Versionsverlauf").
    await page.getByTitle(/version history|versionsverlauf/i).click();

    // versionsTitle = "Version history" — heading "Version history — My Node".
    await expect(page.getByRole('heading', { name: /version history|versionsverlauf/i })).toBeVisible();
    // versions.current = "Current state: v{{version}}" (DE: "Aktueller Stand: v{{version}}").
    await expect(page.getByText(/current state: v3|aktueller stand: v3/i)).toBeVisible();

    // Previous snapshots v1 + v2 are listed as rollback targets (rollback button per row).
    await expect(page.locator('tr', { hasText: 'v2' }).getByRole('button', { name: /roll back to this version|auf diese version zurücksetzen/i })).toBeVisible();
    await expect(page.locator('tr', { hasText: 'v1' }).getByRole('button', { name: /roll back to this version|auf diese version zurücksetzen/i })).toBeVisible();
    // The live version (v3) is NOT a snapshot row → only 2 rollback targets exist.
    await expect(page.getByRole('button', { name: /roll back to this version|auf diese version zurücksetzen/i })).toHaveCount(2);
  });

  test('6 — rollback to v2 with confirm POSTs .../rollback/2 and toasts success', async ({ page }) => {
    const id = 'ca-rb';
    let rollbackHit = false;
    let rollbackVersion: number | null = null;
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([entryJson({ id, name: 'Roll Target', key: 'roll-target', isEnabled: true, version: 3 })]),
        }),
    );
    await page.route((url) => url.pathname === `/api/custom-activities/${id}/versions`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([versionEntryJson(2), versionEntryJson(1)]),
      }),
    );
    await page.route((url) => url.pathname === `/api/custom-activities/${id}/rollback/2`, (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      rollbackHit = true;
      rollbackVersion = 2;
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await page.goto('/custom-activities');
    await expect(page.getByText('Roll Target')).toBeVisible({ timeout: 15_000 });
    await page.getByTitle(/version history|versionsverlauf/i).click();

    // Rollback on the v2 row (rollback = "Roll back to this version" / DE "Auf diese Version zurücksetzen").
    await page.locator('tr', { hasText: 'v2' }).getByRole('button', { name: /roll back to this version|auf diese version zurücksetzen/i }).click();

    // In-app confirm modal → OK (common:ok = "OK").
    await page.getByRole('button', { name: /^ok$/i }).click();

    await expect.poll(() => rollbackHit, { timeout: 10_000 }).toBe(true);
    expect(rollbackVersion).toBe(2);
    // rollbackDone = "Rolled back to version {{version}}." (DE: "Auf Version … zurückgesetzt.").
    await expect(page.getByText(/rolled back to version 2|auf version 2 zurückgesetzt/i)).toBeVisible();
  });

  test('7 — export downloads the envelope; import POSTs the file and toasts import-done', async ({ page }) => {
    const id = 'ca-impexp';
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([entryJson({ id, name: 'Exportable', key: 'exportable', isEnabled: true, version: 1 })]),
        }),
    );
    // GET /custom-activities/export → array envelope.
    await page.route((url) => url.pathname === '/api/custom-activities/export', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([entryJson({ id: 'exp-1', name: 'Packed', key: 'packed' })]) }),
    );
    let importBody: unknown = null;
    let importHit = false;
    await page.route((url) => url.pathname === '/api/custom-activities/import', (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      importHit = true;
      importBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([entryJson({ id: 'imp-1', name: 'Packed', key: 'packed' })]) });
    });

    await page.goto('/custom-activities');
    // exact:true — the row key <code>exportable</code> would else collide (case-insensitive match).
    await expect(page.getByText('Exportable', { exact: true })).toBeVisible({ timeout: 15_000 });

    // Export (customActivities:export = "Export" / DE "Exportieren") → blob download named
    // `custom-nodes.npca.json` (hard-coded in onExport).
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('button', { name: /^export$|^exportieren$/i }).click(),
    ]);
    expect(download.suggestedFilename()).toBe('custom-nodes.npca.json');

    // Import: drive the hidden <input type="file" accept=".npca,.json,..."> directly (no
    // HTML5 drag). The page reads the file text, JSON.parses it, and POSTs the envelope as-is.
    await page.setInputFiles('input[type="file"][accept*=".npca"]', {
      name: 'custom-nodes.npca',
      mimeType: 'application/json',
      buffer: Buffer.from(JSON.stringify([entryJson({ id: 'imp-1', name: 'Packed', key: 'packed' })])),
    });

    await expect.poll(() => importHit, { timeout: 10_000 }).toBe(true);
    expect(importBody).toBeTruthy();
    // importDone = "Imported {{count}} custom node(s) (disabled — review and enable)."
    await expect(page.getByText(/imported 1 custom node\(s\)|1 custom node\(s\) importiert/i)).toBeVisible();
  });

  test('8 — Viewer sees no write controls; Trash is rendered but disabled', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }) }),
    );
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([
            entryJson({ id: 'ro-live', name: 'Live RO', key: 'live-ro', isEnabled: true, version: 2 }),
            entryJson({ id: 'ro-draft', name: 'Draft RO', key: 'draft-ro', isEnabled: false, version: 1 }),
          ]),
        }),
    );

    await page.goto('/custom-activities');
    await expect(page.getByText('Live RO')).toBeVisible({ timeout: 15_000 });

    // New / Import / Export are canWrite-gated → absent for Viewer.
    await expect(page.getByRole('button', { name: /new custom node|neue custom node/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^import$|^importieren$/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^export$|^exportieren$/i })).toHaveCount(0);
    // Power is canAdmin-gated → absent.
    await expect(page.getByTitle(/^enable$|^disable$|^aktivieren$|^deaktivieren$/i)).toHaveCount(0);
    // History (versions) is canWrite-gated → absent.
    await expect(page.getByTitle(/version history|versionsverlauf/i)).toHaveCount(0);
    // Edit: for a Viewer canEdit is always false → the edit button's title is the lockedHint
    // (not "Edit"), so an anchored /^edit$/i match finds none.
    await expect(page.getByTitle(/^edit$|^bearbeiten$/i)).toHaveCount(0);

    // ADAPTATION: the Trash button is always rendered (title "Delete") but disabled when
    // !canEdit — so for a Viewer it is present-but-disabled, not count 0. The task brief
    // listed "Trash count 0"; the source renders it disabled instead. Assert disabled.
    const trashButtons = page.getByTitle(/^delete$|^löschen$/i);
    await expect(trashButtons).toHaveCount(2);
    for (const btn of await trashButtons.all()) await expect(btn).toBeDisabled();
  });

  test('9 — lint warnings block the modal open with an amber warning block', async ({ page }) => {
    const rows: ReturnType<typeof entryJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;
    await page.route(
      (url) => url.pathname === '/api/custom-activities',
      (route) => {
        const req = route.request();
        if (req.method() === 'GET') {
          return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
        }
        if (req.method() === 'POST') {
          postedBody = req.postDataJSON();
          // Non-empty warnings → onSuccess keeps the modal open and surfaces them.
          return route.fulfill({
            status: 201,
            contentType: 'application/json',
            body: JSON.stringify({
              definition: fullDefJson({ id: 'warn-1', key: 'warn-1', name: 'Risky' }),
              warnings: [{ rule: 'no-invoke-expression', message: 'Invoke-Expression is forbidden' }],
            }),
          });
        }
        return route.fallback();
      },
    );

    await page.goto('/custom-activities');
    await expect(page.getByText(/no custom nodes yet|noch keine custom nodes/i)).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: /new custom node|neue custom node/i }).click();

    const panel = createEditPanel(page, /create custom node|custom node erstellen/i);
    await panel.getByText(/^name$/i).locator('..').getByRole('textbox').fill('Risky');
    await panel.getByPlaceholder('disk-check').fill('risky');
    const cm = panel.locator('.cm-content');
    await cm.click();
    await page.keyboard.press('ControlOrMeta+A');
    await page.keyboard.type('Invoke-Expression "rm -rf /"');

    await panel.getByRole('button', { name: /^create$|^anlegen$/i }).click();

    await expect.poll(() => postedBody, { timeout: 10_000 }).not.toBeNull();

    // Modal stays open (warnings present → onSuccess does not close).
    await expect(panel).toBeVisible();
    // lintWarnings heading = "Security lint warnings" (DE: "Security-Lint-Warnungen").
    await expect(panel.getByText(/security lint warnings|security-lint-warnungen/i)).toBeVisible();
    // The warning message itself is rendered in the amber <ul>.
    await expect(panel.getByText(/invoke-expression is forbidden/i)).toBeVisible();
  });
});