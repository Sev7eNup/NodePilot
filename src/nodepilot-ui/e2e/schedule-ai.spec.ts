import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, capsJson, mockCaps } from './fixtures/mockApi';

/**
 * Covers E2ETests.md part 53, which bundles two unrelated features.
 *
 * A) Next fires (53.1, 53.2): the schedule-trigger panel computes its preview locally with
 *    cron-parser (lib/cronPreview.ts) instead of calling GET /api/triggers/schedule/next-fires,
 *    so the tests assert the local preview and mock that endpoint to prove it stays unused.
 * B) AI workflow generation (53.3, 53.4): the dialog posts /api/ai/generate-workflow, previews
 *    the result, then POSTs /api/workflows; a disabled LLM answers 503 LLM_DISABLED and the
 *    message lands in the dialog's error alert.
 *
 * Hermetic mocks only. MOCK_USER is an Admin, so the AI button renders. The dialog text is
 * German, so selectors use aria-labels, role=dialog/alert and the data-testid JSON preview.
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
    // The backend next-fires endpoint is not consulted by the designer.
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

    // An empty cron suppresses the preview block (cron.trim() is falsy), so no fire rows render.
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
    // The stage 1 submit button in the footer, matched by its label text.
    await page.locator('div[role="dialog"] button', { hasText: /generieren|generate/i }).first().click();

    await expect.poll(() => genHit, { timeout: 10_000 }).toBe(true);

    // Stage 2 (preview): the suggested name lands in the editable Name field; stats render.
    await expect(page.getByLabel(/workflow name/i)).toHaveValue('Daily Disk Check', { timeout: 10_000 });
    // The raw definition JSON preview is reachable via the data-testid pre.
    await page.locator('div[role="dialog"] button', { hasText: /definition json/i }).click();
    await expect(page.getByTestId('workflow-definition-json')).toContainText('scheduleTrigger');

    // Create and open: POSTs /api/workflows with the generated definition.
    await page.locator('div[role="dialog"] button', { hasText: /erstellen|create/i }).last().click();

    await expect.poll(() => createBody, { timeout: 10_000 }).not.toBeNull();
    expect(createBody!.name).toBe('Daily Disk Check');
    expect(createBody!.definitionJson).toContain('scheduleTrigger');
  });

  test('53.4 — LLM disabled: 503 { code:"LLM_DISABLED" } surfaces in the dialog error alert', async ({ page }) => {
    // The button renders only when capabilities report llm: true (the suite default), so this
    // flow stands for the LLM being switched off after the page loaded.
    await page.route('**/api/ai/generate-workflow', (route) =>
      route.fulfill({
        status: 503, contentType: 'application/json',
        body: JSON.stringify({ code: 'LLM_DISABLED', message: 'AI features are disabled. Set Llm:Enabled=true to use them.' }),
      }),
    );

    await openAiDialog(page);
    await page.getByLabel(/workflow prompt/i).fill('Generate something');
    await page.locator('div[role="dialog"] button', { hasText: /generieren|generate/i }).first().click();

    // The api client pulls `message` from the structured error body into the role=alert element.
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible({ timeout: 10_000 });
    await expect(alert).toContainText(/AI features are disabled/i);

    // Stayed on Stage 1 (no preview) — the Name field is absent because generation failed.
    await expect(page.getByLabel(/workflow name/i)).toHaveCount(0);
  });

  test('53.5 — no usable LLM endpoint: "New AI Workflow" is hidden, "New Workflow" stays', async ({ page }) => {
    await mockCaps(page, capsJson({ llm: false, enabled: false, docs: false, operational: false, sourceCode: false, db: false }));

    await page.goto('/workflows');
    await expect(page.getByRole('button', { name: /^new workflow$/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('button', { name: /new ai workflow/i })).toHaveCount(0);
  });
});
