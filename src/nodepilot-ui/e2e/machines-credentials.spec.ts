import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Part 20 — Machines & Credentials (E2ETests.md ~2008-2072).
 *
 * Machines live on /machines (MachinesPage); Credentials are managed inline on the
 * /settings page (PersonalSettings section). No real backend — every /api call is a
 * per-test page.route mock layered over the hermetic catch-all in fixtures/mockApi.ts.
 *
 * Locale: the SPA renders EN under Playwright but may render DE on other machines, so
 * every visible-text selector is bilingual. Credential/machine dialogs use
 * role="presentation" (not role="dialog"), so we scope by the dialog heading's parent.
 *
 * Test 20.1 — create a machine + Test-Connection (mocked WinRM result, success & failure).
 * Test 20.2 — create a credential, UI masks the password (never echoed back).
 * Test 20.3 — delete a credential (confirm). The dependency-warning / force-delete flow
 *             described in E2ETests.md is NOT implemented in the UI, so only the plain
 *             confirm+DELETE round-trip is covered (see PARTIAL note in the report).
 */

const CRED_ID = 'cccccccc-0000-0000-0000-000000000001';

function machineJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'mmmmmmmm-0000-0000-0000-000000000001',
    name: 'Test-Server-01',
    hostname: 'server01.local',
    winRmPort: 5985,
    useSsl: false,
    defaultCredentialId: null,
    tags: null,
    lastConnectivityCheck: null,
    isReachable: false,
    usedByWorkflowCount: 0,
    recentStepCount: 0,
    recentFailedStepCount: 0,
    activeRunCount: 0,
    ...overrides,
  };
}

function credentialJson(overrides: Record<string, unknown> = {}) {
  return {
    id: CRED_ID,
    name: 'DomainAdmin',
    username: 'admin',
    domain: 'DOMAIN',
    ...overrides,
  };
}

// The create/edit machine dialog has role="presentation". Its heading sits inside a header
// flex div, with the fields + footer as SIBLINGS of that header — so the dialog panel is the
// heading's grandparent (`../..`), which contains header, fields and the submit footer.
function machineCreatePanel(page: Page) {
  return page.getByRole('heading', { name: /add machine|maschine hinzufügen/i }).locator('../..');
}

test.describe('Teil 20 — Machines & Credentials', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('20.1a — renders the machines list', async ({ page }) => {
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          machineJson({ id: 'm-1', name: 'WEB-01', hostname: 'web01.local', winRmPort: 5985 }),
          machineJson({ id: 'm-2', name: 'DB-01', hostname: 'db01.local', winRmPort: 5986, useSsl: true, isReachable: true }),
        ]),
      }),
    );

    await page.goto('/machines');

    await expect(page.getByText('WEB-01')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('DB-01')).toBeVisible();
    // Address cell renders "hostname:port".
    await expect(page.getByText('web01.local:5985')).toBeVisible();
    await expect(page.getByText('db01.local:5986')).toBeVisible();
  });

  test('20.1b — creates a machine (round-trip with body assertion)', async ({ page }) => {
    const rows: ReturnType<typeof machineJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    // A credential so the "Default Credential" dropdown has a real option.
    await page.route('**/api/credentials', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([credentialJson()]),
      }),
    );
    await page.route('**/api/machines', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        const created = machineJson({ id: 'created-1', ...(postedBody as object) });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/machines');

    // Empty state first.
    await expect(page.getByText(/no machines configured|keine maschinen konfiguriert/i)).toBeVisible({
      timeout: 15_000,
    });

    await page.getByRole('button', { name: /add machine|maschine hinzufügen/i }).click();

    const panel = machineCreatePanel(page);
    await expect(panel).toBeVisible();
    // Fields are unlabeled-by-id; pick by placeholder.
    await panel.getByPlaceholder(/display name|anzeigename/i).fill('Test-Server-01');
    await panel.getByPlaceholder(/hostname or ip|hostname oder ip/i).fill('server01.local');
    // Pick the credential in the dropdown (round-trips defaultCredentialId).
    await panel.locator('select').selectOption(CRED_ID);

    // Submit button is "Add" / "Hinzufügen" — scope inside the panel & match exactly so it
    // doesn't collide with the "Add Machine" heading/header button.
    await panel.getByRole('button', { name: /^add$|^hinzufügen$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'Test-Server-01',
      hostname: 'server01.local',
      winRmPort: 5985,
      useSsl: false,
      defaultCredentialId: CRED_ID,
    });

    // Dialog closes; refetched list shows the new row.
    await expect(panel).toHaveCount(0);
    await expect(page.getByText('Test-Server-01')).toBeVisible();
  });

  test('20.1c — SSL toggle bumps the default port to 5986 in the create body', async ({ page }) => {
    let postedBody: Record<string, unknown> | null = null;
    await page.route('**/api/machines', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        return route.fulfill({
          status: 201,
          contentType: 'application/json',
          body: JSON.stringify(machineJson({ id: 'ssl-1', ...(postedBody as object) })),
        });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });

    await page.goto('/machines');
    await expect(page.getByText(/no machines configured|keine maschinen konfiguriert/i)).toBeVisible({
      timeout: 15_000,
    });

    await page.getByRole('button', { name: /add machine|maschine hinzufügen/i }).click();
    const panel = machineCreatePanel(page);
    await panel.getByPlaceholder(/display name|anzeigename/i).fill('Secure-01');
    await panel.getByPlaceholder(/hostname or ip|hostname oder ip/i).fill('secure01.local');
    // The transport button shows its CURRENT state: it reads "HTTP" while SSL is off. Clicking
    // it toggles SSL on (and the label flips to "HTTPS"), bumping the default port 5985 -> 5986.
    await panel.getByRole('button', { name: /^http$/i }).click();
    await expect(panel.getByRole('button', { name: /^https$/i })).toBeVisible();
    await panel.getByRole('button', { name: /^add$|^hinzufügen$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({ name: 'Secure-01', useSsl: true, winRmPort: 5986 });
  });

  test('20.1d — Test Connection shows Online on a successful WinRM result', async ({ page }) => {
    let testHit = false;
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([machineJson({ id: 'm-ok', name: 'PING-OK' })]),
      }),
    );
    // Mock the WinRM probe's RESPONSE — no real connection.
    await page.route(`**/api/machines/m-ok/test`, (route) => {
      testHit = true;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ success: true, computerName: 'PING-OK' }),
      });
    });

    await page.goto('/machines');
    await expect(page.getByText('PING-OK')).toBeVisible({ timeout: 15_000 });

    // Test button is icon-only with a title attribute.
    await page.getByTitle(/test connection|verbindung testen/i).click();

    await expect.poll(() => testHit).toBe(true);
    await expect(page.getByText(/^online$/i).first()).toBeVisible();
  });

  test('20.1e — Test Connection surfaces a failure message', async ({ page }) => {
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([machineJson({ id: 'm-bad', name: 'PING-FAIL' })]),
      }),
    );
    await page.route(`**/api/machines/m-bad/test`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ success: false, error: 'WinRM auth failed (timeout)' }),
      }),
    );

    await page.goto('/machines');
    await expect(page.getByText('PING-FAIL')).toBeVisible({ timeout: 15_000 });
    await page.getByTitle(/test connection|verbindung testen/i).click();

    // Status badge flips to Failed; the error message rides along as the badge title.
    await expect(page.getByText(/^failed$/i).first()).toBeVisible();
    await expect(page.getByTitle('WinRM auth failed (timeout)')).toBeVisible();
  });

  test('20.1f — Viewer sees no machine write controls', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }),
      }),
    );
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([machineJson({ id: 'ro-1', name: 'ReadOnly-Box' })]),
      }),
    );

    await page.goto('/machines');
    await expect(page.getByText('ReadOnly-Box')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /add machine|maschine hinzufügen/i })).toHaveCount(0);
    await expect(page.getByTitle(/test connection|verbindung testen/i)).toHaveCount(0);
  });

  test('20.2 — creates a credential; password is never echoed back to the UI', async ({ page }) => {
    const rows: ReturnType<typeof credentialJson>[] = [];
    let postedBody: Record<string, unknown> | null = null;

    await page.route('**/api/credentials', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        // Server returns the credential WITHOUT the password (Credential type has no password field).
        const created = credentialJson({
          id: 'new-cred',
          name: (postedBody as { name: string }).name,
          username: (postedBody as { username: string }).username,
          domain: (postedBody as { domain: string | null }).domain,
        });
        rows.push(created);
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/settings');

    // Credentials section header.
    await expect(page.getByRole('heading', { name: /^credentials$/i })).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: /add credential|credential hinzufügen/i }).click();

    await page.getByPlaceholder(/credential name|name des credentials/i).fill('DomainAdmin');
    await page.getByPlaceholder(/^username$|^benutzername$/i).fill('DOMAIN\\admin');
    await page.getByPlaceholder(/^password$|^passwort$/i).fill('ValidPass123!');
    await page.getByRole('button', { name: /^save$|^speichern$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'DomainAdmin',
      username: 'DOMAIN\\admin',
      password: 'ValidPass123!',
    });

    // New credential appears; the rendered row shows name + username only — the password
    // never round-trips back (Credential DTO carries no password field).
    await expect(page.getByText('DomainAdmin')).toBeVisible();
    await expect(page.getByText(/DOMAIN\\admin/)).toBeVisible();
    await expect(page.getByText('ValidPass123!')).toHaveCount(0);
  });

  test('20.3 — deletes a credential after confirm (in-app confirm+DELETE)', async ({ page }) => {
    const id = CRED_ID;
    let rows = [credentialJson({ id, name: 'ToDelete' })];
    let deleteHit = false;

    await page.route('**/api/credentials', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/credentials/${id}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        rows = [];
        return route.fulfill({ status: 204 });
      }
      return route.fallback();
    });

    await page.goto('/settings');
    await expect(page.getByText('ToDelete')).toBeVisible({ timeout: 15_000 });

    // The delete button is the trash icon in the credential row (icon-only, no title) —
    // it is the last button in the .np-row that contains "ToDelete" (the pencil edit
    // button precedes it and carries an aria-label).
    await page.locator('.np-row', { hasText: 'ToDelete' }).getByRole('button').last().click();
    // In-app ConfirmHost modal — confirm with the (default) OK button.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect.poll(() => deleteHit).toBe(true);
    await expect(page.getByText('ToDelete')).toHaveCount(0);
  });

  test('20.3b — Viewer sees no credential write/delete controls', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }),
      }),
    );
    await page.route('**/api/credentials', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([credentialJson({ id: 'ro-c', name: 'ReadOnlyCred' })]),
      }),
    );

    await page.goto('/settings');
    await expect(page.getByText('ReadOnlyCred')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /add credential|credential hinzufügen/i })).toHaveCount(0);
    // No delete trash button inside the credential row for a Viewer.
    await expect(page.getByText('ReadOnlyCred').locator('../..').getByRole('button')).toHaveCount(0);
  });

  test('20.1g — editing a machine PUTs the changed fields and the row updates', async ({ page }) => {
    const id = 'm-edit';
    const rows = [machineJson({ id, name: 'OLD-NAME', hostname: 'old.local', winRmPort: 5985 })];
    let putBody: Record<string, unknown> | null = null;

    await page.route('**/api/machines', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/machines/${id}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        rows[0] = { ...rows[0], ...(putBody as object) };
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows[0]) });
      }
      return route.fallback();
    });

    await page.goto('/machines');
    await expect(page.getByText('OLD-NAME')).toBeVisible({ timeout: 15_000 });

    // Open the edit dialog (Pencil button, title "Edit").
    await page.getByTitle(/^edit$|^bearbeiten$/i).click();
    const panel = page.getByRole('heading', { name: /edit machine|maschine bearbeiten/i }).locator('../..');
    await expect(panel).toBeVisible();
    await expect(panel.getByPlaceholder(/display name|anzeigename/i)).toHaveValue('OLD-NAME');
    await panel.getByPlaceholder(/display name|anzeigename/i).fill('NEW-NAME');
    await panel.getByRole('button', { name: /^save$|^speichern$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ name: 'NEW-NAME', hostname: 'old.local', winRmPort: 5985, useSsl: false });

    await expect(panel).toHaveCount(0);
    await expect(page.getByText('NEW-NAME')).toBeVisible();
  });

  test('20.1h — deletes a machine after confirm (confirm+DELETE)', async ({ page }) => {
    const id = 'm-del';
    let rows = [machineJson({ id, name: 'DECOMMISSIONED' })];
    let deleteHit = false;

    await page.route('**/api/machines', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/machines/${id}`, (route) => {
      if (route.request().method() === 'DELETE') {
        deleteHit = true;
        rows = [];
        return route.fulfill({ status: 204, body: '' });
      }
      return route.fallback();
    });

    await page.goto('/machines');
    await expect(page.getByText('DECOMMISSIONED')).toBeVisible({ timeout: 15_000 });

    await page.getByTitle(/^delete$|^löschen$/i).click();
    // In-app ConfirmHost modal — confirm with the (default) OK button.
    await page.getByRole('button', { name: 'OK' }).click();
    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText('DECOMMISSIONED')).toHaveCount(0);
  });

  test('20.4 — search filters the machine list by name/hostname', async ({ page }) => {
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          machineJson({ id: 'm-web', name: 'WEB-01', hostname: 'web01.local' }),
          machineJson({ id: 'm-db', name: 'DB-01', hostname: 'db01.local' }),
        ]),
      }),
    );

    await page.goto('/machines');
    await expect(page.getByText('WEB-01')).toBeVisible({ timeout: 15_000 });

    await page.getByPlaceholder(/search by name, hostname or tag|name, hostname/i).fill('db01');
    await expect(page.getByText('DB-01')).toBeVisible();
    await expect(page.getByText('WEB-01')).toHaveCount(0);
  });

  test('20.5 — a tag-filter chip narrows the list to machines carrying that tag', async ({ page }) => {
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          machineJson({ id: 'm-prod', name: 'PROD-01', tags: 'prod,web' }),
          machineJson({ id: 'm-dev', name: 'DEV-01', tags: 'dev' }),
        ]),
      }),
    );

    await page.goto('/machines');
    await expect(page.getByText('PROD-01')).toBeVisible({ timeout: 15_000 });

    // The "prod" chip exists both in the toolbar filter bar and in the PROD-01 row's tag cell;
    // clicking either applies the same filter. Take the first (toolbar).
    await page.getByRole('button', { name: 'prod' }).first().click();
    await expect(page.getByText('PROD-01')).toBeVisible();
    await expect(page.getByText('DEV-01')).toHaveCount(0);
  });

  test('20.6 — "Test all" probes every machine after confirm', async ({ page }) => {
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([
          machineJson({ id: 'm-a', name: 'NODE-A' }),
          machineJson({ id: 'm-b', name: 'NODE-B' }),
        ]),
      }),
    );
    const tested = new Set<string>();
    await page.route('**/api/machines/m-a/test', (route) => {
      tested.add('m-a');
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, computerName: 'NODE-A' }) });
    });
    await page.route('**/api/machines/m-b/test', (route) => {
      tested.add('m-b');
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, computerName: 'NODE-B' }) });
    });
    await page.goto('/machines');
    await expect(page.getByText('NODE-A')).toBeVisible({ timeout: 15_000 });

    await page.getByRole('button', { name: /test all|alle testen/i }).click();
    // testAllConfirm now renders as the in-app ConfirmHost modal — confirm via OK.
    await page.getByRole('button', { name: 'OK' }).click();
    await expect.poll(() => tested.size, { timeout: 10_000 }).toBe(2);
  });
});
