import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 59 — PausedVariablesInspector (lines 3632-3649).
 *
 * The PausedVariablesInspector (debug/PausedVariablesInspector.tsx) replaces the live
 * execution detail when a step is at a breakpoint. It is rendered by ExecutionPanel only when
 * the live state holds an execution with a step whose `status === 'Paused'`.
 *
 * Live state is normally SignalR-driven, and our SignalR negotiate is mocked to 404. BUT
 * useWorkflowSignalR also has a pure-HTTP polling fallback (`hydrateActive(..., 'periodic')`,
 * every LIVE_REFRESH_INTERVAL_MS = 10 s) that fetches
 *   GET /executions?workflowId=<id>&activeOnly=true
 * and hydrates each active run's steps via GET /executions/<id>/steps. By returning a Running
 * execution whose step list contains a `Paused` step, we drive the inspector into view purely
 * over HTTP — no SignalR required.
 *
 *   59.1 — VERIFIED: after the polling tick discovers the paused run and the user expands it in
 *          the Live tab, the inspector shows the "Paused at <step>" header + the
 *          Continue / Step Over / Stop resume controls; clicking Continue POSTs
 *          /executions/<id>/resume with mode=continue.
 *
 *   59.2 — SKIPPED: the editable variable rows + the override-send contract require
 *          `pausedVariables`, which is ONLY delivered by the SignalR `StepPaused` event
 *          (signalrReducer maps evt.variables → step.pausedVariables). The HTTP step-hydration
 *          path does NOT carry paused-variable snapshots, so the override surface is unreachable
 *          here. (Known finding: the variable override is sent on Resume but not propagated to
 *          downstream steps server-side — that is a backend concern, not assertable in the UI.)
 *
 * The SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'e6e6e6e6-5959-5959-5959-595959595959';
const EXEC_ID = 'f0f0f0f0-5959-5959-5959-595959595959';

function workflowJson() {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Paused',
    description: '',
    isEnabled: true,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: JSON.stringify({
      nodes: [{ id: 'step-a', type: 'activity', position: { x: 60, y: 60 }, data: { label: 'Probe', activityType: 'runScript', config: { script: 'x' } } }],
      edges: [],
    }),
    version: 1,
  });
}

test.describe('PausedVariablesInspector (Teil 59)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );
  });

  test('59.1 — a paused run surfaces the inspector with resume controls; Continue POSTs /resume', async ({ page }) => {
    let resumeBody: { stepId?: string; mode?: string; overrides?: unknown } | null = null;

    // Active-run listing (the periodic HTTP hydration fallback hits this). Must match the
    // activeOnly query variant — register a predicate route so it beats the empty catch-all.
    await page.route(
      (url) => url.pathname === '/api/executions' && url.search.includes('activeOnly'),
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify([{
            id: EXEC_ID, workflowId: WF_ID, status: 'Running',
            startedAt: new Date(Date.now() - 30_000).toISOString(), completedAt: null, errorMessage: null,
          }]),
        }),
    );

    // Step hydration for the active run → one Paused step. (pausedVariables aren't carried by
    // this path; the inspector still renders with the resume controls.)
    await page.route(`**/api/executions/${EXEC_ID}/steps`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          stepId: 'step-a', stepName: 'Probe', stepType: 'runScript', status: 'Paused',
          startedAt: new Date(Date.now() - 20_000).toISOString(), completedAt: null,
          output: null, errorOutput: null, traceOutput: null,
        }]),
      }),
    );

    // Resume endpoint — capture the POST body.
    await page.route(`**/api/executions/${EXEC_ID}/resume`, (route) => {
      resumeBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: true }) });
    });

    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 15_000 });

    // The periodic hydration tick runs every 10 s. Once it discovers the paused run, the panel
    // auto-expands + switches to the Live tab (anyStepPaused effect). The accordion header shows
    // a "Paused" badge — wait for it (allow >1 poll interval).
    const pausedBadge = page.getByText('Paused', { exact: true }).first();
    await expect(pausedBadge).toBeVisible({ timeout: 25_000 });

    // Expand the accordion item → LiveExecutionDetail mounts, and because a Paused step exists it
    // renders the PausedVariablesInspector instead of the normal detail view.
    await pausedBadge.click();

    // Inspector header + resume controls.
    await expect(page.getByText(/paused at/i).first()).toBeVisible({ timeout: 10_000 });
    const continueBtn = page.getByRole('button', { name: /^continue$/i });
    await expect(continueBtn).toBeVisible();
    await expect(page.getByRole('button', { name: /step over/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^stop$/i })).toBeVisible();

    // Continue → resume POST with mode=continue (no overrides, none editable in this path).
    await continueBtn.click();
    await expect.poll(() => resumeBody, { timeout: 10_000 }).not.toBeNull();
    expect(resumeBody!.mode).toBe('continue');
    expect(resumeBody!.stepId).toBe('step-a');
  });

  test('59.2 — variable-override send is skipped (pausedVariables only arrive via SignalR StepPaused, unreachable over HTTP mocks)', async () => {
    test.skip(true, 'PausedVariablesInspector edits/overrides need step.pausedVariables, populated ONLY by the SignalR StepPaused event (signalrReducer). The HTTP step-hydration fallback does not carry paused-variable snapshots, and SignalR is mocked to 404, so the override-send surface cannot be reached. The Continue/Resume contract is covered by 59.1.');
  });
});
