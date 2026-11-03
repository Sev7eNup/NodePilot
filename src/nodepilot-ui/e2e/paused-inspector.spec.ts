import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * Covers section 59 of docs/testing/E2ETests.md.
 *
 * PausedVariablesInspector (debug/PausedVariablesInspector.tsx) replaces the live execution
 * detail while a step sits at a breakpoint. SignalR is mocked to 404 here, so the tests rely on
 * the HTTP polling fallback in useWorkflowSignalR: it lists active runs and hydrates their
 * steps, and a Running execution with a `Paused` step is enough to bring the inspector up.
 * The paused variables come from `GET /executions/{id}/paused-steps`, the same snapshot a page
 * reload uses.
 *
 * The SPA renders English under Playwright.
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

  /** A Running execution paused at step-a, as the HTTP hydration fallback sees it. */
  async function mockPausedRun(page: Page, variables: Record<string, string>) {
    const sink: { body: { stepId?: string; mode?: string; overrides?: Record<string, string> } | null } = { body: null };
    // A predicate route matches the activeOnly query variant so it wins over the empty catch-all.
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
    await page.route(`**/api/executions/${EXEC_ID}/paused-steps`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          stepId: 'step-a', stepLabel: 'Probe', pausedAt: new Date(Date.now() - 10_000).toISOString(),
          reason: 'breakpoint', variables,
        }]),
      }),
    );
    await page.route(`**/api/executions/${EXEC_ID}/resume`, (route) => {
      sink.body = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: true }) });
    });
    return sink;
  }

  /** Waits for the hydration tick to find the paused run and opens its inspector. */
  async function openInspector(page: Page) {
    await page.goto(`/workflows/${WF_ID}`);
    await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 15_000 });
    // The periodic hydration tick runs every 10 s. Once it finds the paused run the panel
    // auto-expands and switches to the Live tab (anyStepPaused effect), showing a Paused badge
    // in the accordion header. The timeout allows more than one poll interval.
    const pausedBadge = page.getByText('Paused', { exact: true }).first();
    await expect(pausedBadge).toBeVisible({ timeout: 25_000 });
    // Expanding the accordion item mounts LiveExecutionDetail, which renders the
    // PausedVariablesInspector instead of the normal detail view because a Paused step exists.
    await pausedBadge.click();
    await expect(page.getByText(/paused at/i).first()).toBeVisible({ timeout: 10_000 });
  }

  test('59.1 — a paused run surfaces the inspector with resume controls; Continue POSTs /resume', async ({ page }) => {
    const resume = await mockPausedRun(page, {});
    await openInspector(page);

    // Inspector resume controls.
    const continueBtn = page.getByRole('button', { name: /^continue$/i });
    await expect(continueBtn).toBeVisible();
    await expect(page.getByRole('button', { name: /step over/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^stop$/i })).toBeVisible();

    // Continue sends a resume POST with mode=continue; no overrides, none are editable here.
    await continueBtn.click();
    await expect.poll(() => resume.body, { timeout: 10_000 }).not.toBeNull();
    expect(resume.body!.mode).toBe('continue');
    expect(resume.body!.stepId).toBe('step-a');
  });

  test('59.2 — paused variables come from the snapshot; only run inputs and step results are editable', async ({ page }) => {
    const resume = await mockPausedRun(page, { 'manual.target': 'srv01', 'globals.Env': 'prod' });
    await openInspector(page);

    await expect(page.getByRole('textbox', { name: 'globals.Env' })).toHaveAttribute('readonly', '');
    await page.getByRole('textbox', { name: 'manual.target' }).fill('srv02');
    await page.getByRole('button', { name: /^continue$/i }).click();

    await expect.poll(() => resume.body, { timeout: 10_000 }).not.toBeNull();
    expect(resume.body!.overrides).toEqual({ 'manual.target': 'srv02' });
  });
});
