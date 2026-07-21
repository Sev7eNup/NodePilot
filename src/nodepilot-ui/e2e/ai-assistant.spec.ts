import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md — AI workflow assistant (explain + edit). The violet toolbar button
 * (next to the Standard/Expert toggle) opens a docked chat panel. Asking returns a Markdown
 * reply; a change request returns a proposal with an Apply button — gated on Admin/Operator.
 *
 * Hermetic: all APIs via page.route, incl. POST /api/ai/chat. SPA renders ENGLISH under
 * Playwright. The chat mock echoes the request's baseDefinitionHash so the stale-guard passes.
 */

const WF_ID = 'a1a1a1a1-b2b2-c3c3-d4d4-e5e5e5e5e5e5';

function definition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'A', activityType: 'manualTrigger', config: {} } },
      { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
        data: { label: 'B', activityType: 'log', config: { message: 'hi' } } },
    ],
    edges: [
      { id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled',
        data: { label: '', condition: 'step-a.success', disabled: false } },
    ],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Assistant', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(), version: 1,
  });
}

function proposalDefinition() {
  return JSON.stringify({
    nodes: [
      { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
        data: { label: 'A', activityType: 'manualTrigger', config: {} } },
      { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
        data: { label: 'B', activityType: 'log', config: { message: 'hi' } } },
      { id: 'step-c', type: 'activity', position: { x: 360, y: 40 },
        data: { label: 'C', activityType: 'log', config: { message: 'new' } } },
    ],
    edges: [
      { id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled', data: { condition: 'step-a.success' } },
      { id: 'edge-ac', source: 'step-a', target: 'step-c', type: 'labeled', data: {} },
    ],
  });
}

async function mockChat(page: Page, withProposal: boolean) {
  // SSE response: one delta event (the prose reply), optionally one proposal event (echoing
  // baseDefinitionHash), then a done event.
  await page.route('**/api/ai/chat', (route) => {
    const body = JSON.parse(route.request().postData() || '{}');
    const reply = withProposal ? 'Added a **log** step C.' : 'This workflow starts manually, then logs B.';
    const frames = [
      `event: delta\ndata: ${JSON.stringify({ text: reply })}\n\n`,
      ...(withProposal
        ? [`event: proposal\ndata: ${JSON.stringify({ definitionJson: proposalDefinition(), summary: 'Add C', nodeCount: 3, edgeCount: 2, baseDefinitionHash: body.baseDefinitionHash })}\n\n`]
        : []),
      `event: done\ndata: ${JSON.stringify({ model: 'test-model', durationMs: 5 })}\n\n`,
    ].join('');
    route.fulfill({ status: 200, contentType: 'text/event-stream', body: frames });
  });
}

async function openEditor(page: Page) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('KI-Workflow-Assistent', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('opens the panel and shows a Markdown explanation', async ({ page }) => {
    await mockChat(page, false);
    await openEditor(page);

    await page.getByTestId('toggle-ai-assistant').click();
    const panel = page.getByRole('complementary', { name: /AI workflow assistant/i });
    await expect(panel).toBeVisible();

    await panel.getByRole('textbox').fill('What does this workflow do?');
    await panel.getByRole('button', { name: /send/i }).click();

    await expect(panel.getByText(/starts manually/i)).toBeVisible();
  });

  test('admin sees an Apply button for a proposal and can apply it', async ({ page }) => {
    await mockChat(page, true);
    await openEditor(page);

    await page.getByTestId('toggle-ai-assistant').click();
    const panel = page.getByRole('complementary', { name: /AI workflow assistant/i });
    await panel.getByRole('textbox').fill('Add a log step C.');
    await panel.getByRole('button', { name: /send/i }).click();

    const apply = panel.getByRole('button', { name: /^apply$/i });
    await expect(apply).toBeVisible();
    await apply.click();
    await expect(panel.getByText(/applied/i)).toBeVisible();
  });

  test('viewer can ask but cannot apply a proposal', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }) }),
    );
    await mockChat(page, true);
    await openEditor(page);

    await page.getByTestId('toggle-ai-assistant').click();
    const panel = page.getByRole('complementary', { name: /AI workflow assistant/i });
    await panel.getByRole('textbox').fill('Add a log step C.');
    await panel.getByRole('button', { name: /send/i }).click();

    await expect(panel.getByText(/reserved for Operator\/Admin/i)).toBeVisible();
    await expect(panel.getByRole('button', { name: /^apply$/i })).toHaveCount(0);
  });
});
