import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

/**
 * Covers E2ETests.md part 48: output redaction seen from a read-only account.
 *
 * Hermetic page.route() mocks only. OutputRedactor masks secrets on the server before the payload
 * leaves the API, so the mocks return already-masked text and the browser never receives the
 * cleartext. The assertions cover the UI contract: the mask (***) is displayed verbatim and the
 * cleartext secret appears nowhere in the DOM. Masking does not depend on the role, so 48.1 also
 * overrides /me to a Viewer. SignalR is mocked 404, so 48.2 cannot be exercised in the browser.
 */

const VIEWER = { id: '00000000-0000-0000-0000-0000000000c0', username: 'viewer-bob', role: 'Viewer' };

const WF_ID = '48484848-0000-0000-0000-000000000048';
const EXEC_ID = 'aaaa4848-0000-0000-0000-000000000048';

const SECRET = 'hunter2';
const MASK = '***';

function workflow() {
  return {
    id: WF_ID,
    name: 'Redaction_WF',
    description: '',
    isEnabled: true,
    checkedOutByUserId: null,
    checkedOutByUserName: null,
    checkedOutAt: null,
    definitionJson: '{"nodes":[],"edges":[]}',
    version: 1,
    activityCount: 1,
    triggerTypes: [] as string[],
    createdAt: '2026-06-01T00:00:00.000Z',
    updatedAt: '2026-06-01T00:00:00.000Z',
  };
}

function execution() {
  return {
    id: EXEC_ID,
    workflowId: WF_ID,
    status: 'Succeeded',
    startedAt: '2026-06-18T10:00:00.000Z',
    completedAt: '2026-06-18T10:00:05.000Z',
    triggeredBy: 'manual',
    errorMessage: null,
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: null,
  };
}

// The server already masked the secret, so the fixture carries password=*** not the cleartext.
function steps() {
  return JSON.stringify([
    {
      id: 'step-row-1',
      stepId: 'print-secret',
      stepName: 'Print Secret',
      stepType: 'runScript',
      targetMachine: null,
      status: 'Succeeded',
      startedAt: '2026-06-18T10:00:00.000Z',
      completedAt: '2026-06-18T10:00:02.000Z',
      output: `password=${MASK}\nconnstring=Server=db;Pwd=${MASK}`,
      errorOutput: null,
      traceOutput: null,
      outputParametersJson: null,
    },
  ]);
}

async function mockExecutionsView(page: Page) {
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([workflow()]) }),
  );
  // ExecutionsPage fetches /executions?terminalOnly=true — catch the query-string variant.
  await page.route('**/api/executions**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([execution()]) }),
  );
  await page.route(`**/api/executions/${EXEC_ID}/steps`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: steps() }),
  );
}

test.describe('Output-Redaction Viewer-Perspektive (Teil 48)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('48.1 — Viewer sees the masked step output (***), the cleartext secret never appears', async ({ page }) => {
    // Lowest-privilege perspective: redaction is role-independent, so a Viewer must still see ***.
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(VIEWER) }),
    );
    await mockExecutionsView(page);

    await page.goto('/executions');

    // The terminal run row is present; expand it to load the steps.
    const row = page.getByRole('button', { name: /Redaction_WF/i });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.click();

    // The masked output renders; the cleartext secret does not (no client-side unmasking).
    const output = page.locator('pre', { hasText: /password=/ });
    await expect(output).toBeVisible({ timeout: 10_000 });
    await expect(output).toContainText(MASK);
    await expect(output).not.toContainText(SECRET);

    // The cleartext secret is absent from the whole page body, not only from the pre element.
    await expect(page.locator('body')).not.toContainText(SECRET);
  });

  test('48.1b — same masked output for an Admin (redaction is independent of role)', async ({ page }) => {
    // installDefaultMocks already authenticates an Admin (MOCK_USER). Assert parity.
    await mockExecutionsView(page);

    await page.goto('/executions');
    const row = page.getByRole('button', { name: /Redaction_WF/i });
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.click();

    const output = page.locator('pre', { hasText: /password=/ });
    await expect(output).toBeVisible({ timeout: 10_000 });
    await expect(output).toContainText(MASK);
    await expect(output).not.toContainText(SECRET);
  });

  test('48.2 — SignalR StepCompleted redaction', async () => {
    test.skip(true, 'The StepCompleted WS event carries server-redacted output, but SignalR negotiation is mocked 404 in this hermetic harness so no live event reaches the browser. Server-side redaction of the WS payload is covered by NodePilot.Api.Tests (OutputRedactor) — the same redactor path the REST /steps endpoint uses, already asserted in 48.1.');
  });
});
