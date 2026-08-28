import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Part 21 — Global Variables (E2ETests.md).
 *
 * Admin-managed constants exposed to workflows via `{{globals.NAME}}`. The page lives at
 * /global-variables (GlobalVariablesPage). Admin can list and mutate, Operator and Viewer are
 * read-only. Secret values render as `***` and are never sent to the client.
 *
 * There is no backend: every call is mocked per test with page.route over the hermetic
 * catch-all, and selectors are bilingual. The create/edit dialog is role="presentation", so
 * scope to the parent element of its heading.
 */

// Root folder sentinel, mirrors GlobalVariableFolder.RootFolderId.
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
    // The page queries the folder tree on mount and filters the list by folder, so always mock it.
    // The default is Root alone; tests that need subfolders override this route.
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
          // The server returns a secret value masked as "***", never the cleartext.
          variableJson({ id: 'g-secret', name: 'API_KEY', value: '***', isSecret: true }),
        ]),
      }),
    );

    await page.goto('/global-variables');

    await expect(page.getByText('API_BASE_URL')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('https://api.example.com')).toBeVisible();
    await expect(page.getByText('API_KEY')).toBeVisible();
    // The type badge wraps an icon and the label text, so match the text as a substring.
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
    // The name input carries the placeholder "MY_CONSTANT".
    await panel.getByPlaceholder('MY_CONSTANT').fill('API_BASE_URL');
    // For a non-secret variable the value field is a plain text input, the second in the panel.
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

    // An Operator can read the list, because workflows reference these values.
    await expect(page.getByText('READ_ONLY_VAR')).toBeVisible({ timeout: 15_000 });
    // Mutation is blocked in the UI: canAdmin gates every write control.
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
        // The server stores the secret DPAPI-encrypted and returns it masked as "***".
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
    // Toggle the secret checkbox first: it flips the value input to type=password.
    await panel.getByRole('checkbox').check();
    // A password input has no textbox role, so select the value field by its type.
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
    // An untouched secret sends value === null, which tells the server to keep the ciphertext.
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
    // For a plain variable the value field is the second textbox, prefilled from the row.
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
    // Confirmation runs through the in-app ConfirmHost dialog, not a native confirm().
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
    // Root plus one subfolder, with a variable in each. Root is the default selection.
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
    // With Root selected both rows are visible, because the selection includes descendants.
    await expect(page.getByText('ROOT_VAR')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('DB_VAR')).toBeVisible();

    // Clicking the "Databases" subfolder scopes the list to that folder.
    await page.getByTestId('global-folder-f-db').click();
    await expect(page.getByText('ROOT_VAR')).toHaveCount(0);
    await expect(page.getByText('DB_VAR')).toBeVisible();
  });

  test('21.7 — Admin creates a folder via the tree (POST body assertion)', async ({ page }) => {
    let postedBody: Record<string, unknown> | null = null;
    // Seed an existing subfolder so the panel is a few rows tall. In a one-row panel the corner
    // resize handle sits over the "+" button on the Root row.
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
  test('21.8 - deleting a non-empty folder removes it with its contents', async ({ page }) => {
    // Deleting a non-empty folder is safe only because the confirmation names what it removes,
    // so that is what this test asserts.
    await page.route('**/api/global-variable-folders', (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          folderJson(),
          folderJson({ id: 'f-db', parentFolderId: ROOT_FOLDER, name: 'Databases', path: '/Databases', depth: 1, variableCount: 3 }),
        ]),
      });
    });
    let recursiveUrl: string | null = null;
    await page.route('**/api/global-variable-folders/f-db*', (route) => {
      if (route.request().method() !== 'DELETE') return route.fallback();
      recursiveUrl = route.request().url();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ deletedFolders: 1, deletedVariables: 3 }),
      });
    });
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }),
    );

    await page.goto('/global-variables');
    const dbRow = page.getByTestId('global-folder-f-db');
    await expect(dbRow).toBeVisible({ timeout: 15_000 });

    await dbRow.click({ button: 'right' });
    await page.getByTestId('shared-folder-menu-delete').click();

    await expect(page.getByTestId('confirm-details')).toContainText('/Databases');
    await page.getByRole('button', { name: /^(OK|Delete|Löschen)/ }).click();

    await expect.poll(() => recursiveUrl, { timeout: 10_000 }).toContain('recursive=true');
    await expect(page.getByTestId('toast-success')).toBeVisible({ timeout: 10_000 });
  });

  test('21.9 - selecting two folders deletes each with one DELETE, after a single confirm', async ({ page }) => {
    await page.route('**/api/global-variable-folders', (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          folderJson(),
          folderJson({ id: 'f-db', parentFolderId: ROOT_FOLDER, name: 'Databases', path: '/Databases', depth: 1, variableCount: 1 }),
          folderJson({ id: 'f-keys', parentFolderId: ROOT_FOLDER, name: 'Keys', path: '/Keys', depth: 1, variableCount: 2 }),
        ]),
      });
    });
    const deleted: string[] = [];
    await page.route('**/api/global-variable-folders/*', (route) => {
      if (route.request().method() !== 'DELETE') return route.fallback();
      deleted.push(new URL(route.request().url()).pathname.split('/').pop()!);
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ deletedFolders: 1, deletedVariables: 1 }),
      });
    });
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }),
    );

    await page.goto('/global-variables');
    await expect(page.getByTestId('global-folder-f-db')).toBeVisible({ timeout: 15_000 });

    await page.getByTestId('global-folder-select-f-db').check();
    await page.getByTestId('global-folder-select-f-keys').check();
    await expect(page.getByTestId('folder-bulk-bar')).toBeVisible();

    await page.getByTestId('folder-bulk-delete').click();
    // Scope to the dialog: the bulk bar has a "Delete" button of its own, and ModalShell
    // exposes no dialog role to select on.
    const dialog = page.getByTestId('confirm-details').locator('..');
    await dialog.getByRole('button', { name: /^(OK|Delete|Löschen)/ }).click();

    // Both folders are siblings and therefore top-most: one DELETE each, and a single dialog.
    await expect.poll(() => deleted.length, { timeout: 10_000 }).toBe(2);
    expect(deleted).toEqual(expect.arrayContaining(['f-db', 'f-keys']));
  });

  test('21.10 - multi-selecting variables deletes each with its own request', async ({ page }) => {
    // No batch endpoint: every variable keeps its own authorization check and audit row.
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          variableJson({ id: 'g-1', name: 'ALPHA', value: 'a' }),
          variableJson({ id: 'g-2', name: 'BRAVO', value: 'b' }),
        ]),
      }),
    );
    const deleted: string[] = [];
    await page.route('**/api/global-variables/*', (route) => {
      if (route.request().method() !== 'DELETE') return route.fallback();
      deleted.push(new URL(route.request().url()).pathname.split('/').pop()!);
      return route.fulfill({ status: 204, body: '' });
    });

    await page.goto('/global-variables');
    await expect(page.getByText('ALPHA')).toBeVisible({ timeout: 15_000 });

    await page.getByTestId('variable-select-g-1').check();
    await page.getByTestId('variable-select-g-2').check();
    await expect(page.getByTestId('variable-bulk-bar')).toBeVisible();

    await page.getByTestId('variable-bulk-delete').click();
    // The confirmation names the variables; a bare count would not be checkable.
    await expect(page.getByTestId('confirm-details')).toContainText('ALPHA');
    const dialog = page.getByTestId('confirm-details').locator('..');
    await dialog.getByRole('button', { name: /^(OK|Delete|Löschen)/ }).click();

    await expect.poll(() => deleted.length, { timeout: 10_000 }).toBe(2);
    expect(deleted).toEqual(expect.arrayContaining(['g-1', 'g-2']));
  });
});
