import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

// Mirrors `NotificationRuleStore.UnchangedSecret` on the backend (src/api/alerting.ts exports
// the same `'__unchanged__'`). Inlined here rather than imported from src/ so the spec doesn't
// pull the app module graph (api/client → auth/fetch wrappers) into the Playwright Node runtime
// — no other e2e spec imports from src/, and this keeps the test hermetic to /api mocks.
const UNCHANGED_SECRET = '__unchanged__';

/**
 * Teil 78 — Alerting (Custom rules + System policies).
 *
 * Hermetic Playwright spec for the /alerts page. Every /api call is a per-test `page.route`
 * mock layered over the predicate catch-all in fixtures/mockApi.ts — no backend, no Postgres,
 * no SignalR (delivery status comes from REST). The preview build renders the UI in English
 * (i18n falls back to EN); visible-text selectors are bilingual regex anchored on the EN
 * strings in src/i18n/locales/en/alerts.json + common.json, so a German render still matches.
 *
 * RBAC mirror of the AlertingController: read = Admin/Operator, mutate = Admin-only. The UI
 * gates every write affordance on `useRole().canAdmin` (Admin-only), so Viewer and Operator
 * both see the list + deliveries but no New/Power/Edit/Trash controls.
 *
 * Icon-only row buttons (Power/Edit/Trash) carry `title={t(...)}` → `getByTitle(regex)`.
 * The confirm dialog (ConfirmHost) renders an "OK" button → `getByRole('button', { name: /^ok$/i })`.
 * Editor modals use ModalShell (role="presentation"); scope to the heading's parent panel.
 */

// ---- mock-factory helpers ---------------------------------------------------------------

function ruleJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'rrrrrrrr-0000-0000-0000-000000000001',
    name: 'Disk Full',
    description: 'Fires when a volume crosses 90%',
    isEnabled: true,
    eventTypes: ['ExecutionFailed'],
    filterExpressionJson: null,
    scopeKind: 'Global',
    cooldownMinutes: 0,
    minOccurrences: 1,
    occurrenceWindowMinutes: 0,
    routes: [
      { id: 'rt-1', channel: 'Email', target: 'ops@example.com', secret: null, order: 0, conditionExpressionJson: null },
    ],
    targets: [],
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    updatedBy: null,
    dedupKeyTemplate: null,
    ...overrides,
  };
}

function catalogJson() {
  return {
    eventTypes: [
      { name: 'ExecutionFailed', category: 'execution', scopeable: false },
      { name: 'ExecutionSucceeded', category: 'execution', scopeable: false },
      { name: 'ExecutionCancelled', category: 'execution', scopeable: false },
    ],
    eventFields: [
      { name: 'eventType', applies: 'both', type: 'string' },
      { name: 'workflowName', applies: 'execution', type: 'string' },
    ],
    channels: ['Email', 'GenericWebhook'],
    dedupTemplateFields: ['eventType', 'workflowId', 'sourceKey'],
  };
}

function systemCatalogJson() {
  return {
    sources: [
      {
        sourceId: 'execution-result',
        category: 'Execution',
        scopeCapability: 'GlobalOnly',
        defaultSeverity: 'Warning',
        fields: [{ name: 'status', type: 'String', operators: ['=='], unit: null, enumValues: null }],
        parameters: [],
        presets: [
          { presetId: 'critical', severity: 'Critical', sustainForSeconds: 0, conditionJson: null, parameters: null },
        ],
        available: true,
      },
    ],
  };
}

function systemPolicyJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'ssssssss-0000-0000-0000-000000000001',
    name: 'Backlog critical',
    description: null,
    isEnabled: false,
    sourceId: 'execution-result',
    presetId: 'critical',
    sourceParameters: null,
    conditionJson: null,
    sustainForSeconds: 0,
    severityOverride: 'Critical',
    scopeKind: 'Global',
    targets: [],
    routes: [
      { id: 'srt-1', channel: 'Email', target: 'ops@example.com', secret: null, order: 0, conditionExpressionJson: null },
    ],
    cooldownMinutes: 0,
    minOccurrences: 1,
    occurrenceWindowMinutes: 0,
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    updatedBy: null,
    activatedAt: null,
    ...overrides,
  };
}

function deliveryJson(overrides: Record<string, unknown> = {}) {
  return {
    id: 'dddddddd-0000-0000-0000-000000000001',
    ruleId: 'rrrrrrrr-0000-0000-0000-000000000001',
    ruleName: 'Disk Full',
    routeId: 'rt-1',
    channel: 'Email',
    target: 'ops@example.com',
    eventKey: 'ExecutionFailed:wf-1',
    status: 'Sent',
    attempt: 1,
    createdAt: '2026-07-02T12:00:00Z',
    sentAt: '2026-07-02T12:00:01Z',
    error: null,
    isTest: false,
    summary: null,
    ...overrides,
  };
}

function testFireJson(opts: { allSucceeded?: boolean; error?: string | null } = {}) {
  return {
    allSucceeded: opts.allSucceeded ?? false,
    results: [
      {
        channel: 'Email',
        target: 'ops@example.com',
        success: opts.allSucceeded ?? false,
        error: opts.allSucceeded ? null : (opts.error ?? 'SMTP timeout'),
      },
    ],
  };
}

// ModalShell wraps the panel in role="presentation"; the h3 heading is a direct child of the
// panel div, so the heading's parent locator resolves to the editable panel.
function editorPanel(page: Page, headingRegex: RegExp) {
  return page.getByRole('heading', { name: headingRegex }).locator('..');
}

// ---- suite ------------------------------------------------------------------------------

test.describe('Teil 78 — Alerting', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // Custom-rule catalog (event types, channels, dedup fields).
    await page.route('**/api/alerting/catalog', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(catalogJson()) }),
    );
    // System-alert catalog (one Execution source with a preset).
    await page.route('**/api/alerting/system/catalog', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(systemCatalogJson()) }),
    );
    // Shared folders tree — empty (the scope Folders/Workflows pickers query this).
    await page.route('**/api/shared-workflow-folders', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
    // System policies list — empty by default; the CRUD test overrides this.
    await page.route('**/api/alerting/system/policies', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
  });

  // ---- Custom block -------------------------------------------------------------------

  test('78.1 — tab switch system↔custom, URL ?tab= persistence + deep-link empty state', async ({ page }) => {
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );

    // Default landing (no ?tab=) renders the System alerts tab.
    await page.goto('/alerts');
    await expect(page.getByRole('button', { name: /system alerts|system-alarme/i })).toBeVisible({ timeout: 15_000 });
    // System subtitle is shown only on the system tab.
    await expect(page.getByText(/catalog-driven|kataloggesteuerte/i)).toBeVisible();

    // Switch to Custom rules → URL carries ?tab=custom, empty state renders.
    await page.getByRole('button', { name: /custom rules|benutzerdefinierte regeln/i }).click();
    await expect(page).toHaveURL(/tab=custom/);
    await expect(page.getByText(/no alerting rules yet|noch keine alerting-regeln/i)).toBeVisible();

    // Switch back to System alerts → URL carries ?tab=system.
    await page.getByRole('button', { name: /system alerts|system-alarme/i }).click();
    await expect(page).toHaveURL(/tab=system/);
    await expect(page.getByText(/catalog-driven|kataloggesteuerte/i)).toBeVisible();

    // Deep-link ?tab=custom mounts the custom tab directly with the empty state.
    await page.goto('/alerts?tab=custom');
    await expect(page.getByText(/no alerting rules yet|noch keine alerting-regeln/i)).toBeVisible();
  });

  test('78.2 — custom-rule list renders rows with enabled/disabled badges', async ({ page }) => {
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          ruleJson({ id: 'r-on', name: 'Enabled-Rule', isEnabled: true }),
          ruleJson({ id: 'r-off', name: 'Disabled-Rule', isEnabled: false }),
        ]),
      }),
    );

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('Enabled-Rule')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('Disabled-Rule')).toBeVisible();
    // Enabled column renders Yes/No (common:yes / common:no).
    await expect(page.locator('tr', { hasText: 'Enabled-Rule' }).getByText(/^yes$|^ja$/i)).toBeVisible();
    await expect(page.locator('tr', { hasText: 'Disabled-Rule' }).getByText(/^no$|^nein$/i)).toBeVisible();
  });

  test('78.3 — create rule POSTs the expected body', async ({ page }) => {
    let postedBody: Record<string, unknown> | null = null;
    await page.route('**/api/alerting/rules', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postDataJSON();
        return route.fulfill({
          status: 201,
          contentType: 'application/json',
          body: JSON.stringify(ruleJson({ id: 'new-1', ...(postedBody as object) })),
        });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText(/no alerting rules yet|noch keine alerting-regeln/i)).toBeVisible({ timeout: 15_000 });

    // "New rule" button is gated canAdmin && tab===custom.
    await page.getByRole('button', { name: /new rule|neue regel/i }).click();
    const panel = editorPanel(page, /create alerting rule|alerting-regel erstellen/i);
    await expect(panel).toBeVisible();

    // Name field is the first text input in the editor; description is the second.
    await panel.locator('input[type="text"]').first().fill('Disk Full');
    // Default event type is ExecutionFailed (pre-selected); leave it.
    // Route target — the email placeholder identifies the route row input.
    await panel.getByPlaceholder(/ops@example.com/i).fill('ops@example.com');

    await panel.getByRole('button', { name: /^create$|^anlegen$/i }).click();

    await expect.poll(() => postedBody).not.toBeNull();
    expect(postedBody).toMatchObject({
      name: 'Disk Full',
      eventTypes: ['ExecutionFailed'],
      scopeKind: 'Global',
      filterExpressionJson: null,
      routes: [{ channel: 'Email', target: 'ops@example.com' }],
    });
    // Dialog closes on success.
    await expect(panel).toHaveCount(0);
  });

  test('78.4 — edit + delete with confirm (PUT + DELETE capture)', async ({ page }) => {
    const id = 'r-edit';
    let rows = [ruleJson({ id, name: 'EDIT-ME', isEnabled: true })];
    let putBody: Record<string, unknown> | null = null;
    let deleteHit = false;

    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) }),
    );
    await page.route(`**/api/alerting/rules/${id}`, (route) => {
      const method = route.request().method();
      if (method === 'PUT') {
        putBody = route.request().postDataJSON();
        rows = [ruleJson({ id, name: 'EDITED', ...(putBody as object) })];
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows[0]) });
      }
      if (method === 'DELETE') {
        deleteHit = true;
        rows = [];
        return route.fulfill({ status: 204, body: '' });
      }
      return route.fallback();
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('EDIT-ME')).toBeVisible({ timeout: 15_000 });

    // Edit — pencil icon, title "Edit".
    await page.locator('tr', { hasText: 'EDIT-ME' }).getByTitle(/^edit$|^bearbeiten$/i).click();
    const panel = editorPanel(page, /edit alerting rule|alerting-regel bearbeiten/i);
    await expect(panel).toBeVisible();
    await expect(panel.locator('input[type="text"]').first()).toHaveValue('EDIT-ME');
    await panel.locator('input[type="text"]').first().fill('EDITED');
    await panel.getByRole('button', { name: /^update$|^aktualisieren$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({ name: 'EDITED', scopeKind: 'Global' });
    await expect(panel).toHaveCount(0);
    await expect(page.getByText('EDITED')).toBeVisible();

    // Delete — trash icon, title "Delete"; confirm via in-app OK.
    await page.locator('tr', { hasText: 'EDITED' }).getByTitle(/^delete$|^löschen$/i).click();
    await page.getByRole('button', { name: /^ok$/i }).click();
    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText('EDITED')).toHaveCount(0);
  });

  test('78.5 — enable/disable toggle POSTs to .../enable', async ({ page }) => {
    const id = 'r-off';
    let enableHit = false;
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([ruleJson({ id, name: 'TOGGLE-ME', isEnabled: false })]),
      }),
    );
    await page.route(`**/api/alerting/rules/${id}/enable`, (route) => {
      enableHit = true;
      return route.fulfill({ status: 204, body: '' });
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('TOGGLE-ME')).toBeVisible({ timeout: 15_000 });

    // Disabled rule → power icon title is "Enable".
    await page.locator('tr', { hasText: 'TOGGLE-ME' }).getByTitle(/^enable$|^aktivieren$/i).click();
    await expect.poll(() => enableHit, { timeout: 10_000 }).toBe(true);
  });

  test('78.6 — test-fire renders partial result with error text', async ({ page }) => {
    const id = 'r-tf';
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([ruleJson({ id, name: 'TF-RULE', isEnabled: true })]),
      }),
    );
    await page.route(`**/api/alerting/rules/${id}/test-fire`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(testFireJson({ error: 'SMTP timeout' })) }),
    );

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('TF-RULE')).toBeVisible({ timeout: 15_000 });

    // Open the editor (Test notification button only renders in edit mode).
    await page.locator('tr', { hasText: 'TF-RULE' }).getByTitle(/^edit$|^bearbeiten$/i).click();
    const panel = editorPanel(page, /edit alerting rule|alerting-regel bearbeiten/i);
    await expect(panel).toBeVisible();

    await panel.getByRole('button', { name: /test notification|testbenachrichtigung/i }).click();
    // Partial-result header. (DE locale uses "Kanaele" — ae substitution, no umlaut.)
    await expect(panel.getByText(/some channels failed:|einige kanaele fehlgeschlagen:/i)).toBeVisible();
    // Route line echoes the error text.
    await expect(panel.getByText(/SMTP timeout/)).toBeVisible();
  });

  test('78.7 — preview-rule matches and shows the dedup key', async ({ page }) => {
    let previewBody: Record<string, unknown> | null = null;
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
    );
    await page.route('**/api/alerting/preview-rule', (route) => {
      previewBody = route.request().postDataJSON();
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          matchesRule: true,
          dedupKey: 'ExecutionFailed:wf-1',
          routes: [{ channel: 'Email', target: 'ops@example.com', matches: true }],
          reasons: [],
        }),
      });
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText(/no alerting rules yet|noch keine alerting-regeln/i)).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: /new rule|neue regel/i }).click();
    const panel = editorPanel(page, /create alerting rule|alerting-regel erstellen/i);
    await expect(panel).toBeVisible();

    // Form must be valid (name + route target) before the Preview button enables.
    await panel.locator('input[type="text"]').first().fill('Preview-Rule');
    await panel.getByPlaceholder(/ops@example.com/i).fill('ops@example.com');

    await panel.getByRole('button', { name: /^preview$/i }).click();

    await expect(panel.getByText(/preview matches the sample event|preview passt zum beispiel-ereignis/i)).toBeVisible();
    // Dedup key line is rendered verbatim.
    await expect(panel.getByText(/ExecutionFailed:wf-1/)).toBeVisible();
    await expect.poll(() => previewBody).not.toBeNull();
  });

  test('78.8 — deliveries modal + status filter (URL + row filtering)', async ({ page }) => {
    const failed = deliveryJson({ id: 'd-fail', status: 'Failed', target: 'ops@example.com', error: 'SMTP timeout' });
    const sent = deliveryJson({ id: 'd-sent', status: 'Sent', target: 'alert@example.com' });
    let deliveriesUrl = '';

    await page.route('**/api/alerting/deliveries**', (route) => {
      const url = route.request().url();
      deliveriesUrl = url;
      const rows = url.includes('status=Failed') ? [failed] : [failed, sent];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rows) });
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByRole('button', { name: /custom rules|benutzerdefinierte regeln/i })).toBeVisible({ timeout: 15_000 });

    // Open the deliveries modal from the header button (title "Delivery history").
    await page.getByTitle(/delivery history|zustell-verlauf/i).click();
    // DeliveriesModal wraps its <h3> in an extra flex-header bar (h3 + status <select>), unlike the
    // rule editor where the heading sits directly in the panel — so editorPanel's parent scope
    // would stop at the header bar and miss the table. Scope to the grandparent = the ModalShell
    // panel that contains both the header bar and the deliveries table.
    const modal = page.getByRole('heading', { name: /delivery history|zustell-verlauf/i }).locator('xpath=../..');
    await expect(modal).toBeVisible();

    // Unfiltered initial fetch shows both rows (rendered as `Email:<target>` text nodes).
    await expect(modal.getByText('ops@example.com')).toBeVisible();
    await expect(modal.getByText('alert@example.com')).toBeVisible();

    // Select "Failed" in the status combobox → re-query with ?status=Failed.
    await modal.locator('select').selectOption('Failed');
    await expect.poll(() => deliveriesUrl, { timeout: 10_000 }).toContain('status=Failed');
    // Only the failed row remains; the sent row is gone.
    await expect(modal.getByText('ops@example.com')).toBeVisible();
    await expect(modal.getByText('alert@example.com')).toHaveCount(0);
  });

  test('78.9 — secret redaction: stored placeholder, cleartext on PUT, never echoed', async ({ page }) => {
    const id = 'r-sec';
    let putBody: Record<string, unknown> | null = null;
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          ruleJson({
            id,
            name: 'WEBHOOK-RULE',
            routes: [
              { id: 'rt-wh', channel: 'GenericWebhook', target: 'https://hooks.example.com/ci', secret: UNCHANGED_SECRET, order: 0, conditionExpressionJson: null },
            ],
          }),
        ]),
      }),
    );
    await page.route(`**/api/alerting/rules/${id}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ruleJson({ id })) });
      }
      return route.fallback();
    });

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('WEBHOOK-RULE')).toBeVisible({ timeout: 15_000 });

    await page.locator('tr', { hasText: 'WEBHOOK-RULE' }).getByTitle(/^edit$|^bearbeiten$/i).click();
    const panel = editorPanel(page, /edit alerting rule|alerting-regel bearbeiten/i);
    await expect(panel).toBeVisible();

    // The webhook route renders a password input whose placeholder marks a stored secret.
    const secretInput = panel.locator('input[type="password"]');
    await expect(secretInput).toBeVisible();
    await expect(secretInput).toHaveAttribute('placeholder', /stored|gespeichert/i);

    // Type a new cleartext secret and save.
    await secretInput.fill('supersecret-value');
    await panel.getByRole('button', { name: /^update$|^aktualisieren$/i }).click();

    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    expect(putBody).toMatchObject({
      routes: [{ channel: 'GenericWebhook', target: 'https://hooks.example.com/ci', secret: 'supersecret-value' }],
    });
    // The cleartext must never appear as visible page text.
    await expect(page.getByText('supersecret-value')).toHaveCount(0);
  });

  // ---- RBAC ----------------------------------------------------------------------------

  test('78.10 — Viewer: row renders, no mutate controls, deliveries visible', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }) }),
    );
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([ruleJson({ id: 'ro-r', name: 'RO-RULE' })]) }),
    );

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('RO-RULE')).toBeVisible({ timeout: 15_000 });

    // No New / Power / Edit / Trash affordances.
    await expect(page.getByRole('button', { name: /new rule|neue regel/i })).toHaveCount(0);
    await expect(page.getByTitle(/^enable$|^disable$|^aktivieren$|^deaktivieren$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^edit$|^bearbeiten$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^delete$|^löschen$/i)).toHaveCount(0);
    // Deliveries button is not canAdmin-gated.
    await expect(page.getByTitle(/delivery history|zustell-verlauf/i)).toBeVisible();
  });

  test('78.11 — Operator: mutate controls count 0, deliveries visible', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Operator' }) }),
    );
    await page.route('**/api/alerting/rules', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([ruleJson({ id: 'op-r', name: 'OP-RULE' })]) }),
    );

    await page.goto('/alerts?tab=custom');
    await expect(page.getByText('OP-RULE')).toBeVisible({ timeout: 15_000 });

    // Operator can read but not mutate (canAdmin = Admin-only).
    await expect(page.getByRole('button', { name: /new rule|neue regel/i })).toHaveCount(0);
    await expect(page.getByTitle(/^enable$|^disable$|^aktivieren$|^deaktivieren$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^edit$|^bearbeiten$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^delete$|^löschen$/i)).toHaveCount(0);
    await expect(page.getByTitle(/delivery history|zustell-verlauf/i)).toBeVisible();
  });

  // ---- System block -------------------------------------------------------------------

  test('78.12 — system-policy CRUD + test-fire (Execution category, preset, DELETE confirm)', async ({ page }) => {
    const policyId = 'sp-1';
    let policies: ReturnType<typeof systemPolicyJson>[] = [];
    let postBody: Record<string, unknown> | null = null;
    let deleteHit = false;

    await page.route('**/api/alerting/system/policies', (route) => {
      const method = route.request().method();
      if (method === 'POST') {
        postBody = route.request().postDataJSON();
        const created = systemPolicyJson({ id: policyId, name: 'Backlog critical', ...(postBody as object) });
        policies = [created];
        return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(created) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(policies) });
    });
    await page.route(`**/api/alerting/system/policies/${policyId}`, (route) => {
      const method = route.request().method();
      if (method === 'PUT') {
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(policies[0]) });
      }
      if (method === 'DELETE') {
        deleteHit = true;
        policies = [];
        return route.fulfill({ status: 204, body: '' });
      }
      return route.fallback();
    });
    await page.route(`**/api/alerting/system/policies/${policyId}/test-fire`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(testFireJson({ error: 'Webhook 502' })) }),
    );

    await page.goto('/alerts');
    // System tab is default; the Execution category heading renders.
    await expect(page.getByRole('button', { name: /system alerts|system-alarme/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/^execution$/i).first()).toBeVisible();

    // Add policy under the Execution source card.
    await page.getByRole('button', { name: /add policy|policy hinzufuegen/i }).click();
    const panel = editorPanel(page, /new policy|neue policy/i);
    await expect(panel).toBeVisible();

    // Name (placeholder from alerts:system.editor.namePlaceholder).
    await panel.getByPlaceholder(/backlog critical/i).fill('Backlog critical');
    // Preset select — pick the 'critical' preset.
    await panel.locator('select').first().selectOption('critical');
    // Route target — email placeholder.
    await panel.getByPlaceholder(/ops@example.com/i).fill('ops@example.com');

    await panel.getByRole('button', { name: /^save$|^speichern$/i }).click();

    await expect.poll(() => postBody, { timeout: 10_000 }).not.toBeNull();
    expect(postBody).toMatchObject({
      name: 'Backlog critical',
      sourceId: 'execution-result',
      presetId: 'critical',
      scopeKind: 'Global',
      routes: [{ channel: 'Email', target: 'ops@example.com' }],
    });
    await expect(panel).toHaveCount(0);
    // The saved policy row appears under the Execution source.
    await expect(page.getByText('Backlog critical')).toBeVisible();

    // Edit the policy → Test-fire renders a partial result.
    await page.locator('li', { hasText: 'Backlog critical' }).getByTitle(/^edit$|^bearbeiten$/i).click();
    const editPanel = editorPanel(page, /edit policy|policy bearbeiten/i);
    await expect(editPanel).toBeVisible();
    await editPanel.getByRole('button', { name: /test notification|testbenachrichtigung/i }).click();
    await expect(editPanel.getByText(/some channels failed:|einige kanaele fehlgeschlagen:/i)).toBeVisible();
    await expect(editPanel.getByText(/Webhook 502/)).toBeVisible();
    // Close the editor via Cancel.
    await editPanel.getByRole('button', { name: /^cancel$|^abbrechen$/i }).click();
    await expect(editPanel).toHaveCount(0);

    // Delete the policy with confirm.
    await page.locator('li', { hasText: 'Backlog critical' }).getByTitle(/^delete$|^löschen$/i).click();
    await page.getByRole('button', { name: /^ok$/i }).click();
    await expect.poll(() => deleteHit, { timeout: 10_000 }).toBe(true);
    await expect(page.getByText('Backlog critical')).toHaveCount(0);
  });
});