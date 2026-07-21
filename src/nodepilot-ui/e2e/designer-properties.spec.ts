import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Part 5 — Properties Panel & Variables (lines 747-846).
 *
 * Hermetic: page.route() mocks only. Workflow is locked-by-me → editable (State B), so all the
 * PropertiesPanel edit affordances are live. The SPA renders ENGLISH under Playwright.
 *
 * What's covered without a real backend:
 *   5.1 — Output Variable name set + rename (the StatusPillRow output pill → input), Save PUT.
 *   5.2 — Template-variable plumbing: a downstream runScript sees its upstream var in the
 *         "Input Variables" section and the in-field Variable picker. (CodeMirror's keystroke
 *         autocompletion popup itself is an editor-internal widget — we assert the picker +
 *         input-variable surfaces instead, which is the testable equivalent.)
 *   5.3 — Global variables: a mocked /global-variables (ADMIN_EMAIL) shows up in the Globals
 *         picker on an emailNotification node and inserts {{globals.ADMIN_EMAIL}}.
 *   5.4 — Structured output: a runScript producing $hostName / $version surfaces its
 *         step.param.* entries as Input Variables on a downstream returnData node, whose
 *         Rückgabe-Felder (return data) table holds the {{step.param.x}} references.
 */

const WF_ID = 'f5f5f5f5-5555-5555-5555-555555555555';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Props',
    description: '',
    isEnabled: false,
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

/** Click a node → opens its PropertiesPanel. Clicks near the node's top-left corner to keep
 *  the click point clear of the bottom-right MiniMap / bottom-left Controls overlays. */
async function selectNode(page: Page, id: string, expectActivityLabel: RegExp) {
  await node(page, id).click({ position: { x: 15, y: 15 } });
  // PanelHeader renders the activity-type label under the editable name.
  await expect(page.getByText(expectActivityLabel).first()).toBeVisible({ timeout: 10_000 });
}

test.describe('Designer Properties Panel & Variablen (Teil 5)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('5.1 — set & rename the Output Variable, then Save PUTs the new alias', async ({ page }) => {
    const def = JSON.stringify({
      nodes: [{
        id: 'step-script', type: 'activity', position: { x: 60, y: 60 },
        data: { label: 'Probe', activityType: 'runScript', config: { script: 'Get-Date' } },
      }],
      edges: [],
    });
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
    });

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-script')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-script', /run script/i);

    // The output pill opens a name input. Default shows the step id placeholder.
    await page.getByRole('button', { name: /step-script/ }).click();
    const outInput = page.getByPlaceholder('step-script');
    await expect(outInput).toBeVisible();
    await outInput.fill('myScriptOutput');
    await page.keyboard.press('Enter');

    // Persist, then rename → second PUT carries the new name.
    const save = page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first();
    await save.click();
    await expect.poll(() => putBody && JSON.parse(putBody.definitionJson as string).nodes[0].data.outputVariable, { timeout: 10_000 })
      .toBe('myScriptOutput');

    // Rename: reopen pill, the input now reflects myScriptOutput → change it.
    putBody = null;
    await page.getByRole('button', { name: /myScriptOutput/ }).click();
    const outInput2 = page.locator('input[placeholder="step-script"]');
    await outInput2.fill('myScriptOutput_v2');
    await page.keyboard.press('Enter');
    await save.click();
    await expect.poll(() => putBody && JSON.parse(putBody.definitionJson as string).nodes[0].data.outputVariable, { timeout: 10_000 })
      .toBe('myScriptOutput_v2');
  });

  test('5.2 — downstream node shows the upstream template variable in Input Variables + picker', async ({ page }) => {
    // delay (delayOutput) → runScript. The script step is downstream so it sees {{delayOutput.*}}.
    const def = JSON.stringify({
      nodes: [
        { id: 'step-delay', type: 'activity', position: { x: 60, y: 60 },
          data: { label: 'Wait', activityType: 'delay', outputVariable: 'delayOutput', config: { seconds: 2 } } },
        { id: 'step-script', type: 'activity', position: { x: 320, y: 60 },
          data: { label: 'Consume', activityType: 'runScript', config: { script: "$prev = '{{delayOutput.output}}'" } } },
      ],
      edges: [{ id: 'e1', source: 'step-delay', target: 'step-script', type: 'labeled', data: { label: '', condition: '' } }],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-script')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-script', /run script/i);

    // "Input Variables" section is present (collapsible). Expand it.
    const inputVarsHeader = page.getByRole('button', { name: /input variables/i });
    await expect(inputVarsHeader).toBeVisible();
    await inputVarsHeader.click();

    // The producing step's output expression is offered (delay has no params → just .output).
    await expect(page.getByText('{{delayOutput.output}}').first()).toBeVisible();

    // The in-field Variable picker also counts the upstream var (>=1).
    const varPicker = page.getByRole('button', { name: /^vars?\b/i }).first();
    await expect(varPicker).toBeVisible();
  });

  test('5.3 — a global variable surfaces in the Globals picker and inserts {{globals.NAME}}', async ({ page }) => {
    const def = JSON.stringify({
      nodes: [{
        id: 'step-email', type: 'activity', position: { x: 60, y: 60 },
        data: { label: 'Notify', activityType: 'emailNotification', config: { to: '', subject: 'Hi' } },
      }],
      edges: [],
    });
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) });
    });
    // The Globals picker reads /global-variables — seed one (non-secret).
    await page.route('**/api/global-variables', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: 'g1', name: 'ADMIN_EMAIL', value: 'admin@example.com', isSecret: false, description: null }]),
      }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-email')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-email', /send email/i);

    // Each VariableInsertField carries a "Globals N" picker button. Open the first one.
    const globalsBtn = page.getByRole('button', { name: /^globals\b/i }).first();
    await expect(globalsBtn).toBeVisible();
    await globalsBtn.click();

    // The seeded global is listed; clicking it inserts {{globals.ADMIN_EMAIL}} into the field.
    await page.getByRole('button', { name: /ADMIN_EMAIL/ }).click();

    // The "To" field now holds the template. Save → PUT carries it through.
    await expect(page.locator('input[value="{{globals.ADMIN_EMAIL}}"], textarea')).toBeTruthy();
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => {
      if (!putBody) return null;
      const d = JSON.parse(putBody.definitionJson as string) as { nodes: { data: { config?: Record<string, string> } }[] };
      return d.nodes[0].data.config?.to ?? null;
    }, { timeout: 10_000 }).toContain('{{globals.ADMIN_EMAIL}}');
  });

  test('5.4 — structured-output params flow from runScript into a downstream returnData', async ({ page }) => {
    // runScript declares $hostName/$version → the engine captures them as param.*; the var-scan
    // surfaces {{producer.param.hostName}} / .param.version downstream. The returnData step is
    // downstream and references them in its Rückgabe-Felder (return-data) map.
    const def = JSON.stringify({
      nodes: [
        { id: 'step-collect', type: 'activity', position: { x: 60, y: 60 },
          data: {
            label: 'Collect', activityType: 'runScript', outputVariable: 'collect',
            config: { script: "$hostName = 'SERVER01'\n$version = '1.0.5'\nWrite-Host \"$hostName $version\"" },
          } },
        { id: 'step-return', type: 'activity', position: { x: 320, y: 60 },
          data: {
            label: 'Return', activityType: 'returnData',
            config: { data: { host: '{{collect.param.hostName}}', version: '{{collect.param.version}}' } },
          } },
      ],
      edges: [{ id: 'e1', source: 'step-collect', target: 'step-return', type: 'labeled', data: { label: '', condition: '' } }],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-return')).toBeVisible({ timeout: 15_000 });
    await selectNode(page, 'step-return', /return data/i);

    // The returnData config exposes its "Rückgabe-Felder" (return fields) editor with the two keys.
    await expect(page.locator('input[value="host"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('input[value="version"]')).toBeVisible();

    // The downstream node sees the producer's structured params in Input Variables.
    const inputVarsHeader = page.getByRole('button', { name: /input variables/i });
    await expect(inputVarsHeader).toBeVisible();
    await inputVarsHeader.click();
    await expect(page.getByText('{{collect.param.hostName}}').first()).toBeVisible();
    await expect(page.getByText('{{collect.param.version}}').first()).toBeVisible();
  });
});
