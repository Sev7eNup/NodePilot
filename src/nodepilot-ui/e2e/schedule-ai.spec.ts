import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Part 53 — Schedule Next-Fires & AI Generate-Workflow (lines 3469-3498).
 *
 * Two unrelated features bundled in Part 53:
 *
 *  A) Next-Fires (53.1 / 53.2)
 *     The backend endpoint GET /api/triggers/schedule/next-fires returns upcoming ISO fire times.
 *     IMPORTANT: the SPA's schedule-trigger config panel does NOT call this endpoint — it computes
 *     the "Next fire times" preview CLIENT-SIDE via cron-parser (lib/cronPreview.ts). So the
 *     observable UI surface is that client-side preview, which we assert here (a valid cron lists
 *     upcoming fires; an empty/invalid cron shows no future fires). We still register the endpoint
 *     mock to prove it is never the source of the rendered preview.
 *
 *  B) AI Generate-Workflow (53.3 / 53.4)
 *     WorkflowsPage → "New AI Workflow" → WorkflowGenerationDialog. Stage 1 posts
 *     /api/ai/generate-workflow; Stage 2 previews the returned definition; "Erstellen & öffnen"
 *     POSTs /api/workflows and navigates. When the LLM is disabled the endpoint returns 503
 *     { code:"LLM_DISABLED", message }, which the api client surfaces and the dialog shows in its
 *     error alert.
 *
 * Hermetic: page.route() mocks only. MOCK_USER is Admin → canWrite → the AI button renders.
 * The dialog text is hardcoded German; we target stable aria-labels + role=dialog/alert + the
 * data-testid JSON preview, which are language-agnostic.
 */

const WF_ID = 'e53e53e5-0000-0000-0000-00000000e53e';

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

function scheduleWorkflowJson(cronExpression: string) {
  const def = JSON.stringify({
    nodes: [{ id: 'sched', type: 'trigger', position: { x: 40, y: 40 }, data: { label: 'Schedule', activityType: 'scheduleTrigger', config: { cronExpression } } }],
    edges: [],
  });
  return JSON.stringify({
    id: WF_ID, name: 'WF-Schedule', description: '', isEnabled: true,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username, checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: def, version: 1,
  });
}

test.describe('Schedule Next-Fires & AI Generate-Workflow (Teil 53)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  // ---------------- A) Next-Fires (client-side preview) ----------------

  test('53.1 — schedule trigger lists upcoming fire times for a valid cron', async ({ page }) => {
    let nextFiresHit = false;
    await page.route('**/api/triggers/schedule/next-fires**', (route) => {
      nextFiresHit = true; // should stay false — the SPA computes this locally
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ fires: [], summary: '' }) });
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: scheduleWorkflowJson('0 */5 * * * ?') }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'sched')).toBeVisible({ timeout: 15_000 });
    await node(page, 'sched').click({ position: { x: 15, y: 15 } });
    await expect(page.getByText(/^Schedule$/).first()).toBeVisible({ timeout: 10_000 });

    // "Next fire times" header + at least one concrete upcoming fire ("in …") rendered locally.
    await expect(page.getByText(/next fire times/i)).toBeVisible();
    await expect(page.getByText(/in \d/).first()).toBeVisible();
    // The backend next-fires endpoint was NOT consulted by the designer.
    expect(nextFiresHit).toBe(false);
  });

  test('53.2 — empty cron shows no upcoming fires (preview empty, not crashing)', async ({ page }) => {
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: scheduleWorkflowJson('') }),
    );

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'sched')).toBeVisible({ timeout: 15_000 });
    await node(page, 'sched').click({ position: { x: 15, y: 15 } });
    await expect(page.getByText(/^Schedule$/).first()).toBeVisible({ timeout: 10_000 });

    // With an empty cron, the preview block is suppressed (cron.trim() falsy) → no fire rows.
    await expect(page.getByText(/next fire times/i)).toHaveCount(0);
  });

  // ---------------- B) AI Generate-Workflow ----------------

  async function openAiDialog(page: Page) {
    await page.goto('/workflows');
    const aiBtn = page.getByRole('button', { name: /new ai workflow/i });
    await expect(aiBtn).toBeVisible({ timeout: 15_000 });
    await aiBtn.click();
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 10_000 });
  }

  test('53.3 — generate previews the returned workflow, then Create POSTs /api/workflows', async ({ page }) => {
    const GENERATED_DEF = JSON.stringify({
      nodes: [
        { id: 't', type: 'trigger', position: { x: 0, y: 0 }, data: { label: 'Daily', activityType: 'scheduleTrigger', config: { cronExpression: '0 0 6 * * ?' } } },
        { id: 's', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'Check Disk', activityType: 'runScript', config: { script: 'Get-PSDrive C' } } },
      ],
      edges: [{ id: 'e1', source: 't', target: 's', type: 'labeled', data: {} }],
    });

    let genHit = false;
    await page.route('**/api/ai/generate-workflow', (route) => {
      genHit = true;
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          definitionJson: GENERATED_DEF,
          suggestedName: 'Daily Disk Check',
          suggestedDescription: 'Checks disk space every morning',
          nodeCount: 2, edgeCount: 1, retried: false, durationMs: 1234, model: 'gpt-test',
        }),
      });
    });

    let createBody: { name?: string; definitionJson?: string } | null = null;
    await page.route('**/api/workflows', (route) => {
      if (route.request().method() === 'POST') {
        createBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: WF_ID, name: 'Daily Disk Check', definitionJson: GENERATED_DEF, isEnabled: false, version: 1 }) });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    });
    // After create, the page navigates to /workflows/{id} — serve that workflow so it mounts.
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: WF_ID, name: 'Daily Disk Check', description: '', isEnabled: false, checkedOutByUserId: null, checkedOutByUserName: null, checkedOutAt: null, definitionJson: GENERATED_DEF, version: 1 }) }),
    );

    await openAiDialog(page);

    // Stage 1: type a prompt, click Generate (the only enabled gradient footer button).
    await page.getByLabel(/workflow prompt/i).fill('Daily check disk space on ServerA and cleanup if low');
    // Footer "Generieren" button — selected by being the submit action in stage 1.
    await page.locator('div[role="dialog"] button', { hasText: /generieren|generate/i }).first().click();

    await expect.poll(() => genHit, { timeout: 10_000 }).toBe(true);

    // Stage 2 (preview): the suggested name lands in the editable Name field; stats render.
    await expect(page.getByLabel(/workflow name/i)).toHaveValue('Daily Disk Check', { timeout: 10_000 });
    // The raw definition JSON preview is reachable via the data-testid pre.
    await page.locator('div[role="dialog"] button', { hasText: /definition json/i }).click();
    await expect(page.getByTestId('workflow-definition-json')).toContainText('scheduleTrigger');

    // Create & open → POST /api/workflows with the generated definition.
    await page.locator('div[role="dialog"] button', { hasText: /erstellen|create/i }).last().click();

    await expect.poll(() => createBody, { timeout: 10_000 }).not.toBeNull();
    expect(createBody!.name).toBe('Daily Disk Check');
    expect(createBody!.definitionJson).toContain('scheduleTrigger');
  });

  test('53.4 — LLM disabled: 503 { code:"LLM_DISABLED" } surfaces in the dialog error alert', async ({ page }) => {
    await page.route('**/api/ai/generate-workflow', (route) =>
      route.fulfill({
        status: 503, contentType: 'application/json',
        body: JSON.stringify({ code: 'LLM_DISABLED', message: 'AI features are disabled. Set Llm:Enabled=true to use them.' }),
      }),
    );

    await openAiDialog(page);
    await page.getByLabel(/workflow prompt/i).fill('Generate something');
    await page.locator('div[role="dialog"] button', { hasText: /generieren|generate/i }).first().click();

    // The api client extracts `message` from the structured error body → shown in role=alert.
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible({ timeout: 10_000 });
    await expect(alert).toContainText(/AI features are disabled/i);

    // Stayed on Stage 1 (no preview) — the Name field is absent because generation failed.
    await expect(page.getByLabel(/workflow name/i)).toHaveCount(0);
  });
});
