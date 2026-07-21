import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Part 6 — Workflow Execution & Debugging.
 *
 * Hermetic constraints (see fixtures/mockApi.ts + the team playbook):
 *   - SignalR is mocked to 404, so live PROGRESS (Running→Completed, step pulses, the green
 *     "Test-Verlauf" banner, the live Cancel button) never arrives in the browser. Execution
 *     scenarios therefore assert the UI-observable side: the /execute request fires with the
 *     right body (parameters / debug flag) and the editor enters the submitted state.
 *   - React Flow canvas drag is not synthesizable — every node/edge is pre-seeded via
 *     definitionJson, never dropped from the library.
 *
 * The Test button (Play icon, aria-label "Test run") fires handleRunClick(false) → POST
 * /execute {debug:false}. The Debug button (Bug icon, "Debug run …") fires handleRunClick(true)
 * → {debug:true}. A manualTrigger WITH parameters opens RunWorkflowDialog first; the dialog's
 * "Run" submit posts {parameters, debug}. handleRunClick early-returns with an alert() when the
 * workflow is disabled, so every executable fixture sets isEnabled:true.
 */

const WF_ID = 'e6e6e6e6-0000-0000-0000-000000000006';

function workflowJson(overrides: Record<string, unknown> = {}) {
  return {
    id: WF_ID,
    name: 'Exec_Test',
    description: '',
    isEnabled: true, // handleRunClick alerts + bails on a disabled workflow
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    activityCount: 0,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  };
}

// log → delay → log, no trigger, no parameters → Test runs directly (no dialog).
const SIMPLE_DEF = JSON.stringify({
  nodes: [
    { id: 'n1', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Start', activityType: 'log', config: { message: 'Starting' } } },
    { id: 'n2', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Wait', activityType: 'delay', config: { delaySeconds: 5 } } },
    { id: 'n3', type: 'activity', position: { x: 440, y: 0 }, data: { label: 'End', activityType: 'log', config: { message: 'Done' } } },
  ],
  edges: [
    { id: 'e1', source: 'n1', target: 'n2', type: 'labeled', data: {} },
    { id: 'e2', source: 'n2', target: 'n3', type: 'labeled', data: {} },
  ],
});

// manualTrigger with two parameters → Test opens RunWorkflowDialog.
const PARAM_DEF = JSON.stringify({
  nodes: [
    {
      id: 'trg', type: 'activity', position: { x: 0, y: 0 },
      data: {
        label: 'Manual', activityType: 'manualTrigger',
        config: {
          title: 'Run Exec_Test',
          parameters: [
            { name: 'environmentType', type: 'string', required: true, default: 'dev' },
            { name: 'deployCount', type: 'number', required: false, default: '1' },
          ],
        },
      },
    },
    { id: 'log', type: 'activity', position: { x: 260, y: 0 }, data: { label: 'Log', activityType: 'log', config: { message: 'Deploying to {{param.environmentType}} (count: {{param.deployCount}})' } } },
  ],
  edges: [{ id: 'e1', source: 'trg', target: 'log', type: 'labeled', data: {} }],
});

// manualTrigger with a single `result` parameter → fans out to three log branches by condition.
const BRANCH_DEF = JSON.stringify({
  nodes: [
    { id: 'trg', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Manual', activityType: 'manualTrigger', config: { title: 'Branch Test', parameters: [{ name: 'result', type: 'string', required: true, default: 'success' }] } } },
    { id: 'proc', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Processing', activityType: 'log', config: { message: 'Processing' } } },
    { id: 'ok', type: 'activity', position: { x: 440, y: -80 }, data: { label: 'Success', activityType: 'log', config: { message: 'Success!' } } },
    { id: 'err', type: 'activity', position: { x: 440, y: 0 }, data: { label: 'Failed', activityType: 'log', config: { message: 'Failed!' } } },
    { id: 'unk', type: 'activity', position: { x: 440, y: 80 }, data: { label: 'Unknown', activityType: 'log', config: { message: 'Unknown' } } },
  ],
  edges: [
    { id: 'e0', source: 'trg', target: 'proc', type: 'labeled', data: {} },
    { id: 'e1', source: 'proc', target: 'ok', type: 'labeled', data: { condition: '{{param.result}} == "success"' } },
    { id: 'e2', source: 'proc', target: 'err', type: 'labeled', data: { condition: '{{param.result}} == "error"' } },
    { id: 'e3', source: 'proc', target: 'unk', type: 'labeled', data: { condition: 'fallback' } },
  ],
});

// log → delay (breakpoint candidate) → log.
const DEBUG_DEF = JSON.stringify({
  nodes: [
    { id: 's1', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Step 1', activityType: 'log', config: { message: 'Step 1' } } },
    { id: 's2', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Step 2', activityType: 'delay', config: { delaySeconds: 5 } } },
    { id: 's3', type: 'activity', position: { x: 440, y: 0 }, data: { label: 'Step 3', activityType: 'log', config: { message: 'Step 3' } } },
  ],
  edges: [
    { id: 'e1', source: 's1', target: 's2', type: 'labeled', data: {} },
    { id: 'e2', source: 's2', target: 's3', type: 'labeled', data: {} },
  ],
});

/** Capture every POST to /workflows/{id}/execute and return 202 + an executionId. */
async function mockExecute(page: import('@playwright/test').Page, sink: { body: Record<string, unknown> | null }) {
  await page.route(`**/api/workflows/${WF_ID}/execute`, (route) => {
    sink.body = route.request().postDataJSON();
    return route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ id: 'exec-1111', workflowId: WF_ID, status: 'Pending', startedAt: '2026-06-01T00:00:00.000Z', completedAt: null, triggeredBy: MOCK_USER.username, errorMessage: null, traceId: null, spanId: null, returnData: null, inputParametersJson: null }),
    });
  });
}

test.describe('Workflow-Ausführung & Debugging (Teil 6)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('6.1 — execute a simple workflow fires POST /execute (debug:false)', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ definitionJson: SIMPLE_DEF, activityCount: 3 })) }),
    );
    await mockExecute(page, sink);

    await page.goto(`/workflows/${WF_ID}`);

    const testBtn = page.getByRole('button', { name: /test run/i });
    await expect(testBtn).toBeVisible({ timeout: 15_000 });
    await testBtn.click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ debug: false });
  });

  test('6.2 — manualTrigger parameters: dialog opens and Run posts the entered values', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ definitionJson: PARAM_DEF, triggerTypes: ['manualTrigger'], activityCount: 2 })) }),
    );
    await mockExecute(page, sink);

    await page.goto(`/workflows/${WF_ID}`);

    await page.getByRole('button', { name: /test run/i }).click();

    // RunWorkflowDialog (role="dialog") shows a field per declared parameter, prefilled with defaults.
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    const env = dialog.getByRole('textbox').first();
    await expect(env).toHaveValue('dev'); // default prefill
    await env.fill('prod');
    await dialog.getByRole('spinbutton').fill('3'); // type:number renders <input type=number>

    await dialog.getByRole('button', { name: /^run$/i }).click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ parameters: { environmentType: 'prod', deployCount: '3' }, debug: false });
  });

  test('6.3 — conditional branches: run dialog carries the discriminating parameter', async ({ page }) => {
    // The per-branch routing (only the matching log "Completed", others "Skipped") is engine
    // behaviour observed over SignalR — unreachable here. The UI-observable contract is that the
    // run dialog surfaces the `result` discriminator and the execute call carries it downstream.
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ definitionJson: BRANCH_DEF, triggerTypes: ['manualTrigger'], activityCount: 5 })) }),
    );
    await mockExecute(page, sink);

    await page.goto(`/workflows/${WF_ID}`);
    await page.getByRole('button', { name: /test run/i }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    const resultField = dialog.getByRole('textbox').first();
    await resultField.fill('error');
    await dialog.getByRole('button', { name: /^run$/i }).click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ parameters: { result: 'error' } });
  });

  test('6.4 — parallel fan-out timing', async () => {
    test.skip(true, 'Fan-out parallelism + junction waitAll timing is pure engine behaviour observed over SignalR (mocked 404 here). Covered by NodePilot.Engine.Tests.');
  });

  test('6.5 — breakpoint toggle works and Debug executes with debug:true', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workflowJson({ definitionJson: DEBUG_DEF, checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username, checkedOutAt: '2026-06-01T00:00:00.000Z', isEnabled: true, activityCount: 3 })) }),
    );
    await mockExecute(page, sink);

    // Breakpoints + the Debug-run button are expert-only — seed expert mode before boot.
    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);

    // Right-click the delay node to open the NodeContextMenu. Nodes carry their label text, so we
    // target the rendered node by its label and open the menu there (canWrite, lock-by-me fixture).
    const node = page.locator('.react-flow__node', { hasText: 'Step 2' });
    await expect(node).toBeVisible({ timeout: 15_000 });
    await node.click({ button: 'right' });

    // Context menu offers "Add breakpoint" (toggles data.breakpoint). Clicking it sets the
    // breakpoint, which renders a red BreakpointBadge on the node (title "Breakpoint set …") —
    // proof the toggle stuck, asserted on the durable badge rather than a re-opened menu.
    await page.getByRole('button', { name: /add breakpoint/i }).click();
    await expect(page.getByTitle(/breakpoint set/i)).toBeVisible({ timeout: 10_000 });

    // Debug run posts debug:true so the engine honours the breakpoint.
    await page.getByRole('button', { name: /debug run/i }).click();
    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ debug: true });
  });

  test('6.6 — cancel a running execution', async () => {
    test.skip(true, 'The Cancel button only renders while liveExecution.status === "Running", which is pushed over SignalR (mocked 404). The cancel POST /executions/{id}/cancel itself is exercised in execution-retry.spec.ts / NodePilot.Api.Tests.');
  });

  test('6.7 — retry a failed execution from the editor', async () => {
    test.skip(true, 'No frontend retry control exists — POST /executions/{id}/retry is API-only (np CLI + REST). Contract covered in execution-retry.spec.ts notes + NodePilot.Api.Tests Teil 49.');
  });
});
