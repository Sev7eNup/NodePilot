import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 4 — Edges (Verbindungen) & Bedingungen (lines 558-746).
 *
 * Hermetic: page.route() mocks only. Workflow is locked-by-me → editable (State B), so the
 * edge-properties panel, the condition builder, the disable toggle and delete are all live.
 *
 * Creating an edge by DRAGGING from one node handle to another is React-Flow d3-drag and is
 * NOT synthesizable with Playwright (4.1 create-via-drag is skipped with a reason). Instead we
 * PRE-SEED edges in definitionJson and exercise selection / condition-editing / disable / delete
 * — which is where the actual logic + persistence lives.
 *
 * The condition editor (ConditionBuilder.tsx) renders comparison operators as <option> labels
 * via OP_LABELS: == "equals", != "not equals", < "less than", > "greater than", <= "≤",
 * >= "≥", contains, startsWith "starts with", endsWith "ends with", matches "matches regex",
 * isEmpty "is empty", isNotEmpty "is not empty", isTrue "is true", isFalse "is false".
 * Unary ops (isEmpty/isNotEmpty/isTrue/isFalse) hide the right-hand operand picker.
 * AND/OR group-operator buttons appear only when a group has >1 child; NOT renders for a
 * pre-seeded `not` node. The SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'e4e4e4e4-4444-4444-4444-444444444444';
const NODE_A = 'step-src';
const NODE_B = 'step-dst';
const EDGE_ID = 'edge-main';

interface DefOverrides {
  edgeData?: Record<string, unknown>;
}

function definition({ edgeData }: DefOverrides = {}) {
  return JSON.stringify({
    nodes: [
      {
        id: NODE_A,
        type: 'activity',
        position: { x: 60, y: 60 },
        // outputVariable so the upstream-variable list for the edge has a named step to pick.
        data: { label: 'Producer', activityType: 'runScript', outputVariable: 'step1', config: { script: 'x' } },
      },
      {
        id: NODE_B,
        type: 'activity',
        position: { x: 320, y: 60 },
        data: { label: 'Consumer', activityType: 'delay', config: { seconds: 1 } },
      },
    ],
    edges: [
      {
        id: EDGE_ID,
        source: NODE_A,
        target: NODE_B,
        type: 'labeled',
        data: edgeData ?? { label: 'On Success', condition: `${NODE_A}.success`, disabled: false },
      },
    ],
  });
}

function workflowJson(defOverrides: DefOverrides = {}, overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Edges',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(defOverrides),
    version: 1,
    ...overrides,
  });
}

async function waitForCanvas(page: Page) {
  await expect(page.locator('.react-flow__node')).toHaveCount(2, { timeout: 15_000 });
  await expect(page.locator('.react-flow__edge')).toHaveCount(1);
}

/** Click the seeded edge → opens the Connection (EdgePropertiesPanel).
 *
 * A horizontal edge's interaction path has a zero-height bounding box, so a normal element
 * click degenerates to an unclickable point. We instead aim the mouse at the geometric
 * midpoint of the path's box — a point that lies on the line and bubbles onEdgeClick. */
async function selectEdge(page: Page) {
  const heading = page.getByRole('heading', { name: /^connection$|^verbindung$/i });
  const interaction = page.locator(`.react-flow__edge[data-id="${EDGE_ID}"] .react-flow__edge-interaction`);
  await interaction.waitFor({ state: 'attached', timeout: 10_000 }); // SVG path: attached, not "visible"
  await page.waitForTimeout(500); // let the load-time fitView animation settle before measuring
  // The fitView animation can shift the edge between measuring its box and the click landing, so a
  // single centre click occasionally misses. Re-measure + click until the Connection panel opens.
  for (let i = 0; i < 8; i++) {
    const box = await interaction.boundingBox();
    if (box) {
      await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
      if (await heading.isVisible().catch(() => false)) return;
    }
    await page.waitForTimeout(250);
  }
  await expect(heading).toBeVisible({ timeout: 5_000 });
}

const ALL_OPERATOR_LABELS = [
  'equals', 'not equals', 'less than', 'greater than', '≤', '≥',
  'contains', 'starts with', 'ends with', 'matches regex',
  'is empty', 'is not empty', 'is true', 'is false',
];

test.describe('Designer Edges & Bedingungen (Teil 4)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('4.1 — pre-seeded edge renders, is selectable and shows source→target (create-by-drag skipped)', async ({ page }) => {
    test.skip(false, 'create-edge-by-handle-drag is not synthesizable in React Flow; pre-seeded edge covers render/select/label');
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() }),
    );

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);

    // Edge is visually rendered with its label.
    await expect(page.locator(`.react-flow__edge[data-id="${EDGE_ID}"]`)).toBeVisible();
    await expect(page.getByText('On Success').first()).toBeVisible();

    // Selectable → opens the Connection panel showing the producer → consumer endpoints.
    await selectEdge(page);
    const panel = page.getByRole('heading', { name: /^connection$|^verbindung$/i }).locator('../..');
    await expect(panel.getByText('Producer')).toBeVisible();
    await expect(panel.getByText('Consumer')).toBeVisible();
  });

  test('4.2 — comparison editor offers all 14 operators; unary ops hide the right operand', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);

    // Switch the condition editor from "Simple" to "Expression" → ConditionBuilder mounts.
    await page.getByRole('button', { name: /^expression$/i }).click();
    // Add one comparison row (button label "Condition").
    await page.getByRole('button', { name: /^\s*condition\s*$/i }).first().click();

    // The operator <select> must offer every comparison operator the engine supports.
    const opSelect = page.locator('select').filter({ hasText: 'equals' }).first();
    await expect(opSelect).toBeVisible();
    for (const label of ALL_OPERATOR_LABELS) {
      await expect(opSelect.locator('option', { hasText: new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`) }))
        .toHaveCount(1);
    }

    // Binary operator (default ==): two operand pickers (left + right) each expose Variable/Literal.
    await expect(page.getByRole('button', { name: /^literal$/i })).toHaveCount(2);

    // Switch to a unary operator → right operand picker disappears.
    await opSelect.selectOption('isEmpty');
    await expect(page.getByRole('button', { name: /^literal$/i })).toHaveCount(1);

    // Switch to a string operator → right operand returns.
    await opSelect.selectOption('contains');
    await expect(page.getByRole('button', { name: /^literal$/i })).toHaveCount(2);

    // Editing the condition marks the workflow dirty → Save round-trips a conditionExpression.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { edges: { data?: { conditionExpression?: unknown } }[] };
    expect(def.edges[0].data?.conditionExpression).toBeTruthy();
  });

  test('4.3a — pre-seeded AND/OR group exposes the AND/OR toggle and both child rows', async ({ page }) => {
    const groupExpr = {
      type: 'group',
      op: 'AND',
      children: [
        { type: 'comparison', left: { kind: 'variable', stepId: NODE_A, field: 'param', paramName: 'env' }, op: '==', right: { kind: 'literal', value: 'prod' } },
        { type: 'comparison', left: { kind: 'variable', stepId: NODE_A, field: 'param', paramName: 'debug' }, op: '==', right: { kind: 'literal', value: 'false' } },
      ],
    };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({ edgeData: { label: '', conditionExpression: groupExpr, disabled: false } }),
      }),
    );

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);
    // A pre-seeded expression opens directly in Expression mode.
    // Two children → AND / OR operator buttons are offered, AND active.
    await expect(page.getByRole('button', { name: /^and$/i })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole('button', { name: /^or$/i })).toBeVisible();

    // Two comparison rows → two operator selects + multiple literal pickers.
    await expect(page.locator('select').filter({ hasText: 'equals' })).toHaveCount(2);

    // Flip to OR and add a third condition — conditions can be added.
    await page.getByRole('button', { name: /^or$/i }).click();
    await page.getByRole('button', { name: /^\s*condition\s*$/i }).first().click();
    await expect(page.locator('select').filter({ hasText: 'equals' })).toHaveCount(3);
  });

  test('4.3b — pre-seeded NOT wrapper renders the NOT label and its inner comparison', async ({ page }) => {
    const notExpr = {
      type: 'not',
      child: { type: 'comparison', left: { kind: 'variable', stepId: NODE_A, field: 'param', paramName: 'isDev' }, op: 'isTrue' },
    };
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: workflowJson({ edgeData: { label: '', conditionExpression: notExpr, disabled: false } }),
      }),
    );

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);

    // NOT label is rendered; the inner comparison's unary op is selected (no right operand).
    // The ConditionBuilder also renders an "add NOT" action button (text "NOT") at the bottom
    // of the root group, so scope the assertion to the NOT wrapper label, not the button. The
    // wrapper label now uses the error token (text-error) instead of the old text-red-700.
    await expect(page.locator('div.text-error', { hasText: 'NOT' })).toBeVisible({ timeout: 10_000 });
    const opSelect = page.locator('select').filter({ hasText: 'equals' }).first();
    await expect(opSelect).toBeVisible();
    await expect(opSelect).toHaveValue('isTrue');
  });

  test('4.4 — disabling an edge toggles the panel state and persists disabled:true', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);

    // Toggle is a single button that reads "Connection is active" → click → "Connection is disabled".
    const toggle = page.getByRole('button', { name: /connection is (active|disabled)/i });
    await expect(toggle).toHaveText(/connection is active/i);
    await toggle.click();
    await expect(toggle).toHaveText(/connection is disabled/i);

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { edges: { data?: { disabled?: boolean } }[] };
    expect(def.edges[0].data?.disabled).toBe(true);
  });

  test('4.5 — deleting an edge removes it and keeps both nodes; Save persists 0 edges', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') {
        putBody = route.request().postDataJSON();
        return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
      }
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    });

    await seedExpertMode(page); // edge-properties / condition tooling lives in the expert-mode toolbar (default is standard)
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page);
    await selectEdge(page);

    await page.getByRole('button', { name: /delete connection|verbindung löschen/i }).click();
    // "Delete this connection?" confirms via the in-app ConfirmHost dialog.
    await page.getByRole('button', { name: 'OK' }).click();

    await expect(page.locator('.react-flow__edge')).toHaveCount(0, { timeout: 10_000 });
    await expect(page.locator('.react-flow__node')).toHaveCount(2); // both nodes survive

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as { nodes: unknown[]; edges: unknown[] };
    expect(def.edges).toHaveLength(0);
    expect(def.nodes).toHaveLength(2);
  });
});
