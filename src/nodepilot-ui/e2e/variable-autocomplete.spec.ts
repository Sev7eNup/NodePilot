import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 58 — Variable Autocomplete & Preview Tooltip (lines 3612-3631).
 *
 * Hermetic: page.route() mocks only. Workflow is locked-by-me → editable (State B), so the
 * PropertiesPanel inputs are live.
 *
 * 58.1 — Typing `{{` in a downstream node's input (here the `log` activity's "Message"
 *        textarea, a plain-HTML VariableInsertField — NOT the CodeMirror script field, whose
 *        completion is an editor-internal widget) opens the inline-autocomplete dropdown
 *        rendered by VariableSuggestionsDropdown (role="listbox"). The list offers the upstream
 *        step's output/error/success/param.* expressions PLUS globals.* (loaded from
 *        /api/global-variables). Filtering is case-insensitive; Enter/Tab inserts the
 *        chosen `{{…}}` expression into the field.
 *
 * 58.2 — The Variable-Preview tooltip (VariablePreviewTooltip) hovers over a variable row in
 *        the "Input Variables" list and shows the last-run value/channel. Without a completed
 *        execution it shows the "no value from last run" message — which is still the
 *        tooltip-on-hover contract. We assert the hover surface produces a role="tooltip".
 */

const WF_ID = 'a5a5a5a5-5858-5858-5858-585858585858';

function workflowJson(definitionJson: string) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Autocomplete',
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

async function selectNode(page: Page, id: string, expectActivityLabel: RegExp) {
  await node(page, id).click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(expectActivityLabel).first()).toBeVisible({ timeout: 10_000 });
}

// runScript (producer, var "probe") → log (consumer, downstream). Using runScript as the
// producer also gives the contract param.* tail surface; the log step's Message field is a
// plain-HTML VariableInsertField driving the inline autocomplete. The consumer sees
// {{probe.output}} etc. and — once a global is mocked — {{globals.ADMIN_EMAIL}}.
function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-probe', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'Probe', activityType: 'runScript', outputVariable: 'probe', config: { script: "$hostName = 'SERVER01'" } } },
      { id: 'step-log', type: 'activity', position: { x: 260, y: 40 },
        data: { label: 'Consume', activityType: 'log', config: { level: 'info', message: '' } } },
    ],
    edges: [{ id: 'e1', source: 'step-probe', target: 'step-log', type: 'labeled', data: { label: '', condition: '' } }],
  });
}

test.describe('Variable Autocomplete & Preview Tooltip (Teil 58)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    // Seed one non-secret global so the autocomplete list also surfaces globals.*.
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: 'g1', name: 'ADMIN_EMAIL', value: 'admin@example.com', isSecret: false, description: null }]),
      }),
    );
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(definition()) }),
    );
  });

  test('58.1 — typing {{ opens the autocomplete listing upstream output/error/success/param.* + globals, and inserts on Enter', async ({ page }) => {
    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-log')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-log', /log message/i);

    // The `log` activity's "Message" is a plain-HTML multiline VariableInsertField textarea —
    // the only textarea in the Configuration section. Inline autocomplete is on by default.
    const scriptField = page.locator('textarea').last();
    await expect(scriptField).toBeVisible({ timeout: 10_000 });
    await scriptField.click();
    await scriptField.fill('');
    // Type the trigger. keyUp fires autocomplete.refresh → dropdown opens.
    await scriptField.type('{{');

    // Dropdown is a portal with role="listbox".
    const listbox = page.getByRole('listbox');
    await expect(listbox).toBeVisible({ timeout: 5_000 });

    // The autocomplete suggestion source (useVariableAutocomplete) surfaces the upstream
    // step's `.output` expression, its captured structured `.param.*` (the producer script
    // declares `$hostName`), and every global `{{globals.NAME}}`. (`.error`/`.success` are
    // contract-resolvable tails but are not emitted as autocomplete entries by
    // describeNodeOutputs — they are typed as literals, see 58.1 note in the dropdown help.)
    await expect(listbox.getByText('{{probe.output}}')).toBeVisible();
    await expect(listbox.getByText('{{probe.param.hostName}}')).toBeVisible();
    await expect(listbox.getByText('{{globals.ADMIN_EMAIL}}')).toBeVisible();

    // Case-insensitive filter: narrow to "OUTPUT" (upper) → only the output expression remains.
    await scriptField.type('probe.OUTPUT');
    await expect(listbox.getByText('{{probe.output}}')).toBeVisible();
    await expect(listbox.getByText('{{probe.param.hostName}}')).toHaveCount(0);

    // Enter inserts the highlighted suggestion, closing the `{{…}}`.
    await scriptField.press('Enter');
    await expect(listbox).toHaveCount(0);
    await expect(scriptField).toHaveValue('{{probe.output}}');
  });

  test('58.2 — hovering an Input-Variable row pops a preview tooltip (no-last-run message without an execution)', async ({ page }) => {
    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-log')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-log', /log message/i);

    // Expand the "Input Variables" section so the variable rows (wrapped in VariablePreviewTooltip)
    // are present.
    const inputVarsHeader = page.getByRole('button', { name: /input variables/i });
    await expect(inputVarsHeader).toBeVisible({ timeout: 10_000 });
    await inputVarsHeader.click();

    const row = page.getByText('{{probe.output}}').first();
    await expect(row).toBeVisible();

    // VariablePreviewTooltip opens after a 250 ms hover delay. No completed execution is mocked,
    // so the tooltip shows the "no value from last run" copy — but the on-hover tooltip contract
    // is exactly what 58.2 verifies.
    await row.hover();
    const tooltip = page.locator('[role="tooltip"]');
    await expect(tooltip).toBeVisible({ timeout: 5_000 });
    await expect(tooltip).toContainText(/letzten lauf|noch nicht ausgeführt|kein wert/i);
  });
});
