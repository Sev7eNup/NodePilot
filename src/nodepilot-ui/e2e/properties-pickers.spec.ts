import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 65 — Credential- & Machine-Picker im Properties Panel (lines 3753-3768).
 *
 * Hermetic: page.route() mocks only. Workflow is locked-by-me → editable (State B).
 *
 * A remote activity (runScript is `isRemote`) renders the "Execution Context" section with two
 * DynamicTargetfield rows — Target machine + Credential. Each row is a VariableInsertField
 * (accepts a GUID / {{var}} / literal) plus a "List N" OptionsPicker chip that opens a
 * searchable popover of the fetched machines / credentials. Picking an entry inserts its `id`
 * and the row's green caption confirms the resolved friendly label.
 *
 *   65.1 — /api/machines is seeded; the machine "List" picker lists the machine and selecting
 *          it persists `targetMachineId` to the Save PUT.
 *   65.2 — /api/credentials is seeded; the credential "List" picker lists the credential and
 *          selecting it persists `credentialId` to the Save PUT.
 *
 * The SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'c6c6c6c6-6565-6565-6565-656565656565';
const MACHINE_ID = '11111111-1111-1111-1111-111111111111';
const CRED_ID = '22222222-2222-2222-2222-222222222222';

function definition() {
  return JSON.stringify({
    nodes: [{
      id: 'step-remote', type: 'activity', position: { x: 60, y: 60 },
      data: { label: 'Remote Probe', activityType: 'runScript', config: { script: 'Get-Date' } },
    }],
    edges: [],
  });
}

function workflowJson() {
  return JSON.stringify({
    id: WF_ID,
    name: 'WF-Pickers',
    description: '',
    isEnabled: false,
    checkedOutByUserId: MOCK_USER.id,
    checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(),
    version: 1,
  });
}

function node(page: Page, id: string) {
  return page.locator(`.react-flow__node[data-id="${id}"]`);
}

async function selectRemoteNode(page: Page) {
  await node(page, 'step-remote').click({ position: { x: 15, y: 15 } });
  await expect(page.getByText(/run script/i).first()).toBeVisible({ timeout: 10_000 });
}

/** Capture the last PUT body so we can assert the persisted targetMachineId / credentialId. */
function installWorkflowRoute(page: Page, capture: { body: { definitionJson?: string } | null }) {
  return page.route(`**/api/workflows/${WF_ID}`, (route) => {
    if (route.request().method() === 'PUT') {
      capture.body = route.request().postDataJSON();
      return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson() });
  });
}

test.describe('Credential- & Machine-Picker im Properties Panel (Teil 65)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/machines', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: MACHINE_ID, name: 'WEB01', hostname: 'web01.corp.local', winRmPort: 5985, useSsl: false,
          defaultCredentialId: null, tags: null, lastConnectivityCheck: null, isReachable: true,
          usedByWorkflowCount: 0, recentStepCount: 0, recentFailedStepCount: 0, activeRunCount: 0,
        }]),
      }),
    );
    await page.route('**/api/credentials', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ id: CRED_ID, name: 'svc-deploy', username: 'CORP\\svc-deploy', domain: 'CORP' }]),
      }),
    );
  });

  test('65.1 — the machine "List" picker lists the fleet and selecting persists targetMachineId', async ({ page }) => {
    const cap: { body: { definitionJson?: string } | null } = { body: null };
    await installWorkflowRoute(page, cap);

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-remote')).toBeVisible({ timeout: 15_000 });
    await selectRemoteNode(page);

    // Execution Context section is present for a remote activity.
    await expect(page.getByText(/execution context/i)).toBeVisible();

    // Two "List N" picker chips exist (machine + credential), each showing "1" available.
    const listPickers = page.getByRole('button', { name: /^list\b/i });
    await expect(listPickers).toHaveCount(2);

    // First List picker (under Target machine) → opens popover listing WEB01.
    await listPickers.first().click();
    const machineOption = page.getByRole('button', { name: /WEB01/ });
    await expect(machineOption).toBeVisible({ timeout: 5_000 });
    await machineOption.click();

    // Green resolved-label caption confirms the GUID maps to the machine.
    await expect(page.getByText(/web01\.corp\.local/i).first()).toBeVisible();

    // Save → PUT carries targetMachineId.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => {
      if (!cap.body) return null;
      const d = JSON.parse(cap.body.definitionJson as string) as { nodes: { data: Record<string, unknown> }[] };
      return d.nodes[0].data.targetMachineId ?? null;
    }, { timeout: 10_000 }).toBe(MACHINE_ID);
  });

  test('65.2 — the credential "List" picker lists credentials and selecting persists credentialId', async ({ page }) => {
    const cap: { body: { definitionJson?: string } | null } = { body: null };
    await installWorkflowRoute(page, cap);

    await page.goto(`/workflows/${WF_ID}`);
    await expect(node(page, 'step-remote')).toBeVisible({ timeout: 15_000 });
    await selectRemoteNode(page);

    await expect(page.getByText(/execution context/i)).toBeVisible();

    // Second List picker (under Credential) → opens popover listing svc-deploy.
    const listPickers = page.getByRole('button', { name: /^list\b/i });
    await expect(listPickers).toHaveCount(2);
    await listPickers.nth(1).click();
    const credOption = page.getByRole('button', { name: /svc-deploy/ });
    await expect(credOption).toBeVisible({ timeout: 5_000 });
    await credOption.click();

    // Save → PUT carries credentialId.
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => {
      if (!cap.body) return null;
      const d = JSON.parse(cap.body.definitionJson as string) as { nodes: { data: Record<string, unknown> }[] };
      return d.nodes[0].data.credentialId ?? null;
    }, { timeout: 10_000 }).toBe(CRED_ID);
  });
});
