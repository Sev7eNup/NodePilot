import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 28 — Step-Test mit Kontext (lines 2516-2589).
 *
 * The StepTestPanel (components/designer/properties/StepTestPanel.tsx) is mounted inside the
 * PropertiesPanel's collapsible "Test & Debug" section for any non-trigger activity when a
 * workflowId is present. It:
 *   - POSTs /api/workflows/{id}/steps/{stepId}/test with { mockVariables, configOverride }
 *     (configOverride = the LIVE editor config, so unsaved edits are tested),
 *   - has four modes (Empty / Last run / Pick a run / Manual mocks); lastRun/pickRun pull from
 *     GET .../test-context (+ /test-context/runs for the run picker),
 *   - renders the result block with output, error output, and structured params.
 *
 * Backend redaction: OutputRedactor masks secrets server-side BEFORE the /test response is
 * returned — the panel renders the response verbatim. We simulate that by returning an already
 * masked ("***") output and asserting it surfaces (the UI must not un-mask).
 *
 * Hermetic: page.route() mocks only. SPA renders ENGLISH. Run button hidden for Viewers (canRun).
 *
 * Covers:
 *   28.1 — Empty mode runs the step + shows live output (no execution row created — observed as
 *          /test being the only POST, never /execute).
 *   28.1b — redacted output ("***") surfaces in the result block.
 *   28.2 — Manual mocks: key=value lines are parsed into mockVariables on the /test POST.
 *   28.3 — configOverride carries the LIVE editor config (unsaved), not the DB-saved config.
 *   28.6 — Viewer: Run button hidden (canRun=false) — API also 403s but the UI gate is observable.
 */

const WF_ID = 'b28b28b2-0000-0000-0000-00000000b28b';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-StepTest',
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

/** Seed one runScript node, open it, then expand the "Test & Debug" section. */
async function openStepTest(page: Page, scriptConfig: Record<string, unknown>) {
  const def = JSON.stringify({
    nodes: [{
      id: 'step-probe', type: 'activity', position: { x: 50, y: 50 },
      data: { label: 'Probe', activityType: 'runScript', config: scriptConfig },
    }],
    edges: [],
  });
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
  );

  await seedExpertMode(page);
  await page.goto(`/workflows/${WF_ID}`);
  await expect(node(page, 'step-probe')).toBeVisible({ timeout: 15_000 });
  await node(page, 'step-probe').click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(/run script/i).first()).toBeVisible({ timeout: 10_000 });

  // The "Test & Debug" section is collapsible + closed by default → click its header to expand.
  const section = page.getByRole('button', { name: /test & debug/i }).first();
  await expect(section).toBeVisible({ timeout: 10_000 });
  await section.click();
}

const runButton = (page: Page) => page.getByRole('button', { name: /run test/i });

test.describe('Step-Test mit Kontext (Teil 28)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('28.1 — Empty mode runs the step, shows output, and never starts an execution', async ({ page }) => {
    const calls: { test: Record<string, unknown> | null; executeHit: boolean } = { test: null, executeHit: false };
    await page.route(`**/api/workflows/${WF_ID}/steps/step-probe/test`, (route) => {
      calls.test = route.request().postDataJSON();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ success: true, output: 'PSDrive C OK', errorOutput: null, outputParameters: {}, durationMs: 18, errorMessage: null }),
      });
    });
    // A step-test must NOT create a WorkflowExecution — guard by flagging any /execute hit.
    await page.route(`**/api/workflows/${WF_ID}/execute`, (route) => {
      calls.executeHit = true;
      return route.fulfill({ status: 202, contentType: 'application/json', body: '{}' });
    });

    await openStepTest(page, { script: 'Get-PSDrive C' });

    await expect(runButton(page)).toBeVisible({ timeout: 10_000 });
    await runButton(page).click();

    // The step-test POST fired; its body has configOverride and no mockVariables (Empty mode).
    await expect.poll(() => calls.test, { timeout: 10_000 }).not.toBeNull();
    expect((calls.test as { configOverride?: Record<string, unknown> }).configOverride).toMatchObject({ script: 'Get-PSDrive C' });
    expect((calls.test as { mockVariables?: unknown }).mockVariables).toBeUndefined();

    // Live output is shown; no execution was started.
    await expect(page.getByText('PSDrive C OK')).toBeVisible();
    await expect(page.getByText(/succeeded/i).first()).toBeVisible();
    expect(calls.executeHit).toBe(false);
  });

  test('28.1b — server-redacted output (***) surfaces verbatim in the result block', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}/steps/step-probe/test`, (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        // OutputRedactor masked the secret server-side before returning.
        body: JSON.stringify({ success: true, output: 'password=***', errorOutput: null, outputParameters: {}, durationMs: 5, errorMessage: null }),
      }),
    );

    await openStepTest(page, { script: 'Write-Output "password=hunter2"' });
    await runButton(page).click();

    // The result OUTPUT pre shows the masked value. (The unredacted script "hunter2" still
    // appears in the config editor above — that's the user's own input, not server output —
    // so we scope the secret-leak check to the result block's <pre>, not the whole page.)
    const outputPre = page.locator('pre.whitespace-pre-wrap').filter({ hasText: 'password=' });
    await expect(outputPre).toBeVisible({ timeout: 10_000 });
    await expect(outputPre).toContainText('password=***');
    await expect(outputPre).not.toContainText('hunter2');
  });

  test('28.2 — Manual mocks: key=value lines are parsed into mockVariables', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}/steps/step-probe/test`, (route) => {
      sink.body = route.request().postDataJSON();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ success: true, output: 'free=7 ok', errorOutput: null, outputParameters: {}, durationMs: 9, errorMessage: null }),
      });
    });

    await openStepTest(page, { script: "$x = '{{checkDisk.param.freeGb}}'; Write-Output \"free=$x {{checkDisk.output}}\"" });

    // Switch to Manual mocks mode (exact pill label), then enter the two variables.
    await page.getByRole('button', { name: /^manual mocks$/i }).click();
    const textarea = page.getByPlaceholder(/stepName\.output=value/i);
    await expect(textarea).toBeVisible({ timeout: 10_000 });
    await textarea.fill('checkDisk.param.freeGb=7\ncheckDisk.output=ok');

    await runButton(page).click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect((sink.body as { mockVariables?: Record<string, string> }).mockVariables).toEqual({
      'checkDisk.param.freeGb': '7',
      'checkDisk.output': 'ok',
    });
  });

  test('28.3 — configOverride carries the LIVE (unsaved) editor config, not the DB-saved one', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}/steps/step-probe/test`, (route) => {
      sink.body = route.request().postDataJSON();
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ success: true, output: 'NEW', errorOutput: null, outputParameters: {}, durationMs: 3, errorMessage: null }),
      });
    });

    // DB-saved script is "echo OLD". Open it, then edit the script in the RunScript config editor
    // to "echo NEW" WITHOUT saving — the panel must send the live value as configOverride.
    await openStepTest(page, { script: 'echo OLD' });

    // The RunScript config exposes a CodeMirror/textarea holding the script. Find the editable
    // surface that currently contains "echo OLD" and replace it with "echo NEW".
    const scriptArea = page.locator('textarea').filter({ hasText: 'echo OLD' }).first();
    if (await scriptArea.count()) {
      await scriptArea.fill('echo NEW');
    } else {
      // RunScript uses a CodeMirror editor (contenteditable). Click into it and retype.
      const cm = page.locator('.cm-content').first();
      await cm.click();
      await page.keyboard.press('ControlOrMeta+A');
      await page.keyboard.type('echo NEW');
    }

    await runButton(page).click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    const override = (sink.body as { configOverride?: { script?: string } }).configOverride;
    expect(override?.script).toContain('echo NEW');
    expect(override?.script).not.toContain('echo OLD');
  });

  test('28.6 — Viewer cannot run a step-test: the Run button is hidden', async ({ page }) => {
    // A Viewer (or a non-lock-owner) has canWrite=false → StepTestPanel canRun=false → no Run button.
    const def = JSON.stringify({
      nodes: [{ id: 'step-probe', type: 'activity', position: { x: 50, y: 50 }, data: { label: 'Probe', activityType: 'runScript', config: { script: 'Get-Date' } } }],
      edges: [],
    });
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: MOCK_USER.id, username: 'viewer', role: 'Viewer' }) }),
    );
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      // Not checked out by me → read-only → canWrite=false.
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def, { checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null }) }),
    );

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-probe')).toBeVisible({ timeout: 15_000 });
    await node(page, 'step-probe').click({ position: { x: 15, y: 15 } });
    await expect(page.getByText(/run script/i).first()).toBeVisible({ timeout: 10_000 });

    // Expand the Test & Debug section — the panel mounts but the Run button must be absent.
    await page.getByRole('button', { name: /test & debug/i }).first().click();
    await expect(runButton(page)).toHaveCount(0);
  });
});
