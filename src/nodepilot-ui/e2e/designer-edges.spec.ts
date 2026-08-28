import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, seedExpertMode } from './fixtures/mockApi';

/**
 * E2ETests.md part 4 - edges (connections) and conditions.
 *
 * Hermetic: page.route() mocks only. The workflow is locked by the current user and therefore
 * editable, so the edge-properties panel, the condition builder, the disable toggle and delete
 * are all live.
 *
 * Creating an edge by dragging from one node handle to another is React Flow d3-drag and cannot
 * be synthesized with Playwright, so edges are pre-seeded in definitionJson and the specs cover
 * selection, condition editing, disable and delete, where the logic and persistence live.
 *
 * The condition editor (ConditionBuilder.tsx) renders comparison operators as <option> labels
 * from OP_LABELS; the full list is in ALL_OPERATOR_LABELS below. Unary ops
 * (isEmpty/isNotEmpty/isTrue/isFalse) hide the right-hand operand picker, AND/OR group-operator
 * buttons appear only when a group has more than one child, and NOT renders for a pre-seeded
 * `not` node. The SPA renders English under Playwright.
 */

const WF_ID = 'e4e4e4e4-4444-4444-4444-444444444444';
const NODE_A = 'step-src';
const NODE_B = 'step-dst';
const NODE_C = 'step-alt';
const EDGE_ID = 'edge-main';

interface DefOverrides {
  edgeData?: Record<string, unknown>;
  /** Adds an unconnected third node, the re-route target for the edge-detach case (4.6). */
  withAltTarget?: boolean;
}

/**
 * The detach fixture (`withAltTarget`) stacks all three nodes in one column instead of placing
 * them side by side, for two reasons:
 *
 *  - A right-click opens the connection panel, which narrows the canvas, and
 *    `onlyRenderVisibleElements` then removes the nodes that no longer fit from the DOM,
 *    including the ones the tests click next. With one shared x coordinate nothing can
 *    overflow horizontally.
 *  - Controls float above the canvas at fixed screen positions: the New Workflow button at top
 *    centre and the MiniMap at bottom right, both of which swallow clicks. In a vertically
 *    spread column the clicked nodes sit in the middle and stay clear of them.
 */
function definition({ edgeData, withAltTarget }: DefOverrides = {}) {
  const altNode = withAltTarget
    ? [{
        id: NODE_C,
        type: 'activity',
        position: { x: 60, y: 540 },
        data: { label: 'Alternative', activityType: 'delay', config: { seconds: 2 } },
      }]
    : [];
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
        position: withAltTarget ? { x: 60, y: 300 } : { x: 320, y: 60 },
        data: { label: 'Consumer', activityType: 'delay', config: { seconds: 1 } },
      },
      ...altNode,
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

async function waitForCanvas(page: Page, nodeCount = 2) {
  await expect(page.locator('.react-flow__node')).toHaveCount(nodeCount, { timeout: 15_000 });
  await expect(page.locator('.react-flow__edge')).toHaveCount(1);
}

/** Screen coordinate that lies exactly ON the edge's SVG path. A horizontal edge's bounding
 *  box has zero height, so box-centre arithmetic degenerates; getPointAtLength + getScreenCTM
 *  works for curved edges too. Same helper as in edge-reshape.spec.ts. */
async function pointOnEdge(page: Page): Promise<{ x: number; y: number }> {
  return page.locator(`.react-flow__edge[data-id="${EDGE_ID}"] .react-flow__edge-path`).first()
    .evaluate((el) => {
      const path = el as unknown as SVGPathElement;
      const p = path.getPointAtLength(path.getTotalLength() / 2);
      const dom = path.ownerSVGElement!.createSVGPoint();
      dom.x = p.x; dom.y = p.y;
      const screen = dom.matrixTransform(path.getScreenCTM()!);
      return { x: screen.x, y: screen.y };
    });
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
    // Deliberate scope note (NOT a skip): create-edge-by-handle-drag is not synthesizable
    // in React Flow; the pre-seeded edge covers render/select/label.
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

  /**
   * Edge-Detach (Kontextmenü → „Ziel lösen" → Ziel-Node anklicken). Der Drag-Weg
   * (React Flows `edgesReconnectable`) ist d3-drag und nicht synthetisierbar; der Klick-Weg
   * ist es sehr wohl und trägt dieselbe Logik.
   *
   * Zwei getrennte Tests statt eines Durchlaufs mit zwei Runden: der Rechtsklick muss eine
   * UNSELEKTIERTE Edge treffen. Nach der ersten Runde ist sie selektiert, und dann liegen
   * `+`-Insert-Button und Reshape-Griffe genau auf dem Pfad-Mittelpunkt und schlucken den
   * Klick — die zweite Runde im selben Test lief zuverlässig in einen Timeout.
   */
  async function openDetach(page: Page) {
    await page.waitForTimeout(500); // fitView-Animation ausklingen lassen, bevor gemessen wird
    const pt = await pointOnEdge(page);
    await page.mouse.click(pt.x, pt.y, { button: 'right' });
    await page.getByText(/detach target|ziel lösen/i).click();
    await expect(page.getByTestId('edge-detach-hint')).toBeVisible();
  }

  test('4.6 — "Detach target" re-routes to the clicked node, picks the nearest port, keeps condition + label', async ({ page }) => {
    let putBody: { definitionJson?: string } | null = null;
    const body = workflowJson({ withAltTarget: true });
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') putBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page, 3);
    await openDetach(page);

    // Klickpunkt: horizontal auf dem Bottom-Handle, vertikal ein Stück DARUNTER. Drei Gründe,
    // warum er so und nicht "irgendwo im Node" gewählt ist:
    //  - horizontal mittig — an einer Ecke läge je nach Node-Proportion ein Seiten-Handle
    //    näher, und der Test sähe `left` statt `bottom`, ohne dass etwas kaputt wäre.
    //  - nicht auf dem Handle selbst: das sitzt mittig auf der Kante und ragt zur Hälfte
    //    heraus; ein Treffer startet einen Verbindungs-Drag statt `onNodeClick`.
    //  - die UNTERE Kante, nicht die obere: über dem Node schwebt der „New Workflow"-Button
    //    der Canvas und schluckt Klicks dort.
    const target = page.locator(`.react-flow__node[data-id="${NODE_C}"]`);
    const handle = (await target.locator('[data-handleid="bottom"]').boundingBox())!;
    await page.mouse.click(handle.x + handle.width / 2, handle.y + handle.height + 6);
    await expect(page.getByTestId('edge-detach-hint')).toHaveCount(0);

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as {
      edges: { source: string; target: string; targetHandle?: string; data?: { condition?: string; label?: string } }[];
    };
    expect(def.edges).toHaveLength(1);
    expect(def.edges[0].source).toBe(NODE_A);   // Quelle bleibt, nur das Ziel zieht um
    expect(def.edges[0].target).toBe(NODE_C);
    // Die eine Aussage, die kein Unit-Test treffen kann: Klickposition und persistierter
    // Port passen zusammen. Unten geklickt → unten angedockt, nicht der alte 'left'-Port.
    expect(def.edges[0].targetHandle).toBe('bottom');
    expect(def.edges[0].data?.condition).toBe(`${NODE_A}.success`);
    expect(def.edges[0].data?.label).toBe('On Success');
  });

  test('4.7 — Escape cancels a detach: edge keeps its old target and stays undirty', async ({ page }) => {
    let putSeen = false;
    const body = workflowJson({ withAltTarget: true });
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') putSeen = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page, 3);
    await openDetach(page);

    await page.keyboard.press('Escape');
    await expect(page.getByTestId('edge-detach-hint')).toHaveCount(0);

    // Die Edge zeigt unverändert auf Consumer — und der Abbruch hat den Workflow nicht
    // dirty gemacht, es gibt also nichts zu speichern.
    await selectEdge(page);
    const panel = page.getByRole('heading', { name: /^connection$|^verbindung$/i }).locator('../..');
    await expect(panel.getByText('Consumer')).toBeVisible();
    expect(putSeen).toBe(false);
  });

  test('4.7b — right-click on empty canvas cancels a detach without opening any menu', async ({ page }) => {
    let putSeen = false;
    const body = workflowJson({ withAltTarget: true });
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') putSeen = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page, 3);
    await openDetach(page);

    // Bewusst ueber den Locator statt ueber Mauskoordinaten: Playwrights Actionability-Check
    // stellt sicher, dass der Punkt wirklich die leere Pane trifft und nicht ein darueber
    // liegendes Overlay (Edge-SVG, MiniMap, Controls) — sonst laeuft der Test still ins Leere.
    await page.locator('.react-flow__pane').click({ button: 'right' });

    await expect(page.getByTestId('edge-detach-hint')).toHaveCount(0);
    // Der Rechtsklick ist vom Abbruch VERBRAUCHT — er darf kein Kontextmenue aufziehen.
    await expect(page.getByText(/detach target|ziel lösen/i)).toHaveCount(0);

    // Ursprungszustand: Edge unveraendert auf Consumer, nichts zu speichern.
    await selectEdge(page);
    const panel = page.getByRole('heading', { name: /^connection$|^verbindung$/i }).locator('../..');
    await expect(panel.getByText('Consumer')).toBeVisible();
    expect(putSeen).toBe(false);
  });

  test('4.9 — re-attaching to the CURRENT target node just moves the port', async ({ page }) => {
    // Der bisherige Ziel-Node ist ein gueltiges Detach-Ziel: nur so laesst sich die Port-Seite
    // wechseln, ohne die Edge samt Bedingung zu loeschen und neu zu ziehen. Frueher galt der
    // Klick dort als Abbruch und tat gar nichts.
    let putBody: { definitionJson?: string } | null = null;
    const body = workflowJson({ withAltTarget: true });
    await page.route(`**/api/workflows/${WF_ID}`, (route) => {
      if (route.request().method() === 'PUT') putBody = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body });
    });

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page, 3);
    await openDetach(page);

    // Auf den UNVERAENDERTEN Ziel-Node klicken, aber unterhalb seines Bottom-Handles.
    const target = page.locator(`.react-flow__node[data-id="${NODE_B}"]`);
    const handle = (await target.locator('[data-handleid="bottom"]').boundingBox())!;
    await page.mouse.click(handle.x + handle.width / 2, handle.y + handle.height + 6);
    await expect(page.getByTestId('edge-detach-hint')).toHaveCount(0);

    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => putBody, { timeout: 10_000 }).not.toBeNull();
    const def = JSON.parse(putBody!.definitionJson as string) as {
      edges: { source: string; target: string; targetHandle?: string; data?: { condition?: string } }[];
    };
    expect(def.edges).toHaveLength(1);
    expect(def.edges[0].source).toBe(NODE_A);
    expect(def.edges[0].target).toBe(NODE_B);        // Ziel-Node unveraendert …
    expect(def.edges[0].targetHandle).toBe('bottom'); // … nur der Anschlusspunkt zieht um
    expect(def.edges[0].data?.condition).toBe(`${NODE_A}.success`);
  });

  test('4.8 — undoing a re-route restores the old target AND leaves the edge clickable', async ({ page }) => {
    // Regression: `useWorkflowHistory` snapshottet React Flows Store, also den PROJIZIERTEN
    // Graphen. Der Detach-Marker `__detached` wanderte dadurch beim Undo in die rohen Edges
    // und liess LabeledEdge sie dauerhaft mit `pointerEvents: 'none'` rendern — die Edge war
    // bis zum Reload nicht mehr anklickbar. Genau das prueft dieser Test: nach dem Undo muss
    // ein Klick auf die Edge wieder das Connection-Panel oeffnen.
    const body = workflowJson({ withAltTarget: true });
    await page.route(`**/api/workflows/${WF_ID}`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body }),
    );

    await seedExpertMode(page);
    await page.goto(`/workflows/${WF_ID}`);
    await waitForCanvas(page, 3);
    await openDetach(page);

    const target = page.locator(`.react-flow__node[data-id="${NODE_C}"]`);
    const handle = (await target.locator('[data-handleid="bottom"]').boundingBox())!;
    await page.mouse.click(handle.x + handle.width / 2, handle.y + handle.height + 6);
    await expect(page.getByTestId('edge-detach-hint')).toHaveCount(0);

    await page.keyboard.press('Control+z');

    // Die Edge zeigt wieder auf Consumer — und laesst sich anklicken, ohne Reload.
    await selectEdge(page);
    const panel = page.getByRole('heading', { name: /^connection$|^verbindung$/i }).locator('../..');
    await expect(panel.getByText('Consumer')).toBeVisible();
  });
});
