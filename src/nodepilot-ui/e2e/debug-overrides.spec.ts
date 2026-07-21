import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 39 — Debug: Variable Overrides (lines 3136-3157).
 *
 * What the feature does:
 *   - A debug run (POST /execute with { debug: true }) lets the engine honour breakpoints.
 *   - When a step pauses, the engine emits a SignalR `StepPaused` event carrying the (redacted)
 *     variable snapshot. The editor renders PausedVariablesInspector with editable variable rows
 *     + Continue / Step Over / Stop buttons.
 *   - Continue/Step Over/Stop POST /api/executions/{id}/resume with
 *     { stepId, mode, overrides }, where `overrides` = the variables the user edited.
 *
 * HERMETIC LIMITATION (documented per playbook):
 *   The pause state AND the override variable VALUES arrive exclusively over SignalR
 *   (`StepPaused.variables`). SignalR is mocked to 404 in this harness, so a live pause never
 *   reaches the client through the normal path. The REST hydration fallback
 *   (useSignalR.hydrateActive → GET /executions/{id}/steps) maps step rows but DROPS the
 *   `pausedVariables`/`pausedAt`/`pausedReason` fields (they are not part of ApiStepItem) — so
 *   even a REST-seeded Paused step renders the inspector with ZERO editable variables, leaving
 *   nothing to override. The resume-with-overrides BODY is therefore not reachable hermetically.
 *
 * So this file asserts what IS observable:
 *   39.entry — the Debug-run button (the override flow's entry point) POSTs /execute with
 *              debug:true. This is the documented contract that turns on breakpoint pausing.
 *   39.1 (resume body w/ overrides) — SKIPPED with the reason above; it needs SignalR.
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

// A 2-step workflow with a breakpoint on step B (data.breakpoint:true), matching Teil 39's setup.
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
    // No manualTrigger parameters → runs directly with debug:true (no run dialog).
    expect(sink.body).toMatchObject({ debug: true });
  });

  // The override-send contract (resume body { mode, overrides:{...} }) is SignalR-gated.
  // SignalR is mocked 404 here and REST hydration drops `pausedVariables`, so the inspector
  // can never present editable variables to override. Covered by NodePilot.Engine.Tests /
  // NodePilot.Api.Tests (resume + override semantics) + the StepTestPanel/ExecutionPanel vitest
  // suites instead. See file header for the full mechanism.
  test.skip('39.1 — resume sends { mode:"continue", overrides:{ "stepA.output":"mocked" } }', () => {
    // Intentionally empty — see skip reason above (SignalR-driven pause + override values).
  });
});
