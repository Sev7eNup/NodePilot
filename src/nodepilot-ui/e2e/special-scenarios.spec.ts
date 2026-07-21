import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 9 — Spezielle Szenarien.
 *
 * Hermetic constraints (fixtures/mockApi.ts + playbook): SignalR is 404, so anything that depends
 * on live execution progress (parent↔child timeline, schedule-trigger firing) is engine behaviour
 * owned by NodePilot.Engine.Tests. What IS observable:
 *   - 9.1 / 9.2 — the designer renders the seeded nodes and the manual Test run posts /execute.
 *   - 9.3 — a 50-node workflow opens and the canvas mounts without timing out.
 *   - 9.4 — the editor Export button hits GET /workflows/{id}/export.
 *
 * Nodes/edges are pre-seeded via definitionJson (canvas drag is not synthesizable). Executable
 * fixtures set isEnabled:true so handleRunClick doesn't alert+bail.
 */

const WF_ID = 'e9e9e9e9-0000-0000-0000-000000000009';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'Special_Test',
    description: 'special scenario fixture',
    isEnabled: true,
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson,
    version: 1,
    activityCount: 0,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  });
}

async function mockExecute(page: import('@playwright/test').Page, sink: { body: Record<string, unknown> | null }) {
  await page.route(`**/api/workflows/${WF_ID}/execute`, (route) => {
    sink.body = route.request().postDataJSON();
    return route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ id: 'exec-9999', workflowId: WF_ID, status: 'Pending', startedAt: '2026-06-01T00:00:00.000Z', completedAt: null, triggeredBy: MOCK_USER.username, errorMessage: null, traceId: null, spanId: null, returnData: null, inputParametersJson: null }),
    });
  });
}

test.describe('Spezielle Szenarien (Teil 9)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('9.1 — parent with a startWorkflow node runs and posts the env parameter', async ({ page }) => {
    // Parent: manualTrigger(env) → startWorkflow(Child, {environment: {{param.env}}}). The
    // parameter HANDOFF to the child is engine behaviour over SignalR (mocked) — what we assert is
    // the parent's run dialog surfaces `env` and the execute call carries it.
    const def = JSON.stringify({
      nodes: [
        { id: 'trg', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Manual', activityType: 'manualTrigger', config: { title: 'Run Parent', parameters: [{ name: 'env', type: 'string', required: true, default: 'dev' }] } } },
        { id: 'sub', type: 'activity', position: { x: 260, y: 0 }, data: { label: 'Call Child', activityType: 'startWorkflow', config: { workflowNameOrId: 'Child', parameters: { environment: '{{param.env}}' }, waitForCompletion: true } } },
      ],
      edges: [{ id: 'e1', source: 'trg', target: 'sub', type: 'labeled', data: {} }],
    });
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def, { name: 'Parent', triggerTypes: ['manualTrigger'], activityCount: 2 }) }),
    );
    await mockExecute(page, sink);

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await page.getByRole('button', { name: /test run/i }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 15_000 });
    await dialog.getByRole('textbox').first().fill('prod');
    await dialog.getByRole('button', { name: /^run$/i }).click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ parameters: { env: 'prod' } });
  });

  test('9.2 — multiple triggers: both render and the manual trigger executes', async ({ page }) => {
    // manualTrigger + scheduleTrigger fan into one log node. The schedule firing is backend; here
    // we confirm both trigger nodes render in the designer and the manual Test run fires /execute.
    const def = JSON.stringify({
      nodes: [
        { id: 'm', type: 'activity', position: { x: 0, y: -60 }, data: { label: 'Manual Start', activityType: 'manualTrigger', config: {} } },
        { id: 's', type: 'activity', position: { x: 0, y: 60 }, data: { label: 'Hourly Schedule', activityType: 'scheduleTrigger', config: { cron: '0 * * * * ? *' } } },
        { id: 'log', type: 'activity', position: { x: 260, y: 0 }, data: { label: 'Triggered', activityType: 'log', config: { message: 'Execution triggered' } } },
      ],
      edges: [
        { id: 'e1', source: 'm', target: 'log', type: 'labeled', data: {} },
        { id: 'e2', source: 's', target: 'log', type: 'labeled', data: {} },
      ],
    });
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def, { triggerTypes: ['manualTrigger', 'scheduleTrigger'], activityCount: 3 }) }),
    );
    await mockExecute(page, sink);

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);

    // Both trigger nodes are present on the canvas.
    await expect(page.locator('.react-flow__node', { hasText: 'Manual Start' })).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('.react-flow__node', { hasText: 'Hourly Schedule' })).toBeVisible();

    // No manualTrigger parameters → Test runs directly (no dialog).
    await page.getByRole('button', { name: /test run/i }).click();
    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    expect(sink.body).toMatchObject({ debug: false });
  });

  test('9.3 — a large 50-node workflow opens and mounts the canvas', async ({ page }) => {
    const nodes = [
      { id: 'trg', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Trigger', activityType: 'manualTrigger', config: {} } },
    ];
    const edges: Array<Record<string, unknown>> = [];
    for (let i = 1; i <= 49; i++) {
      nodes.push({ id: `n${i}`, type: 'activity', position: { x: (i % 10) * 200, y: Math.floor(i / 10) * 160 + 160 }, data: { label: `Step ${i}`, activityType: 'log', config: { message: `s${i}` } } });
      const src = i === 1 ? 'trg' : `n${i - 1}`;
      edges.push({ id: `e${i}`, source: src, target: `n${i}`, type: 'labeled', data: {} });
    }
    const def = JSON.stringify({ nodes, edges });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def, { name: 'Big_Workflow', triggerTypes: ['manualTrigger'], activityCount: 50 }) }),
    );

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);

    // The name lands in the editor's name field, and the canvas renders many nodes — the editor
    // is responsive (mounted without hanging) under 50 nodes. The trigger label is unique; the
    // last step (n49 → "Step 49") is unambiguous too, so no strict-mode collision.
    await expect(page.getByRole('textbox', { name: /workflow.?name/i })).toHaveValue('Big_Workflow', { timeout: 20_000 });
    await expect(page.locator('.react-flow__node', { hasText: 'Trigger' })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('rf__node-n49')).toBeVisible({ timeout: 15_000 });
    await expect.poll(async () => page.locator('.react-flow__node').count(), { timeout: 15_000 }).toBeGreaterThan(10);
  });

  test('9.4 — exporting the workflow hits GET /workflows/{id}/export', async ({ page }) => {
    const def = JSON.stringify({
      nodes: [
        { id: 't', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Trigger', activityType: 'manualTrigger', config: {} } },
        { id: 'n1', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Log', activityType: 'log', config: { message: 'hi' } } },
      ],
      edges: [{ id: 'e1', source: 't', target: 'n1', type: 'labeled', data: {} }],
    });
    let exportHit = false;
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def, { activityCount: 2 }) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/export`, (route) => {
      exportHit = true;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { 'Content-Disposition': 'attachment; filename="Special_Test.workflow.json"' },
        body: JSON.stringify({ schema: 'nodepilot-workflow-export/v1', workflow: { name: 'Special_Test', definitionJson: def } }),
      });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);

    // The Export-as-JSON action now lives in the "Werkzeuge" (Tools) menu (role=menuitem).
    await page.getByTestId('tools-menu-trigger').click();
    await page.getByRole('menuitem', { name: /export as json/i }).click();
    await expect.poll(() => exportHit, { timeout: 10_000 }).toBe(true);
  });
});
