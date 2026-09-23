import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { installDefaultMocks } from './fixtures/mockApi';

const workflowId = 'abababab-1111-2222-3333-444444444444';
const executionId = 'bbbbbbbb-1111-2222-3333-444444444444';
const runId = 'cccccccc-1111-2222-3333-444444444444';
const startedAt = '2026-09-20T12:00:00Z';

for (const interrupted of [false, true]) {
test(`team history connects assignments, questions, evidence and full support export (${interrupted ? 'interrupted draft' : 'final report'})`, async ({ page }) => {
  await installDefaultMocks(page);
  const definitionJson = JSON.stringify({ nodes: [{ id: 'team', type: 'activity', position: { x: 0, y: 0 }, data: {
    label: 'Investigation team', activityType: 'aiAgentTeam', config: { task: 'Investigate', members: [
      { id: 'lead', role: 'Renamed current supervisor', isSupervisor: true, instructions: '', tools: [], skillIds: [] },
      { id: 'reader', role: 'Renamed current reader', instructions: '', tools: [], skillIds: [] },
    ] },
  } }], edges: [] });
  await page.route(`**/api/workflows/${workflowId}`, route => route.fulfill({ json: { id: workflowId, name: 'Team history', isEnabled: true,
    definitionJson, checkedOutByUserId: null, version: 1 } }));
  const execution = { id: executionId, workflowId, status: 'Succeeded', startedAt, completedAt: '2026-09-20T12:01:00Z',
    triggeredBy: 'manual', stepsTotal: 1, stepsCompleted: 1 };
  await page.route('**/api/executions**', route => route.request().url().includes('/steps') ? route.fallback() : route.fulfill({ json: [execution] }));
  await page.route(`**/api/executions/${executionId}/steps`, route => route.fulfill({ json: [{ id: 'step-result', stepId: 'team',
    stepName: 'Investigation team', stepType: 'aiAgentTeam', status: 'Succeeded', startedAt, completedAt: execution.completedAt }] }));
  const run = { id: runId, workflowExecutionId: executionId, stepId: 'team', status: interrupted ? 'Failed' : 'Succeeded', startedAt,
    completedAt: execution.completedAt, result: 'Supported final conclusion', error: null, modelCalls: 4, toolCalls: 3, delegations: 2,
    outcome: interrupted ? 'unassessed' : 'partial', outcomeReason: interrupted ? null : 'Counterpart unavailable; symptom established.' };
  await page.route('**/api/agents/runs?**', route => route.fulfill({ json: [run] }));
  const events: Record<string, unknown>[] = [];
  const add = (kind: string, memberId: string | null, content: unknown, toolName: string | null = null) => events.push({
    agentRunId: runId, sequence: events.length + 1, timestamp: startedAt, kind, memberId, toolName,
    content: typeof content === 'string' ? content : JSON.stringify(content),
  });
  add('run_started', null, 'Started');
  add('run_context', null, { members: [{ id: 'lead', role: 'Coordinator at run time', function: 'supervisor' }, { id: 'reader', role: 'Log researcher', function: 'reviewer' }] });
  add('tool_started', 'lead', { memberId: 'reader', task: 'Read the failing endpoint', reason: 'Distinguish service failure from policy failure' }, 'delegate');
  add('tool_started', 'reader', { script: 'Get-Service' }, 'powershell');
  add('tool_completed', 'reader', 'Evidence from application.log:12 <script>untrusted</script>', 'powershell');
  add('tool_completed', 'lead', { status: 'needs_input', content: 'Which time window?', objectionKind: 'evidence' }, 'delegate');
  add('tool_started', 'lead', { memberId: 'reader', task: 'Use the last hour', reason: 'Resolve the requested time scope' }, 'delegate');
  add('tool_completed', 'lead', { status: 'completed', content: 'Cause verified in application.log:12' }, 'delegate');
  // A full first page exercises REST catch-up and export beyond the visible page.
  for (let i = 0; i < 250; i++) { add('model_started', 'lead', 'Model started'); add('model_completed', 'lead', 'Model completed'); }
  add('run_succeeded', null, 'Succeeded');
  const cursors: number[] = [];
  await page.route(`**/api/agents/runs/${runId}/events?**`, route => {
    const after = Number(new URL(route.request().url()).searchParams.get('after'));
    cursors.push(after);
    return route.fulfill({ json: events.filter(event => Number(event.sequence) > after).slice(0, 500) });
  });
  await page.goto(`/workflows/${workflowId}`);
  await page.getByRole('button', { name: /history|historie|verlauf/i }).click();
  await page.locator(`[data-row-id="${executionId}"]`).click();
  await page.getByRole('button', { name: /Investigation team.*aiAgentTeam/ }).click();
  await page.getByRole('button', { name: /open large view|große ansicht/i }).click();
  const dialog = page.getByRole('dialog', { name: /agent|agenten/i });
  const panel = dialog.getByTestId('agent-run-trace');
  await expect(panel).toBeVisible();
  if (interrupted) {
    await expect(panel.getByTestId('agent-task-outcome')).toContainText(/not finally assessed|nicht abschließend bewertet/i);
    await expect(panel.locator('summary').filter({ hasText: /preliminary findings|vorläufige erkenntnisse/i })).toBeVisible();
    await expect(panel.locator('summary').filter({ hasText: /final result|endergebnis/i })).toHaveCount(0);
  } else {
    await expect(panel.getByTestId('agent-task-outcome')).toContainText(/partially completed|teilweise bearbeitet/i);
    await expect(panel.getByTestId('agent-task-outcome')).toContainText('Counterpart unavailable; symptom established.');
  }
  await expect.poll(() => cursors.includes(500)).toBe(true);
  await panel.getByRole('combobox', { name: /show member|mitglied anzeigen/i }).selectOption('reader');
  const first = panel.getByRole('article', { name: 'Coordinator at run time → Log researcher' }).first();
  await expect(first).toContainText('Distinguish service failure from policy failure');
  await expect(first).toContainText(/review requires follow-up|prüfung erfordert nacharbeit/i);
  await expect(panel.getByRole('article', { name: 'Coordinator at run time → Log researcher' }).last()).toContainText(/review approved|prüfung freigegeben/i);
  await first.locator('summary').filter({ hasText: /question|rückfrage/i }).click();
  await expect(first.getByText('Which time window?', { exact: true })).toBeVisible();
  await first.locator('summary').filter({ hasText: /actions and model|aktionen und modell/i }).click();
  await first.locator('summary').filter({ hasText: /PowerShell/ }).click();
  await expect(first.getByText(/Evidence from application.log:12/)).toBeVisible();
  await expect(first.locator('script')).toHaveCount(0);
  const downloadPromise = page.waitForEvent('download');
  await panel.getByRole('button', { name: /export/i }).click();
  const download = await downloadPromise;
  const exported = JSON.parse(await readFile((await download.path())!, 'utf8'));
  expect(exported.events).toHaveLength(events.length);
  expect(exported.complete).toBe(true);
  expect(exported.lastSequence).toBe(events.length);
  expect(exported.members[0].role).toBe('Coordinator at run time');
  await panel.getByRole('checkbox', { name: /technical journal|technisches protokoll/i }).check();
  await expect(panel.locator('[data-agent-sequence="4"]')).toContainText('tool_started');
  await panel.getByRole('checkbox', { name: /technical journal|technisches protokoll/i }).uncheck();
  const opened = panel.getByRole('article', { name: 'Coordinator at run time → Log researcher' }).first();
  await opened.locator('summary').filter({ hasText: /question|rückfrage/i }).click();
  await opened.locator('summary').filter({ hasText: /actions and model|aktionen und modell/i }).click();
  await opened.locator('summary').filter({ hasText: /PowerShell/ }).click();
  await dialog.screenshot({ path: `../../.runlogs/team-trace-panel${interrupted ? '-draft' : ''}.png` });
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
});
}
