import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Teil 21 — Global Variables (E2ETests.md ~2073-2114).
 *
 * Admin-managed constants exposed to workflows via `{{globals.NAME}}`. Page lives at
 * /global-variables (GlobalVariablesPage). Admin can list + mutate; Operator/Viewer are
 * read-only (no create/edit/delete buttons). Secrets render as `***` and are never
 * returned (value === null over the wire).
 *
 * No real backend — per-test page.route mocks over the hermetic catch-all. Locale-agnostic
 * (bilingual) selectors throughout. The create/edit dialog is role="presentation"; scope by
 * its heading's parent panel.
 *
 * Test 21.1 — create a plain variable (round-trip with body assertion) + Operator read-only.
 * Test 21.2 — create a secret variable; UI masks the value as `***`; secret value never
 *             returned to the client.
 */

// Root folder sentinel — mirrors GlobalVariableFolder.RootFolderId (…0002).
const ROOT_FOLDER = '00000000-0000-0000-0000-000000000002';

function variableJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'gggggggg-0000-0000-0000-000000000001',
    name: 'API_BASE_URL',
    value: 'https://api.example.com',
    isSecret: false,
    description: null,
    folderId: ROOT_FOLDER,
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    updatedBy: 'e2e-admin',
    ...overrides,
  };
}

function folderJson(overrides: Record<string, unknown> = {}) {
  return {
    id: ROOT_FOLDER,
    parentFolderId: null,
    name: 'Root',
    path: '/',
    depth: 0,
    createdAt: '2026-06-01T00:00:00.000Z',
    createdByUserId: null,
    variableCount: 0,
    ...overrides,
  };
}

function dialogPanel(page: Page) {
  return page
    .getByRole('heading', { name: /new variable|neue variable|edit variable|variable bearbeiten/i })
    .locator('..');
}

test.describe('Teil 21 — Global Variables', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // The page queries the folder tree on mount and filters the list by folder — always mock it
    // (default: just Root, so every variable in Root shows). Tests needing subfolders override.
    await page.route('**/api/global-variable-folders', (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([folderJson()]) });
    });
  });

  test('21.0 — renders the list with plain + secret rows', async ({ page }) => {
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          variableJson({ id: 'g-plain', name: 'API_BASE_URL', value: 'https://api.example.com', isSecret: false }),
          // Secret: server returns value masked as "***" (never the cleartext).
          variableJson({ id: 'g-secret', name: 'API_KEY', value: '***', isSecret: true }),
        ]),
      }),
    );

    await page.goto('/global-variables');

    await expect(page.getByText('API_BASE_URL')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('https://api.example.com')).toBeVisible();
    await expect(page.getByText('API_KEY')).toBeVisible();
    // Type badges (bilingual) — Secret vs Plain. The badge span wraps an icon + text,
    // so match the text as a substring (not anchored).
    await expect(page.getByText(/secret|geheim/i).first()).toBeVisible();
    await expect(page.getByText(/plain|klartext/i).first()).toBeVisible();
    // Secret value is masked.
    await expect(page.getByText('***')).toBeVisible();
  });

  test('21.1a — Admin creates a plain variable (round-trip with body assertion)', async ({ page }) => {
    const rows: ReturnType<typeof variableJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    await page.route('**/api/global-variables', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        const created = variableJson({ id: 'created-1', ...(postedBody as object) });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/global-variables');

    await expect(page.getByText(/no global variables yet|noch keine globalen variablen/i)).toBeVisible({
      timeout: 15_000,
    });

    await page.getByRole('button', { name: /new variable|neue variable/i }).click();

    const panel = dialogPanel(page);
    await expect(panel).toBeVisible();
    // Name input has placeholder "MY_CONSTANT"; value/textbox second; checkbox = secret.
    await panel.getByPlaceholder('MY_CONSTANT').fill('API_BASE_URL');
    // The value field is a plain text input (not secret) — second textbox in the panel.
    await panel.getByRole('textbox').nth(1).fill('https://api.example.com');
    await panel.getByRole('button', { name: /^create$|^anlegen$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'API_BASE_URL',
      value: 'https://api.example.com',
      isSecret: false,
    });

    await expect(panel).toHaveCount(0);
    await expect(page.getByText('API_BASE_URL')).toBeVisible();
    await expect(page.getByText('https://api.example.com')).toBeVisible();
  });

  test('21.1b — Operator is read-only: no create/edit/delete controls', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Operator' }),
      }),
    );
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([variableJson({ id: 'ro-1', name: 'READ_ONLY_VAR', value: 'x' })]),
      }),
    );

    await page.goto('/global-variables');

    // Operator CAN read the list (used in workflows) ...
    await expect(page.getByText('READ_ONLY_VAR')).toBeVisible({ timeout: 15_000 });
    // ... but cannot mutate via the UI (canAdmin gates all write controls).
    await expect(page.getByRole('button', { name: /new variable|neue variable/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^edit$|^bearbeiten$/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^delete$|^löschen$/i })).toHaveCount(0);
  });

  test('21.2 — creates a secret variable; value masked as *** and never returned', async ({ page }) => {
    const rows: ReturnType<typeof variableJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    await page.route('**/api/global-variables', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        // Server stores the secret DPAPI-encrypted and returns it masked — value === "***".
        const created = variableJson({
          id: 'secret-1',
          name: (postedBody as { name: string }).name,
          value: '***',
          isSecret: true,
        });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/global-variables');
    await expect(page.getByText(/no global variables yet|noch keine globalen variablen/i)).toBeVisible({
      timeout: 15_000,
    });

    await page.getByRole('button', { name: /new variable|neue variable/i }).click();

    const panel = dialogPanel(page);
    await expect(panel).toBeVisible();
    await panel.getByPlaceholder('MY_CONSTANT').fill('API_KEY');
    // Toggle the secret checkbox FIRST — this flips the value input to type=password.
    await panel.getByRole('checkbox').check();
    // After flipping to secret, the value field is a password input (no textbox role) —
    // select it by type within the panel.
    await panel.locator('input[type="password"]').fill('sk-secret-xyz');
    await panel.getByRole('button', { name: /^create$|^anlegen$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'API_KEY',
      value: 'sk-secret-xyz',
      isSecret: true,
    });

    // Dialog closes; the new secret row renders masked, and the cleartext is nowhere on the page.
    await expect(panel).toHaveCount(0);
    await expect(page.getByText('API_KEY')).toBeVisible();
    await expect(page.getByText('***')).toBeVisible();
    await expect(page.getByText('sk-secret-xyz')).toHaveCount(0);
  });

  test('21.2b — editing a secret keeps existing value when left blank (value=null in PUT)', async ({ page }) => {
    const id = 'sec-edit';
    const rows = [variableJson({ id, name: 'API_KEY', value: '***', isSecret: true })];
    let putBody: Record<string, unknown> | null = null;

    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/global-variables/${id}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 204 });
      }
      return route.fallback();
    });

    await page.goto('/global-variables');
    await expect(page.getByText('API_KEY')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /^edit$|^bearbeiten$/i }).click();
    const panel = dialogPanel(page);
    await expect(panel).toBeVisible();
    // Change only the description; leave the secret value untouched.
    await panel.getByRole('textbox').last().fill('rotated quarterly');
    await panel.getByRole('button', { name: /^update$|^aktualisieren$/i }).click();

    await expect.poll(() => putBody).not.toBeNull();
    // Untouched secret value -> server told to keep ciphertext (value === null).
    expect(putBody).toMatchObject({ name: 'API_KEY', isSecret: true, value: null });
  });

  test('21.3 — editing a plain variable PUTs the new value', async ({ page }) => {
    const id = 'g-plain-edit';
    const rows = [variableJson({ id, name: 'API_BASE_URL', value: 'https://old.example.com', isSecret: false })];
    let putBody: Record<string, unknown> | null = null;

    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/global-variables/${id}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        rows[0] = { ...rows[0], value: (putBody as { value: string }).value };
        return route.fulfill({ status: 204 });
      }
      return route.fallback();
    });

    await page.goto('/global-variables');
    await expect(page.getByText('API_BASE_URL')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /^edit$|^bearbeiten$/i }).click();
    const panel = dialogPanel(page);
    await expect(panel).toBeVisible();
    // Plain variable → the value field is the second textbox, prefilled from the row.
    const valueInput = panel.getByRole('textbox').nth(1);
    await expect(valueInput).toHaveValue('https://old.example.com');
    await valueInput.fill('https://new.example.com');
    await panel.getByRole('button', { name: /^update$|^aktualisieren$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ name: 'API_BASE_URL', value: 'https://new.example.com', isSecret: false });
  });

  test('21.4 — deletes a plain variable after confirm', async ({ page }) => {
    const id = 'g-del';
    let rows = [variableJson({ id, name: 'OBSOLETE_FLAG', value: 'true', isSecret: false })];
    let deleteHit = false;

    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/global-variables/${id}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        rows = [];
        return route.fulfill({ status: 204 });
      }
      return route.fallback();
    });

    await page.goto('/global-variables');
    await expect(page.getByText('OBSOLETE_FLAG')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /^delete$|^löschen$/i }).click();
    // Confirm via the in-app ConfirmHost dialog (native confirm() was retired).
    await page.getByRole('button', { name: 'OK' }).click();
    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText('OBSOLETE_FLAG')).toHaveCount(0);
  });

  test('21.5 — search filters the variables list; a non-matching term shows the empty state', async ({ page }) => {
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          variableJson({ id: 'g-api', name: 'API_BASE_URL', value: 'https://api.example.com' }),
          variableJson({ id: 'g-smtp', name: 'SMTP_HOST', value: 'mail.example.com' }),
        ]),
      }),
    );

    await page.goto('/global-variables');
    await expect(page.getByText('API_BASE_URL')).toBeVisible({ timeout: 15_000 });

    const search = page.getByPlaceholder(/search by name, value, or description|name, wert/i);
    await search.fill('SMTP');
    await expect(page.getByText('SMTP_HOST')).toBeVisible();
    await expect(page.getByText('API_BASE_URL')).toHaveCount(0);

    await search.fill('zzz-nothing');
    await expect(page.getByText(/no variable matches the current search|keine variable/i)).toBeVisible();
  });

  test('21.6 — selecting a subfolder scopes the list to that folder', async ({ page }) => {
    // Root + one subfolder; a variable in each. Root (default selection) is descendant-inclusive.
    await page.route('**/api/global-variable-folders', (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          folderJson({ variableCount: 1 }),
          folderJson({ id: 'f-db', parentFolderId: ROOT_FOLDER, name: 'Databases', path: '/Databases', depth: 1, variableCount: 1 }),
        ]),
      });
    });
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          variableJson({ id: 'g-root', name: 'ROOT_VAR', value: 'r', folderId: ROOT_FOLDER }),
          variableJson({ id: 'g-db', name: 'DB_VAR', value: 'd', folderId: 'f-db' }),
        ]),
      }),
    );

    await page.goto('/global-variables');
    // Root selected → both rows visible (descendant-inclusive).
    await expect(page.getByText('ROOT_VAR')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('DB_VAR')).toBeVisible();

    // Click the "Databases" subfolder → list scopes to it.
    await page.getByTestId('global-folder-f-db').click();
    await expect(page.getByText('ROOT_VAR')).toHaveCount(0);
    await expect(page.getByText('DB_VAR')).toBeVisible();
  });

  test('21.7 — Admin creates a folder via the tree (POST body assertion)', async ({ page }) => {
    let postedBody: Record<string, unknown> | null = null;
    // Seed an existing subfolder so the panel is a few rows tall — otherwise the corner
    // resize-handle (absolute bottom-right) overlaps the Root row's "+" in a one-row panel.
    const existingFolders = [
      folderJson({ variableCount: 0 }),
      folderJson({ id: 'f-existing', parentFolderId: ROOT_FOLDER, name: 'Existing', path: '/Existing', depth: 1 }),
    ];
    await page.route('**/api/global-variable-folders', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        return route.fulfill({
          status: 201, contentType: 'application/json',
          body: JSON.stringify(folderJson({ id: 'f-new', parentFolderId: ROOT_FOLDER, name: (postedBody as { name: string }).name, path: '/Databases', depth: 1 })),
        });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(existingFolders) });
    });
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }),
    );

    await page.goto('/global-variables');
    const rootRow = page.getByTestId(`global-folder-${ROOT_FOLDER}`);
    await expect(rootRow).toBeVisible({ timeout: 15_000 });

    // The + on the Root row opens an inline "new subfolder" input.
    await rootRow.getByRole('button', { name: /create subfolder|unterordner anlegen/i }).click();
    await page.getByTestId('global-folder-create-input').fill('Databases');
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({ name: 'Databases', parentFolderId: ROOT_FOLDER });
  });
});
