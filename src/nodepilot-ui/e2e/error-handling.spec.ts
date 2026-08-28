import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md section "Teil 7": error handling and edge cases. Only the editor's static
 * validation is observable in the hermetic browser; runtime behaviour such as failed executions
 * or skipped nodes belongs to NodePilot.Engine.Tests. lintWorkflow() (src/lib/workflowLint.ts)
 * runs on every graph change and shows a pill in the toolbar Run cluster with the combined
 * error and warning count; it renders whenever lintCount > 0, so no edit lock is needed here.
 */

const WF_ID = 'e7e7e7e7-0000-0000-0000-000000000007';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'ErrorCase_Test',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // locked by the current user, so the editor is editable
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson,
    version: 1,
    activityCount: 0,
    triggerTypes: [],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
    ...overrides,
  });
}

// The lint pill is an AlertTriangle button in the Run cluster. Its visible text is only the
// count, so it is matched by its `title` attribute ("N errors, M warnings"), which is unique
// to this button.
function lintPill(page: import('@playwright/test').Page) {
  return page.getByTitle(/\d+ errors?, \d+ warnings?/i);
}

test.describe('Fehlerbehandlung & Edge Cases (Teil 7)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('7.1 — template referencing an unknown variable raises a lint warning', async ({ page }) => {
    // A log node references {{unknownVariable.output}}, which no upstream node exposes, so lint
    // reports the warning "unknown-template-ref" and the pill shows at least one warning.
    const def = JSON.stringify({
      nodes: [
        { id: 't', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Trg', activityType: 'manualTrigger', config: {} } },
        { id: 'n1', type: 'activity', position: { x: 240, y: 0 }, data: { label: 'Log it', activityType: 'log', config: { message: '{{unknownVariable.output}}' } } },
      ],
      edges: [{ id: 'e1', source: 't', target: 'n1', type: 'labeled', data: {} }],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);

    const pill = lintPill(page);
    await expect(pill).toBeVisible({ timeout: 15_000 });
    // Clicking opens the lint panel listing the offending reference.
    await pill.click();
    await expect(page.getByText(/unknownVariable/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('7.2 — invalid / unfinished edge condition', async () => {
    test.skip(true, 'Condition syntax ("{{" half-template) is validated by the engine\'s expression parser at runtime, not by the static lint. The editor lets it save; surfacing requires execution (SignalR, mocked 404). Covered by NodePilot.Engine.Tests condition-parser cases.');
  });

  test('7.3 — cycle-only / trigger-less workflow surfaces a no-trigger lint error', async ({ page }) => {
    // A cycle of three nodes: every node has an incoming edge and there is no trigger. Roots are
    // trigger-only, so the engine would run nothing and lint reports a single `no-trigger` error.
    const def = JSON.stringify({
      nodes: [
        { id: 'A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A', activityType: 'log', config: { message: 'A' } } },
        { id: 'B', type: 'activity', position: { x: 200, y: 0 }, data: { label: 'B', activityType: 'log', config: { message: 'B' } } },
        { id: 'C', type: 'activity', position: { x: 400, y: 0 }, data: { label: 'C', activityType: 'log', config: { message: 'C' } } },
      ],
      edges: [
        { id: 'e1', source: 'A', target: 'B', type: 'labeled', data: {} },
        { id: 'e2', source: 'B', target: 'C', type: 'labeled', data: {} },
        { id: 'e3', source: 'C', target: 'A', type: 'labeled', data: {} },
      ],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);

    const pill = lintPill(page);
    await expect(pill).toBeVisible({ timeout: 15_000 });
    await pill.click();
    // The lint message states that the workflow has no trigger and no entry point.
    await expect(page.getByText(/keinen Trigger|Einstiegspunkt|no-trigger/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('7.4 — isolated (orphan) nodes raise lint errors', async ({ page }) => {
    // Two log nodes with no edges and no trigger: each one is disconnected, so lint reports two
    // "isolated-node" errors plus one `no-trigger` error, and the panel lists all of them.
    const def = JSON.stringify({
      nodes: [
        { id: 'n1', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'Lonely One', activityType: 'log', config: { message: 'one' } } },
        { id: 'n2', type: 'activity', position: { x: 300, y: 0 }, data: { label: 'Lonely Two', activityType: 'log', config: { message: 'two' } } },
      ],
      edges: [],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);

    const pill = lintPill(page);
    await expect(pill).toBeVisible({ timeout: 15_000 });
    await pill.click();
    // The lint panel names the disconnected nodes.
    await expect(page.getByText(/Lonely One|nicht mit dem Graph verbunden|not connected/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('7.5 — a disabled node with downstream edges raises lint warnings', async ({ page }) => {
    // A chain whose middle node is disabled. Lint flags "edge-to-disabled" and
    // "disabled-with-downstream" warnings, matching the engine skipping it and its successors.
    const def = JSON.stringify({
      nodes: [
        { id: 'A', type: 'activity', position: { x: 0, y: 0 }, data: { label: 'A', activityType: 'manualTrigger', config: {} } },
        { id: 'B', type: 'activity', position: { x: 220, y: 0 }, data: { label: 'B disabled', activityType: 'log', disabled: true, config: { message: 'B' } } },
        { id: 'C', type: 'activity', position: { x: 440, y: 0 }, data: { label: 'C', activityType: 'log', config: { message: 'C' } } },
      ],
      edges: [
        { id: 'e1', source: 'A', target: 'B', type: 'labeled', data: {} },
        { id: 'e2', source: 'B', target: 'C', type: 'labeled', data: {} },
      ],
    });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(def) }),
    );

    await page.goto(`/workflows/${WF_ID}`);

    const pill = lintPill(page);
    await expect(pill).toBeVisible({ timeout: 15_000 });
    await pill.click();
    await expect(page.getByText(/deaktivierten Step|disabled|deaktiviert/i).first()).toBeVisible({ timeout: 10_000 });
  });
});
