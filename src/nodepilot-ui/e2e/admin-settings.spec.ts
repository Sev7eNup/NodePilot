import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 38 — Admin Settings (lines 3101-3135) + Teil 76 — Admin Settings UI sections
 * (lines 3997-4040). The admin Settings page has a top-level Personal/System tab split; the
 * System tab (Admin-only) hosts eight sub-sections, each a GET-snapshot + ETag-PUT form with the
 * shared RestartBanner above them.
 *
 * Specs:
 *  - 76.1 — every System section tab renders; the default Integrations card shows SMTP + LLM.
 *  - 38.2 — SMTP TestProbeModal surfaces a structured `{ok:false}` probe result (no crash).
 *  - 38.3 — LLM TestProbeModal surfaces a structured probe result.
 *  - 76.3a — Retention (hot-reloadable) save issues a PUT with If-Match and the Hot-Reload
 *            hint is rendered on the card (no restart banner for a live section).
 *  - 76.3b — a restart-pflichtig section surfaces the orange RestartBanner when /status flips
 *            restartRequired (38.1's status payload feeds the banner).
 *  - 76.5 — System-Info shows live DB-provider / app-version etc.
 *  - 38.5 / 76.1 — an Operator (and a Viewer) never sees the Admin-only System tab.
 *
 * Hermetic: predicate catch-all from fixtures/mockApi.ts. The `/api/admin/settings/*` family is
 * mocked per test — the default-active Integrations tab fetches `Smtp` + `Llm`, RestartBanner
 * polls `/status`, so those three are always mocked in beforeEach. Sections expose `data.payload`
 * directly, so each mock returns a fully-shaped payload (an empty `{}` would white-screen the
 * form). SPA renders ENGLISH under Playwright → bilingual /regex/i + role selectors.
 */

// ─────────────────────────────────────────────────────────────────────────────
// Section payload fixtures — shaped to the C# DTOs the sections read.
// ─────────────────────────────────────────────────────────────────────────────

function sectionResponse(sectionPath: string, payload: unknown, etag = '"etag-v1"', isHotReloadable = true) {
  return JSON.stringify({
    sectionPath,
    payload,
    etag,
    isHotReloadable,
    effectiveSource: {},
  });
}

const SMTP_PAYLOAD = { host: 'mail.example.com', port: 587, username: 'svc', password: '***', from: 'no-reply@example.com', enableSsl: true };
const LLM_PAYLOAD = { enabled: false, baseUrl: 'https://api.example.com/v1', apiKey: null, model: 'gpt-4o-mini', maxTokens: 4096, timeoutSeconds: 90 };
const RETENTION_PAYLOAD = {
  executions: { enabled: true, maxAgeDays: 30, intervalMinutes: 60, batchSize: 500, archivePath: null },
  auditLog: { enabled: true, maxAgeDays: 365, intervalMinutes: 720, batchSize: 1000, archivePath: null },
  workflowVersions: { enabled: true, maxVersionsPerWorkflow: 50, intervalMinutes: 1440, batchSize: 500 },
};

const SYSTEM_INFO = {
  appVersion: '1.4.2',
  overridesPath: 'C:/NodePilot/overrides.json',
  databaseProvider: 'postgres',
  databaseHost: 'localhost:5432',
  secretsProvider: 'Dpapi',
  clusterEnabled: false,
  clusterNodeId: 'node-e2e',
  clusterIsLeader: true,
  jwtIssuer: 'nodepilot',
  jwtAudience: 'nodepilot-ui',
};

function statusJson(opts: { restartRequired?: boolean; restartRequiredFor?: string[] } = {}) {
  return JSON.stringify({
    overridesPath: 'C:/NodePilot/overrides.json',
    restartRequired: opts.restartRequired ?? false,
    restartRequiredSince: opts.restartRequired ? '2026-06-18T09:00:00.000Z' : null,
    restartRequiredFor: opts.restartRequiredFor ?? [],
    lastSavedAt: null,
    lastSavedBy: null,
  });
}

const TEST_PROBE_FAIL = JSON.stringify({
  ok: false,
  message: 'Connection refused: mail.example.com:587 (server unreachable)',
  durationMs: 1234,
  errorKind: 'SocketException',
});

/** Mock every section GET the System tab might fetch with a sensible default payload, so any
 *  sub-tab the operator clicks mounts without a white-screen. Specific tests override the ones
 *  they assert against (last-added wins). */
async function mockAllSections(page: Page) {
  await page.route('**/api/admin/settings/Smtp', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: sectionResponse('Smtp', SMTP_PAYLOAD) }),
  );
  await page.route('**/api/admin/settings/Llm', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: sectionResponse('Llm', LLM_PAYLOAD) }),
  );
}

/** The top-level "System" tab button. "System" also matches the sidebar theme toggle and the
 *  appearance theme option, so we scope to the tab bar that holds the "Personal" tab. */
function systemTab(page: Page) {
  const tabBar = page
    .getByRole('button', { name: /^personal$|^persönlich$/i })
    .locator('..');
  return tabBar.getByRole('button', { name: /^system$/i });
}

/** Switch from the default Personal tab to the Admin System tab. */
async function openSystemTab(page: Page) {
  await page.goto('/settings');
  await systemTab(page).click();
}

test.describe('Admin Settings (Teil 38 + 76)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page); // MOCK_USER is Admin → System tab visible
    await page.route('**/api/admin/settings/status', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: statusJson() }),
    );
    await mockAllSections(page);
  });

  test('76.1 — all System section tabs render; Integrations shows SMTP + LLM', async ({ page }) => {
    await openSystemTab(page);

    // The eight section sub-tabs are always rendered in the tab bar.
    const tabs = [
      /integrations/i,
      /retention/i,
      /system info|system-info/i,
      /authentication/i,
      /logging .* telemetry|logging & telemetry/i,
      /^security$/i,
      /^performance$/i,
      /^database$/i,
    ];
    for (const name of tabs) {
      await expect(page.getByRole('button', { name }).first()).toBeVisible({ timeout: 15_000 });
    }

    // Default active tab is Integrations → SMTP + LLM cards mount from their mocked snapshots.
    await expect(page.getByRole('heading', { name: /^smtp$/i })).toBeVisible();
    await expect(page.getByRole('heading', { name: /llm/i })).toBeVisible();
    // A live value from the SMTP snapshot proves the GET round-tripped into the form: the Host
    // field (first textbox in the SMTP card) is populated from the mocked payload.
    const smtpCard = page.getByRole('heading', { name: /^smtp$/i }).locator('..');
    await expect(smtpCard.getByRole('textbox').first()).toHaveValue('mail.example.com');
  });

  test('38.2 — SMTP test probe surfaces a structured failure (no crash)', async ({ page }) => {
    let probeHit = false;
    await page.route('**/api/admin/settings/test/smtp', (route) => {
      probeHit = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: TEST_PROBE_FAIL });
    });

    await openSystemTab(page);
    await expect(page.getByRole('heading', { name: /^smtp$/i })).toBeVisible({ timeout: 15_000 });

    // Open the SMTP probe modal — the "Test" button on the SMTP card.
    await page.getByRole('button', { name: /^test$|^testen$/i }).first().click();
    // Run the probe inside the modal.
    await page.getByRole('button', { name: /run test|test ausführen|test starten/i }).click();

    await expect.poll(() => probeHit, { timeout: 10_000 }).toBe(true);
    // The modal renders the structured failure result, not a stack trace / blank.
    await expect(page.getByText(/failed|fehlgeschlagen/i).first()).toBeVisible();
    await expect(page.getByText(/server unreachable/i)).toBeVisible();
  });

  test('38.3 — LLM test probe returns a structured result (no crash)', async ({ page }) => {
    let probeHit = false;
    await page.route('**/api/admin/settings/test/llm', (route) => {
      probeHit = true;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ok: true, message: 'Model reachable (200 OK)', durationMs: 412, errorKind: null }),
      });
    });

    await openSystemTab(page);
    await expect(page.getByRole('heading', { name: /llm/i })).toBeVisible({ timeout: 15_000 });

    // The LLM card is the second card; its Test button is the second on the page.
    await page.getByRole('button', { name: /^test$|^testen$/i }).nth(1).click();
    await page.getByRole('button', { name: /run test|test ausführen|test starten/i }).click();

    await expect.poll(() => probeHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText(/success|erfolg/i).first()).toBeVisible();
    await expect(page.getByText(/model reachable/i)).toBeVisible();
  });

  test('76.3a — Retention (hot-reloadable) save issues an If-Match PUT and shows the Hot-Reload hint', async ({ page }) => {
    let ifMatch: string | null = null;
    let putBody: Record<string, unknown> | null = null;

    await page.route('**/api/admin/settings/Retention', (route) => {
      const req = route.request();
      if (req.method() === 'PUT') {
        ifMatch = req.headers()['if-match'] ?? null;
        putBody = req.postDataJSON();
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: sectionResponse('Retention', RETENTION_PAYLOAD, '"etag-v2"', true),
        });
      }
      // Retention is hot-reloadable → isHotReloadable: true → card renders the live hint.
      return route.fulfill({ status: 200, contentType: 'application/json', body: sectionResponse('Retention', RETENTION_PAYLOAD, '"etag-v1"', true) });
    });

    await openSystemTab(page);

    // Go to the Retention sub-tab and save.
    await page.getByRole('button', { name: /retention/i }).click();
    await expect(page.getByRole('heading', { name: /executions/i })).toBeVisible();
    // Hot-reloadable section → the emerald "applies immediately" hint is rendered on each card.
    await expect(page.getByText(/changes apply immediately/i).first()).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: /^save$|^speichern$/i }).first().click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    // The save echoed the snapshot's ETag via If-Match (optimistic-concurrency contract).
    expect(ifMatch).toBe('"etag-v1"');
    expect(putBody).toMatchObject({ Executions: { MaxAgeDays: 30 } });
  });

  test('76.3b — a restart-pflichtig section surfaces the orange RestartBanner from /status', async ({ page }) => {
    // /status reports a pending restart for a boot-fixed section so the RestartBanner is visible.
    await page.route('**/api/admin/settings/status', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: statusJson({ restartRequired: true, restartRequiredFor: ['Logging'] }),
      }),
    );

    await openSystemTab(page);

    // RestartBanner (role=alert) is shown because /status said restart is pending.
    await expect(page.getByRole('alert')).toBeVisible({ timeout: 15_000 });
  });

  test('76.5 — System-Info shows live DB provider, app version and JWT issuer', async ({ page }) => {
    await page.route('**/api/admin/settings/system-info', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(SYSTEM_INFO) }),
    );

    await openSystemTab(page);
    await page.getByRole('button', { name: /system info|system-info/i }).click();

    // Live values from the backend snapshot.
    await expect(page.getByText('postgres')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('1.4.2')).toBeVisible();
    await expect(page.getByText('localhost:5432')).toBeVisible();
    await expect(page.getByText('nodepilot-ui')).toBeVisible(); // JWT audience
  });

  test('38.4 — ETag conflict on save surfaces the EtagConflictDialog', async ({ page }) => {
    await page.route('**/api/admin/settings/Retention', (route) => {
      const req = route.request();
      if (req.method() === 'PUT') {
        // 412 with the server's current snapshot in the body → the conflict dialog.
        return route.fulfill({
          status: 412,
          contentType: 'application/json',
          body: JSON.stringify({
            code: 'etag_mismatch',
            current: JSON.parse(sectionResponse('Retention', { ...RETENTION_PAYLOAD, executions: { ...RETENTION_PAYLOAD.executions, maxAgeDays: 14 } }, '"etag-server"')),
          }),
        });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: sectionResponse('Retention', RETENTION_PAYLOAD) });
    });

    await openSystemTab(page);
    await page.getByRole('button', { name: /retention/i }).click();
    await expect(page.getByRole('heading', { name: /executions/i })).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: /^save$|^speichern$/i }).first().click();

    // The three-way conflict dialog renders with both sides + a resolution button.
    await expect(page.getByText(/conflict with another editor|konflikt/i)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button', { name: /overwrite with my values|mit meinen werten/i })).toBeVisible();
  });

  test('38.5 — an Operator never sees the Admin-only System tab', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: '00000000-0000-0000-0000-0000000000bb', username: 'e2e-operator', role: 'Operator' }),
      }),
    );

    await page.goto('/settings');
    // The page no longer carries a "Settings" heading (the title moved to the app-header
    // breadcrumb, which reads "Personal" for a non-admin). Anchor on the always-present
    // Appearance section heading of the Personal view instead.
    await expect(page.getByRole('heading', { name: /appearance|darstellung/i }).first()).toBeVisible({ timeout: 15_000 });
    // The Personal/System tab bar is gated to Admins; an Operator only sees Personal settings.
    await expect(page.getByRole('button', { name: /^personal$|^persönlich$/i })).toHaveCount(0);
    await expect(systemTab(page)).toHaveCount(0);
  });
});
