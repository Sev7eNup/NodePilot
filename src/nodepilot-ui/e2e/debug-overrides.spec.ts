import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md part 39 - debug variable overrides.
 *
 * A debug run (POST /execute with { debug: true }) makes the engine honour breakpoints. On a
 * pause, SignalR delivers `StepPaused` with the redacted variable snapshot, the editor renders
 * PausedVariablesInspector, and Continue / Step Over / Stop POST /api/executions/{id}/resume
 * with { stepId, mode, overrides }.
 *
 * SignalR is mocked to 404 in this harness and the REST hydration fallback drops
 * `pausedVariables`, so the inspector never gets editable variables. Only the debug-run entry
 * point is asserted here; the resume body carrying overrides is skipped.
 */

const WF_ID = 'c39c39c3-0000-0000-0000-00000000c39c';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Debug',
    description: '',
    // Enabled so handleRunClick doesn't alert+bail before firing /execute.
    isEnabled: true,
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

// Two-step workflow with a breakpoint on step B (data.breakpoint:true), as in part 39's setup.
const DEF = JSON.stringify({
  nodes: [
    { id: 'stepA', type: 'activity', position: { x: 40, y: 40 }, data: { label: 'Produce', activityType: 'runScript', outputVariable: 'stepA', config: { script: "Write-Output 'real'" } } },
    { id: 'stepB', type: 'activity', position: { x: 280, y: 40 }, data: { label: 'Consume', activityType: 'log', breakpoint: true, config: { message: '{{stepA.output}}' } } },
  ],
  edges: [{ id: 'e1', source: 'stepA', target: 'stepB', type: 'labeled', data: {} }],
});

test.describe('Debug — Variable Overrides (Teil 39)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('39.entry — Debug-run posts /execute with debug:true (breakpoint pausing on)', async ({ page }) => {
    const sink: { body: Record<string, unknown> | null } = { body: null };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(DEF) }),
    );
    await page.route(`**/api/workflows/${WF_ID}/execute`, (route) => {
      sink.body = route.request().postDataJSON();
      return route.fulfill({
        status: 202, contentType: 'application/json',
        body: JSON.stringify({ id: 'exec-debug-1', workflowId: WF_ID, status: 'Pending', startedAt: '2026-06-01T00:00:00.000Z', completedAt: null, triggeredBy: MOCK_USER.username, errorMessage: null, returnData: null, inputParametersJson: null }),
      });
    });

    // The Debug-run button is expert-only — seed expert mode before boot.
    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'stepB')).toBeVisible({ timeout: 15_000 });

    // The Debug-run button (aria-label "Debug run — pauses at breakpoints").
    const debugBtn = page.getByRole('button', { name: /debug run/i });
    await expect(debugBtn).toBeVisible({ timeout: 10_000 });
    await debugBtn.click();

    await expect.poll(() => sink.body, { timeout: 10_000 }).not.toBeNull();
    // Without manualTrigger parameters the run starts directly with debug:true (no run dialog).
    expect(sink.body).toMatchObject({ debug: true });
  });

  // The override-send contract (resume body { mode, overrides:{...} }) needs SignalR, which is
  // mocked 404 here, so the inspector never presents editable variables. Covered by the Engine
  // and Api backend tests and by the StepTestPanel/ExecutionPanel vitest suites instead.
  test.skip('39.1 — resume sends { mode:"continue", overrides:{ "stepA.output":"mocked" } }', () => {
    // Intentionally empty; the skip reason is above.
  });
});
