import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md Teil 70 — Lint Panel (lines 3866-3884).
 *
 * lintWorkflow() runs on every graph change; its count surfaces as the toolbar "lint pill"
 * (accessible name = the count, title = "{n} errors, {m} warnings"). Clicking the pill opens
 * the LintPanel ("Workflow validation") which lists each issue; clicking an issue jumps to the
 * node; X closes the panel. With zero issues the pill is not rendered at all (EditorHeader
 * only renders it when lintCount > 0) — so "no issues" is asserted as the absence of the pill.
 *
 * Seeded lint issue: an isolated node (no in/out edges, not a trigger) → error `isolated-node`.
 *
 * Hermetic: page.route mocks. Workflow locked-by-me. SPA renders ENGLISH under Playwright.
 */

const WF_ID = 'e7070707-7070-7070-7070-707070707070';

function definition(withIssue: boolean) {
  const nodes = [
    // Roots are trigger-only — the workflow must start at a trigger, otherwise lintWorkflow would
    // (correctly) emit a `no-trigger` error and break the "clean = no pill" assertion (70.2).
    { id: 'step-trigger', type: 'activity', position: { x: 40, y: -140 },
      data: { label: 'Start', activityType: 'manualTrigger', config: {} } },
    { id: 'step-a', type: 'activity', position: { x: 40, y: 40 },
      data: { label: 'Producer', activityType: 'runScript', config: { script: 'x' } } },
    { id: 'step-b', type: 'activity', position: { x: 40, y: 220 },
      data: { label: 'Consumer', activityType: 'log', config: { message: 'hi' } } },
  ];
  if (withIssue) {
    // Isolated node: connected by nothing → lintWorkflow reports an `isolated-node` error.
    nodes.push({ id: 'step-orphan', type: 'activity', position: { x: 40, y: 400 },
      data: { label: 'Lonely Step', activityType: 'log', config: { message: 'nobody calls me' } } });
  }
  return JSON.stringify({
    nodes,
    edges: [
      { id: 'edge-ta', source: 'step-trigger', target: 'step-a', type: 'labeled',
        data: { label: '', condition: '', disabled: false } },
      { id: 'edge-ab', source: 'step-a', target: 'step-b', type: 'labeled',
        data: { label: '', condition: '', disabled: false } },
    ],
  });
}

function workflowJson(withIssue: boolean) {
  return JSON.stringify({
    id: WF_ID, name: 'WF-Lint', description: '', isEnabled: false,
    checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username,
    checkedOutAt: '2026-06-01T00:00:00.000Z',
    definitionJson: definition(withIssue), version: 1,
  });
}

async function openEditor(page: Page, withIssue: boolean) {
  await page.route(`**/api/workflows/${WF_ID}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: workflowJson(withIssue) }),
  );
  await page.goto(`/workflows/${WF_ID}`);
  await expect(page.locator('.react-flow__node[data-id="step-a"]')).toBeVisible({ timeout: 20_000 });
}

test.describe('Lint Panel (Teil 70)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  test('70.1 — lint pill opens the panel listing the issue; X closes it', async ({ page }) => {
    await openEditor(page, true);

    // The lint pill carries title "{errors} errors, {warnings} warnings".
    const pill = page.getByTitle(/\d+ errors,\s*\d+ warnings/i);
    await expect(pill).toBeVisible({ timeout: 10_000 });

    await pill.click();

    // Panel opens with its header + the seeded isolated-node issue.
    const panelHeading = page.getByRole('heading', { name: /workflow validation|workflow-validierung/i });
    await expect(panelHeading).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/Lonely Step|nicht mit dem Graph|not connected/i).first()).toBeVisible();
    // The issue code chip is rendered uppercased.
    await expect(page.getByText('ISOLATED-NODE', { exact: false }).first()).toBeVisible();

    // Close via the panel's X button. The LintPanel root is the floating container that holds
    // the heading; its close button carries aria-label "Close".
    const panel = panelHeading.locator('xpath=ancestor::div[contains(@class,"absolute")][1]');
    await panel.getByRole('button', { name: /close|schließen/i }).first().click();
    await expect(panelHeading).toHaveCount(0, { timeout: 10_000 });
  });

  test('70.1b — clicking an issue keeps the editor stable and selects/centres the node', async ({ page }) => {
    await openEditor(page, true);

    const consoleErrors: string[] = [];
    page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });

    await page.getByTitle(/\d+ errors,\s*\d+ warnings/i).click();
    const issueRow = page.getByRole('button').filter({ hasText: /Lonely Step|nicht mit dem Graph|not connected/i }).first();
    await expect(issueRow).toBeVisible({ timeout: 10_000 });
    await issueRow.click();

    // Jumping to the node must not crash the editor (no React render error).
    expect(consoleErrors.join('\n')).not.toMatch(/Cannot read|is not a function|Maximum update depth/i);
  });

  test('70.2 — a clean workflow shows no lint pill at all', async ({ page }) => {
    await openEditor(page, false);

    // With no lint issues, EditorHeader renders no pill (lintCount === 0 → button omitted).
    await expect(page.getByTitle(/\d+ errors,\s*\d+ warnings/i)).toHaveCount(0);
    // And the panel cannot be open.
    await expect(page.getByRole('heading', { name: /workflow validation|workflow-validierung/i })).toHaveCount(0);
  });
});
