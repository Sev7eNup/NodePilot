import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md part 14 — all trigger types.
 *
 * Scope: the config UI of each trigger type's PropertiesPanel, not live firing (firing is
 * engine/TriggerOrchestrator behaviour, covered by the backend test suites). Each test seeds
 * a trigger node into a locked-by-me (State B) workflow, opens it, and asserts that the
 * trigger's distinctive config field renders and round-trips through onUpdate and Save.
 *
 * Hermetic: page.route() mocks only (fixtures/mockApi.ts conventions). The SPA renders English.
 *
 * Component details the tests rely on:
 *   - Config-panel routing lives in activityConfigMap.ts, keyed by activityType.
 *   - PanelHeader shows getActivityLabel(activityType): Schedule / Webhook / File Watcher /
 *     Database Trigger / Event Log, which confirms the right panel mounted.
 *   - The scheduleTrigger "Next fire times" preview is computed client-side via cron-parser
 *     (lib/cronPreview.ts), so 14.1 asserts the rendered preview rather than an API response.
 */

const WF_ID = 'a14a14a1-0000-0000-0000-00000000a14a';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Triggers',
    description: '',
    isEnabled: false,
    // Locked by the mock user (State B), so every PropertiesPanel edit affordance is live.
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson,
    version: 1,
    ...overrides,
  });
}

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

/** Seeds one trigger node, mocks the workflow GET, and captures the bodies of PUT requests. */
async function seedTrigger(
  page: Page,
  activityType: string,
  config: Record<string, unknown>,
  putSink: { body: { definitionJson?: string } | null },
) {
  const def = JSON.stringify({
    nodes: [{
      id: 'trg', type: 'trigger', position: { x: 40, y: 40 },
      data: { label: activityType, activityType, config },
    }],
    edges: [],
  });
  await page.route(`**/api/workflows/${WF_ID}`, (route) => {
    if (route.request().method() === 'PUT') {
      putSink.body = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
  });
}

/** Opens a node's PropertiesPanel and waits for the activity-type label to confirm the mount. */
async function openTrigger(page: Page, expectLabel: RegExp) {
  await page.goto(`/workflows/${WF_ID}`);
  await expect(node(page, 'trg')).toBeVisible({ timeout: 15_000 });
  await node(page, 'trg').click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(expectLabel).first()).toBeVisible({ timeout: 10_000 });
}

const saveButton = (page: Page) =>
  page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();

test.describe('Alle Trigger-Typen — Config UI (Teil 14)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('14.1 — scheduleTrigger: cron field + client-side "Next fire times" preview', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    // An every-five-minutes cron makes cron-parser yield concrete future times in the preview.
    await seedTrigger(page, 'scheduleTrigger', { cronExpression: '0 */5 * * * ?' }, putSink);
    // The designer never calls this endpoint; mocking it proves the preview is computed locally.
    await page.route('**/api/triggers/schedule/next-fires**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ fires: [], summary: '' }) }),
    );

    await openTrigger(page, /^Schedule$/);

    // Distinctive field: the cron expression input holds the seeded expression.
    const cronInput = page.locator('input[value="0 */5 * * * ?"]');
    await expect(cronInput).toBeVisible({ timeout: 10_000 });

    // Client-side preview header + at least one upcoming fire time (cron-parser computed).
    await expect(page.getByText(/next fire times/i)).toBeVisible();
    // Each fire row carries a relative "in …" suffix; assert at least one renders.
    await expect(page.getByText(/in \d/).first()).toBeVisible();

    // Round-trip: change the cron, Save, assert the new expression lands in the PUT body.
    await cronInput.fill('0 0 6 * * ?');
    await saveButton(page).click();
    await expect
      .poll(() => putSink.body && JSON.parse(putSink.body.definitionJson as string).nodes[0].data.config.cronExpression, { timeout: 10_000 })
      .toBe('0 0 6 * * ?');
  });

  test('14.1b — scheduleTrigger: invalid cron surfaces the parser error in the preview', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    await seedTrigger(page, 'scheduleTrigger', { cronExpression: 'not a cron' }, putSink);

    await openTrigger(page, /^Schedule$/);

    // previewSchedule() shows the parser message (no fire times) for an unparseable expression.
    await expect(page.getByText(/next fire times/i)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('⚠', { exact: false }).first()).toBeVisible();
  });

  test('14.2 — webhookTrigger: HTTP method + webhook path + secret fields', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    await seedTrigger(page, 'webhookTrigger', { method: 'POST', path: 'my-webhook-test', secret: 's3cr3t' }, putSink);

    await openTrigger(page, /^Webhook$/);

    // Distinctive field: the webhook Path holds the seeded value (VariableInsertField input).
    await expect(page.locator('input[value="my-webhook-test"]')).toBeVisible({ timeout: 10_000 });
    // The HTTP method <select> reflects the seeded POST.
    await expect(page.locator('select').filter({ hasText: 'POST' }).first()).toBeVisible();
    // The secret is a password input with a hidden value, so only its presence is asserted.
    await expect(page.locator('input[type="password"]')).toBeVisible();

    // Round-trip the path through Save.
    await page.locator('input[value="my-webhook-test"]').fill('renamed-hook');
    await saveButton(page).click();
    await expect
      .poll(() => putSink.body && JSON.parse(putSink.body.definitionJson as string).nodes[0].data.config.path, { timeout: 10_000 })
      .toBe('renamed-hook');
  });

  test('14.3 — fileWatcherTrigger: directory + filter + watch-type fields', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    await seedTrigger(page, 'fileWatcherTrigger', {
      directory: 'C:\\temp\\watch', filter: '*.txt', watchType: 'created', includeSubdirectories: true,
    }, putSink);

    await openTrigger(page, /file watcher/i);

    // Distinctive field: the watched Directory holds the seeded path. Located by placeholder
    // because CSS attribute selectors cannot handle the backslashes of a Windows path;
    // toHaveValue compares the literal string and needs no CSS escaping.
    const dirInput = page.getByPlaceholder('C:\\Data\\Incoming');
    await expect(dirInput).toBeVisible({ timeout: 10_000 });
    await expect(dirInput).toHaveValue('C:\\temp\\watch');
    // Filter glob.
    await expect(page.locator('input[value="*.txt"]')).toBeVisible();
    // Watch-type select offers the Created/Changed/Deleted/Renamed options.
    const watchSelect = page.locator('select');
    await expect(watchSelect.filter({ hasText: /File Created/ }).first()).toBeVisible();
    // Include-subdirectories checkbox is checked (seeded true).
    await expect(page.locator('input[type="checkbox"]').first()).toBeChecked();

    await page.locator('input[value="*.txt"]').fill('*.csv');
    await saveButton(page).click();
    await expect
      .poll(() => putSink.body && JSON.parse(putSink.body.definitionJson as string).nodes[0].data.config.filter, { timeout: 10_000 })
      .toBe('*.csv');
  });

  test('14.4 — databaseTrigger: connection ref + polling interval + SQL query fields', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    await seedTrigger(page, 'databaseTrigger', {
      connectionRef: 'Prod', pollingIntervalSeconds: 5,
      query: "SELECT id, status FROM tasks WHERE status='new'",
    }, putSink);

    await openTrigger(page, /database trigger/i);

    // Distinctive fields: connection ref + the SQL query (VariableInsertField multiline textarea).
    await expect(page.locator('input[value="Prod"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('textarea', { hasText: "SELECT id, status FROM tasks" })).toBeVisible();
    await expect(page.locator('input[type="number"][value="5"]')).toBeVisible();

    await page.locator('input[value="Prod"]').fill('Staging');
    await saveButton(page).click();
    await expect
      .poll(() => putSink.body && JSON.parse(putSink.body.definitionJson as string).nodes[0].data.config.connectionRef, { timeout: 10_000 })
      .toBe('Staging');
  });

  test('14.5 — eventLogTrigger: log name + entry type + source fields', async ({ page }) => {
    const putSink: { body: { definitionJson?: string } | null } = { body: null };
    await seedTrigger(page, 'eventLogTrigger', {
      logName: 'Application', entryType: 'Error', source: 'Application Error', eventId: 100, lookbackMinutes: 5,
    }, putSink);

    await openTrigger(page, /event log/i);

    // Distinctive field: the event-log Source text input holds the seeded value.
    await expect(page.locator('input[value="Application Error"]')).toBeVisible({ timeout: 10_000 });
    // Log-name select offers Application/System/Security/Setup.
    await expect(page.locator('select').filter({ hasText: 'Application' }).first()).toBeVisible();
    // Entry-type select offers the Error filter.
    await expect(page.locator('select').filter({ hasText: 'Error' }).first()).toBeVisible();
    // Event ID number field reflects the seeded 100.
    await expect(page.locator('input[type="number"][value="100"]')).toBeVisible();

    await page.locator('input[value="Application Error"]').fill('Service Control Manager');
    await saveButton(page).click();
    await expect
      .poll(() => putSink.body && JSON.parse(putSink.body.definitionJson as string).nodes[0].data.config.source, { timeout: 10_000 })
      .toBe('Service Control Manager');
  });
});
