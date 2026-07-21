import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 7 — Fehlerbehandlung & Edge Cases.
 *
 * These scenarios are about how the editor's STATIC validation reacts to malformed graphs —
 * the runtime behaviours ("execution Failed", "node Skipped") are engine semantics observed over
 * SignalR (mocked 404 here) and are owned by NodePilot.Engine.Tests. What IS observable in the
 * hermetic browser:
 *   - lintWorkflow() (src/lib/workflowLint.ts) runs on every graph change and surfaces a lint pill
 *     in the toolbar Run-cluster with the combined error+warning count (title "N errors, M warnings").
 *   - A graph with no (enabled) trigger — including a cycle-only graph — surfaces a `no-trigger`
 *     lint error (roots are trigger-only; there is no separate cycle banner).
 *
 * The lint pill renders whenever lintCount > 0 regardless of edit-lock, so these fixtures don't
 * need lock-by-me. The pill's accessible name is its tooltip: "{{errors}} errors, {{warnings}} warnings".
 */

const WF_ID = 'e7e7e7e7-0000-0000-0000-000000000007';

function workflowJson(definitionJson: string, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'ErrorCase_Test',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, // lock-by-me so the editor is fully mounted/editable
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

// The lint pill: an AlertTriangle button in the Run cluster. Its visible text is just the count
// number, so its accessible name is the digit — we target the descriptive `title` attribute
// ("N errors, M warnings") instead, which is unique to this button.
function lintPill(page: import('@playwright/test').Page) {
  return page.getByTitle(/\d+ errors?, \d+ warnings?/i);
}

test.describe('Fehlerbehandlung & Edge Cases (Teil 7)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('7.1 — template referencing an unknown variable raises a lint warning', async ({ page }) => {
    // A log node whose message references {{unknownVariable.output}} — no upstream node exposes
    // that name → lint code "unknown-template-ref" (warning). Pill must surface ≥1 warning.
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
    // A → B → C → A. Every node has in-degree ≥ 1 and there is no trigger → roots are trigger-only,
    // so the engine would run nothing. The lint surfaces a single `no-trigger` error (this replaces
    // the old "all nodes form a cycle" banner).
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
    // Lint message: "Workflow hat keinen Trigger. … kein Einstiegspunkt …" (code chip NO-TRIGGER).
    await expect(page.getByText(/keinen Trigger|Einstiegspunkt|no-trigger/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('7.4 — isolated (orphan) nodes raise lint errors', async ({ page }) => {
    // Two log nodes, no edges, no trigger → each is fully disconnected → "isolated-node" ERROR ×2
    // (plus one `no-trigger` error, since there's no trigger either — both are listed in the panel).
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
    // A → B(disabled) → C. Lint flags "edge-to-disabled" + "disabled-with-downstream" warnings,
    // mirroring the engine's cascade-skip of B and C. Observable as the lint pill.
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
