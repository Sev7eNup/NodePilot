import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Part 2 — Node management / activity-type config UIs (lines 112-464).
 *
 * Hermetic: page.route() mocks only (predicate catch-all from fixtures/mockApi.ts). The
 * workflow is mocked locked-by-me (checkedOutByUserId === MOCK_USER.id, isEnabled:false) so
 * the editor opens in State B (editable) — the PropertiesPanel edit affordances are live.
 * The SPA renders ENGLISH under Playwright → language-agnostic selectors.
 *
 * Scope: this suite does NOT execute any activity. For each activity type it
 *   1. seeds a single node of that type into the workflow definitionJson,
 *   2. selects the node (click → PropertiesPanel opens on the right),
 *   3. asserts the activity's *distinctive* config field(s) render in the panel,
 *   4. edits one field, saves in place (Ctrl+S / Save button), and
 *   5. asserts the PUT /api/workflows/<id> body's definitionJson carries the edit.
 *
 * React Flow canvas drag is NOT synthesizable → nodes are pre-seeded + clicked, never dragged.
 * Nodes are clustered top-left (React Flow virtualizes off-screen nodes out of the DOM).
 *
 * Covers Test 2.1 (delay), 2.2 (runScript), 2.3 (file/folderOperation), 2.4 (restApi),
 * 2.5 (sql), 2.6 (emailNotification), 2.7 (startWorkflow), 2.8 (junction), 2.9 (returnData),
 * 2.10 (log), 2.11 (jsonQuery), 2.12 (xmlQuery), 2.13 (the five remote activities).
 */

const WF_ID = '2222aaaa-2222-2222-2222-222222222222';
const MACHINE_ID = '11111111-1111-1111-1111-111111111111';
const CRED_ID = '22222222-2222-2222-2222-222222222222';
const NODE_ID = 'step-under-test';

type SeedNode = {
  id: string;
  type: 'activity';
  position: { x: number; y: number };
  data: Record<string, unknown>;
};

function definitionWith(node: SeedNode): string {
  return JSON.stringify({ nodes: [node], edges: [] });
}

function workflowJson(definitionJson: string): string {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-ActivityConfig',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson,
    version: 1,
  });
}

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

/**
 * Install the workflow GET (returns the seeded definition) + PUT (captures the body).
 * Returns a getter for the most recent PUT body so the test can assert what was persisted.
 */
function routeWorkflow(page: Page, definitionJson: string) {
  const state: { putBody: { definitionJson?: string } | null } = { putBody: null };
  void page.route(`**/api/workflows/${WF_ID}`, (route) => {
    if (route.request().method() === 'PUT') {
      state.putBody = route.request().postDataJSON();
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(definitionJson) });
  });
  return state;
}

/** Open the editor, wait for the seeded node, select it → PropertiesPanel opens. */
async function openAndSelect(page: Page, expectActivityLabel: RegExp) {
  await page.goto(`/workflows/${WF_ID}`);
  await expect(node(page, NODE_ID)).toBeVisible({ timeout: 20_000 });
  // Click near the top-left corner to stay clear of MiniMap / Controls overlays.
  await node(page, NODE_ID).click({ position: { x: 15, y: 15 } });
  // PanelHeader renders the activity-type label under the editable name.
  await expect(page.getByText(expectActivityLabel).first()).toBeVisible({ timeout: 10_000 });
}

/** Save in place (State B). The Save button is icon-only — its accessible name is the title. */
async function saveInPlace(page: Page) {
  await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
}

/** Parse the persisted node's data.config from the captured PUT body. */
function persistedConfig(putBody: { definitionJson?: string } | null): Record<string, unknown> | null {
  if (!putBody?.definitionJson) return null;
  const def = JSON.parse(putBody.definitionJson) as { nodes: { id: string; data: { config?: Record<string, unknown> } }[] };
  return def.nodes.find((n) => n.id === NODE_ID)?.data.config ?? null;
}

test.describe('Node Activity-Config UIs (Teil 2)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // One machine + one credential so the Execution-Context pickers on remote activities populate.
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: MACHINE_ID, name: 'WIN-DC01', hostname: 'win-dc01.lab', isReachable: true }]),
      }),
    );
    await page.route('**/api/credentials', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: CRED_ID, name: 'lab-admin', username: 'LAB\\admin' }]),
      }),
    );
  });

  // ---------- 2.1 — delay (Engine-local, simplest) ----------
  test('2.1 — delay: shows seconds input, edit persists to PUT', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Wait', activityType: 'delay', config: { seconds: 5 } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /delay/i);

    // Distinctive field: the "Delay (seconds)" number input, pre-filled with 5.
    const secondsInput = page.locator('input[type="number"]').first();
    await expect(secondsInput).toBeVisible();
    await expect(secondsInput).toHaveValue('5');

    await secondsInput.fill('42');
    await saveInPlace(page);
    await expect.poll(() => persistedConfig(state.putBody)?.seconds, { timeout: 10_000 }).toBe(42);
  });

  // ---------- 2.2 — runScript (Remote, script editor + machine picker) ----------
  test('2.2 — runScript: script editor + Execution-Context machine field, edit persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Probe', activityType: 'runScript', config: { script: 'Get-Date', engine: 'auto' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /run script/i);

    // Distinctive runScript surfaces: an Engine <select> and the PowerShell CodeMirror editor.
    await expect(page.getByRole('combobox').filter({ hasText: /auto|powershell/i }).first()).toBeVisible();
    const cm = page.locator('.cm-content');
    await expect(cm.first()).toBeVisible();
    await expect(cm.first()).toContainText('Get-Date');

    // Remote activity → the Execution-Context section offers a target-machine field; the seeded
    // machine is selectable via the "Liste" options picker.
    await expect(page.getByText(/execution context/i)).toBeVisible();

    // Persist via the Engine <select> (CodeMirror keystroke-replacement is editor-internal and
    // not reliably synthesizable). Switch auto → PowerShell 7 and assert it round-trips.
    const engineSelect = page.getByRole('combobox').filter({ hasText: /auto/i }).first();
    await engineSelect.selectOption('pwsh');
    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.engine).toBe('pwsh');
    expect(cfg?.script).toBe('Get-Date'); // script field preserved through the save
  });

  // ---------- 2.2b — runScript success-semantics + process-isolation config surface ----------
  test('2.2b — runScript: isolation toggle reveals caps; isolated + successExitCodes persist', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Probe', activityType: 'runScript', config: { script: 'exit 1', engine: 'auto' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /run script/i);

    // Process-isolation checkbox (EN preview → "Process isolation"); enabled because the
    // step has no target machine (local execution).
    const iso = page.getByRole('checkbox', { name: 'Process isolation' });
    await expect(iso).toBeVisible();
    await expect(iso).toBeEnabled();

    // Toggling it on reveals the optional resource-cap fields (anchor on the exact labels so the
    // "Memory limit makes …" hint paragraph doesn't also match).
    await iso.check();
    await expect(page.getByText(/Memory limit \(MB\)/)).toBeVisible();
    await expect(page.getByText(/Max\. processes/)).toBeVisible();

    // The successExitCodes input (placeholder) gates exit-based failure when set.
    const sec = page.getByPlaceholder(/error-based/i);
    await expect(sec).toBeVisible();
    await sec.fill('0,1');

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.isolated).toBe(true);
    expect(cfg?.successExitCodes).toBe('0,1');
    expect(cfg?.script).toBe('exit 1'); // preserved through the save
  });

  // ---------- 2.2c — runScript isolation is local-only (greyed for a remote target) ----------
  test('2.2c — runScript: isolation toggle disabled when a target machine is set', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Probe', activityType: 'runScript', targetMachineId: MACHINE_ID, config: { script: 'Get-Date', engine: 'auto' } },
    };
    routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /run script/i);

    // Isolation runs on the NodePilot host only → disabled (greyed) for a remote step, with a hint.
    const iso = page.getByRole('checkbox', { name: 'Process isolation' });
    await expect(iso).toBeVisible();
    await expect(iso).toBeDisabled();
    await expect(page.getByText(/only applies to local execution/i)).toBeVisible();
  });

  // ---------- 2.3 — fileOperation + folderOperation (dynamic per operation) ----------
  test('2.3a — fileOperation: 6-option operation dropdown + dynamic destination field', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Copy file', activityType: 'fileOperation', config: { operation: 'copy', path: 'C:\\a.txt', destination: 'C:\\b.txt' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /file operation/i);

    // Operation dropdown — fileOperation has exactly 6 options (copy/move/rename/delete/exists/create).
    const opSelect = page.getByRole('combobox').filter({ hasText: /copy/i }).first();
    await expect(opSelect).toBeVisible();
    await expect(opSelect.locator('option')).toHaveCount(6);

    // copy → the "Copy to (Destination)" field is present (dynamic). Edit the File path
    // (located by its stable placeholder — value-attribute selectors mis-parse backslashes).
    await expect(page.getByText(/copy to \(destination\)/i)).toBeVisible();
    const pathInput = page.getByPlaceholder('C:\\Temp\\file.txt');
    await expect(pathInput).toBeVisible();
    await pathInput.fill('C:\\source\\input.txt');

    // Switch operation to rename → the destination field disappears, "New name" appears.
    await opSelect.selectOption('rename');
    await expect(page.getByText(/new name/i)).toBeVisible();
    await expect(page.getByText(/copy to \(destination\)/i)).toHaveCount(0);

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.operation).toBe('rename');
    expect(cfg?.path).toBe('C:\\source\\input.txt');
  });

  test('2.3b — folderOperation: 7-option operation dropdown including List Contents', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'List dir', activityType: 'folderOperation', config: { operation: 'list', path: 'C:\\Temp' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /folder operation/i);

    const opSelect = page.getByRole('combobox').filter({ hasText: /list contents/i }).first();
    await expect(opSelect).toBeVisible();
    // folderOperation has 7 options (file's 6 + "List Contents").
    await expect(opSelect.locator('option')).toHaveCount(7);
    await expect(opSelect.locator('option', { hasText: /list contents/i })).toHaveCount(1);

    // list → only Folder path (no destination / new name).
    await expect(page.getByText(/copy to \(destination\)/i)).toHaveCount(0);
    await expect(page.getByText(/new name/i)).toHaveCount(0);

    await opSelect.selectOption('create');
    await saveInPlace(page);
    await expect.poll(() => persistedConfig(state.putBody)?.operation, { timeout: 10_000 }).toBe('create');
  });

  // ---------- 2.4 — restApi ----------
  test('2.4 — restApi: method dropdown (6 verbs) + URL; body editor appears for POST', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Call API', activityType: 'restApi', config: { method: 'GET', url: 'https://api.example.com/data' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /rest api|http request/i);

    // Method dropdown: GET/POST/PUT/PATCH/DELETE/HEAD = 6 verbs.
    const methodSelect = page.getByRole('combobox').filter({ hasText: /GET/ }).first();
    await expect(methodSelect).toBeVisible();
    await expect(methodSelect.locator('option')).toHaveCount(6);

    // GET → no Body editor. Switch to POST → the "Body (JSON)" field renders.
    await expect(page.getByText(/body \(json\)/i)).toHaveCount(0);
    await methodSelect.selectOption('POST');
    await expect(page.getByText(/body \(json\)/i)).toBeVisible();

    // Edit the URL, save, assert persisted.
    const urlInput = page.locator('input[value="https://api.example.com/data"]');
    await expect(urlInput).toBeVisible();
    await urlInput.fill('https://httpbin.org/post');
    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.method).toBe('POST');
    expect(cfg?.url).toBe('https://httpbin.org/post');
  });

  // ---------- 2.5 — sql ----------
  test('2.5 — sql: provider dropdown + SQL query CodeMirror, query edit persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: {
        label: 'Query DB', activityType: 'sql',
        config: { provider: 'sqlite', connectionMode: 'raw', connectionString: 'Data Source=np.db', query: 'SELECT 1' },
      },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /^sql|database query/i);

    // Provider dropdown: SQL Server / SQLite / PostgreSQL = 3 options.
    const providerSelect = page.getByRole('combobox').filter({ hasText: /sqlite/i }).first();
    await expect(providerSelect).toBeVisible();
    await expect(providerSelect.locator('option')).toHaveCount(3);

    // raw connection-string mode → the Connection String field is present, pre-filled.
    await expect(page.getByText(/connection string/i).first()).toBeVisible();

    // The SQL Query CodeMirror holds the seeded query. Replace it.
    const cm = page.locator('.cm-content');
    await expect(cm.first()).toContainText('SELECT 1');
    await cm.first().click();
    await page.keyboard.press('Control+A');
    await page.keyboard.type('SELECT COUNT(*) FROM Workflows');
    await saveInPlace(page);
    await expect.poll(() => persistedConfig(state.putBody)?.query as string | undefined, { timeout: 10_000 })
      .toContain('SELECT COUNT(*) FROM Workflows');
  });

  // ---------- 2.6 — emailNotification ----------
  test('2.6 — emailNotification: To/Subject/Body + HTML toggle, To edit persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Notify', activityType: 'emailNotification', config: { to: '', subject: 'Hi', body: 'msg', isHtml: false } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /send email|email/i);

    // Distinctive fields: To / Subject / Body labels + an "HTML body" checkbox.
    await expect(page.getByText('To', { exact: true })).toBeVisible();
    await expect(page.getByText('Subject', { exact: true })).toBeVisible();
    await expect(page.getByText(/html body/i)).toBeVisible();

    // The "HTML body" checkbox — the only checkbox in the email config.
    await page.getByRole('checkbox').first().check();

    // Locate the To / Subject fields by their stable placeholders (value-attr selectors are
    // unreliable for controlled React inputs).
    const toInput = page.getByPlaceholder('admin@company.com');
    await expect(toInput).toBeVisible();
    await expect(page.getByPlaceholder('Workflow completed')).toBeVisible();
    await toInput.fill('ops@example.com');

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.to).toBe('ops@example.com');
    expect(cfg?.isHtml).toBe(true);
  });

  // ---------- 2.7 — startWorkflow ----------
  test('2.7 — startWorkflow: workflow ref field + Wait-for-Completion toggle', async ({ page }) => {
    // The config derives a calling-contract from /workflows/by-name/<name>/contract. Return 404
    // so the hook falls back to its free-form ParameterTable (the natural "unknown child" state);
    // without this the catch-all's [] would be mis-read as a contract object and crash the panel.
    await page.route('**/api/workflows/by-name/**/contract', (route) =>
      route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: 'Not Found' }) }),
    );
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Call child', activityType: 'startWorkflow', config: { workflowNameOrId: 'Child_WF', waitForCompletion: true } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /start workflow|sub.?workflow/i);

    // Distinctive surfaces: the "Workflow (Name oder GUID)" field + the wait-for-completion checkbox.
    const refInput = page.getByPlaceholder(/Rollback-Runbook/i);
    await expect(refInput).toBeVisible();
    await expect(refInput).toHaveValue('Child_WF');
    await expect(page.getByText(/auf abschluss warten|wait for completion/i)).toBeVisible();

    // Toggle wait → fire-and-forget; edit the ref. Both should persist.
    await page.getByRole('checkbox').first().uncheck();
    await refInput.fill('Rollback_Runbook');
    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.workflowNameOrId).toBe('Rollback_Runbook');
    expect(cfg?.waitForCompletion).toBe(false);
  });

  // ---------- 2.8 — junction ----------
  test('2.8 — junction: 3-mode merge dropdown; Required Count appears for waitNofM', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Merge', activityType: 'junction', config: { mode: 'waitAll' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /junction|merge/i);

    const modeSelect = page.getByRole('combobox').filter({ hasText: /wait for all/i }).first();
    await expect(modeSelect).toBeVisible();
    await expect(modeSelect.locator('option')).toHaveCount(3); // waitAll / waitAny / waitNofM

    // waitAll/waitAny → no Required Count. Switch to waitNofM → the count field appears.
    await expect(page.getByText(/required count/i)).toHaveCount(0);
    await modeSelect.selectOption('waitNofM');
    await expect(page.getByText(/required count/i)).toBeVisible();
    await page.locator('input[type="number"]').first().fill('3');

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.mode).toBe('waitNofM');
    expect(cfg?.requiredCount).toBe(3);
  });

  // ---------- 2.9 — returnData ----------
  test('2.9 — returnData: Rückgabe-Felder key/value editor, edited value persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Return', activityType: 'returnData', config: { data: { result: 'x' } } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /return data/i);

    // Distinctive: the "Rückgabe-Felder" return-fields editor with the seeded "result" key input.
    await expect(page.getByText(/rückgabe-felder|return/i).first()).toBeVisible();
    const keyInput = page.locator('input[value="result"]');
    await expect(keyInput).toBeVisible();

    // The value field for the key holds "x" — replace it with a template expression.
    const valueInput = page.locator('input[value="x"]');
    await expect(valueInput).toBeVisible();
    await valueInput.fill('{{step.output}}');

    await saveInPlace(page);
    await expect.poll(() => {
      const cfg = persistedConfig(state.putBody);
      return (cfg?.data as Record<string, string> | undefined)?.result ?? null;
    }, { timeout: 10_000 }).toBe('{{step.output}}');
  });

  // ---------- 2.10 — log ----------
  test('2.10 — log: level dropdown (info/warning/error) + message, both persist', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Log step', activityType: 'log', config: { level: 'info', message: 'start' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /^log|logging/i);

    const levelSelect = page.getByRole('combobox').filter({ hasText: /info/i }).first();
    await expect(levelSelect).toBeVisible();
    await expect(levelSelect.locator('option')).toHaveCount(3); // info / warning / error
    await levelSelect.selectOption('error');

    // The Message field is the log config's only textarea (placeholder shows a templated example).
    const messageInput = page.getByPlaceholder(/Processed .* items on/i);
    await expect(messageInput).toBeVisible();
    await messageInput.fill('Processing {{step.param.count}} items');

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.level).toBe('error');
    expect(cfg?.message).toContain('{{step.param.count}}');
  });

  // ---------- 2.11 — jsonQuery ----------
  test('2.11 — jsonQuery: Source + Result Mode dropdowns + JSONPath field, edit persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'JSON extract', activityType: 'jsonQuery', config: { source: 'inline', resultMode: 'single', jsonPath: '$.a', content: '{}' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /json query|json/i);

    // Source dropdown (Inline / File) + Result Mode dropdown (single / all).
    const sourceSelect = page.getByRole('combobox').filter({ hasText: /inline content/i }).first();
    await expect(sourceSelect).toBeVisible();
    await expect(sourceSelect.locator('option')).toHaveCount(2);
    const resultModeSelect = page.getByRole('combobox').filter({ hasText: /single \(first match\)/i }).first();
    await expect(resultModeSelect).toBeVisible();
    await expect(resultModeSelect.locator('option')).toHaveCount(2);
    await resultModeSelect.selectOption('all');

    // JSONPath field is present, seeded with $.a — replace it.
    const jsonPathInput = page.locator('input[value="$.a"]');
    await expect(jsonPathInput).toBeVisible();
    await jsonPathInput.fill('$.items[0].name');

    await saveInPlace(page);
    const cfg = await expect.poll(() => persistedConfig(state.putBody), { timeout: 10_000 }).not.toBeNull().then(() => persistedConfig(state.putBody));
    expect(cfg?.jsonPath).toBe('$.items[0].name');
    expect(cfg?.resultMode).toBe('all');
  });

  // ---------- 2.12 — xmlQuery ----------
  test('2.12 — xmlQuery: Source + Result Mode dropdowns + XPath field, edit persists', async ({ page }) => {
    const seed: SeedNode = {
      id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'XML extract', activityType: 'xmlQuery', config: { source: 'inline', resultMode: 'single', xpath: '/root/item', content: '<root/>' } },
    };
    const state = routeWorkflow(page, definitionWith(seed));
    await openAndSelect(page, /xml query|xml/i);

    const sourceSelect = page.getByRole('combobox').filter({ hasText: /inline content/i }).first();
    await expect(sourceSelect).toBeVisible();
    await expect(sourceSelect.locator('option')).toHaveCount(2);

    // The XPath field is the distinctive xmlQuery surface; also a Namespaces field exists.
    const xpathInput = page.locator('input[value="/root/item"]');
    await expect(xpathInput).toBeVisible();
    await expect(page.getByText(/namespaces/i)).toBeVisible();
    await xpathInput.fill('//book/title');

    await saveInPlace(page);
    await expect.poll(() => persistedConfig(state.putBody)?.xpath, { timeout: 10_000 }).toBe('//book/title');
  });

  // ---------- 2.13 — remote activities (data-driven) ----------
  // serviceManagement, registryOperation, wmiQuery, startProgram, powerManagement. Each is a
  // remote activity → asserts the Execution-Context section renders, the activity's distinctive
  // field is visible, and one edit round-trips through the Save PUT.
  const REMOTE_CASES: {
    activityType: string;
    labelRe: RegExp;
    seedConfig: Record<string, unknown>;
    fieldText: RegExp;
    /** Stable placeholder of the field we edit (value-attr selectors mis-parse backslashes
     *  and miss textareas / controlled React inputs). */
    editPlaceholder: RegExp;
    editValue: string;
    configKey: string;
  }[] = [
    {
      activityType: 'serviceManagement',
      labelRe: /service mgmt|service management/i,
      seedConfig: { serviceName: 'Spooler', action: 'status' },
      fieldText: /service name/i,
      editPlaceholder: /^Spooler$/,
      editValue: 'BITS',
      configKey: 'serviceName',
    },
    {
      activityType: 'registryOperation',
      labelRe: /registry/i,
      seedConfig: { operation: 'read', keyPath: 'HKLM:\\SOFTWARE\\MyApp', valueName: 'Version' },
      fieldText: /key path/i,
      editPlaceholder: /SOFTWARE\\MyApp/,
      editValue: 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion',
      configKey: 'keyPath',
    },
    {
      activityType: 'wmiQuery',
      labelRe: /wmi/i,
      seedConfig: { mode: 'query', className: 'Win32_ComputerSystem', namespace: 'root\\cimv2' },
      fieldText: /wmi class/i,
      editPlaceholder: /Win32_OperatingSystem/,
      editValue: 'Win32_OperatingSystem',
      configKey: 'className',
    },
    {
      activityType: 'startProgram',
      labelRe: /start program|run program/i,
      seedConfig: { filePath: 'notepad.exe', arguments: 'C:\\test.txt', waitForExit: true, successExitCodes: '0' },
      fieldText: /file path/i,
      editPlaceholder: /7z\.exe/,
      editValue: 'C:\\Tools\\7z.exe',
      configKey: 'filePath',
    },
    {
      activityType: 'powerManagement',
      labelRe: /shutdown|restart|power/i,
      seedConfig: { action: 'shutdown', delaySeconds: 60, force: false, message: 'Maintenance' },
      fieldText: /action/i,
      editPlaceholder: /please save your work/i,
      editValue: 'Scheduled maintenance shutdown',
      configKey: 'message',
    },
  ];

  for (const c of REMOTE_CASES) {
    test(`2.13 — ${c.activityType}: distinctive field renders + Execution-Context, edit persists`, async ({ page }) => {
      const seed: SeedNode = {
        id: NODE_ID, type: 'activity', position: { x: 60, y: 60 },
        data: { label: c.activityType, activityType: c.activityType, config: c.seedConfig },
      };
      const state = routeWorkflow(page, definitionWith(seed));
      await openAndSelect(page, c.labelRe);

      // Remote → the Execution-Context section (target machine / credential) renders.
      await expect(page.getByText(/execution context/i)).toBeVisible();
      // Activity-specific config field is present.
      await expect(page.getByText(c.fieldText).first()).toBeVisible();

      const editInput = page.getByPlaceholder(c.editPlaceholder);
      await expect(editInput).toBeVisible();
      await editInput.fill(c.editValue);
      await saveInPlace(page);
      await expect.poll(() => persistedConfig(state.putBody)?.[c.configKey], { timeout: 10_000 }).toBe(c.editValue);
    });
  }
});
