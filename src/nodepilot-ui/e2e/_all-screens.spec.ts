import { test, type Page, type Route } from '@playwright/test';
import { installDefaultMocks } from './fixtures/mockApi';

// Throwaway: capture every mobile page with realistic data. Run:
//   npx playwright test _all-screens --config=playwright.dev.config.ts
const PHONE = { width: 390, height: 844 };
const DIR = 'e2e/__screens__';
const json = (r: Route, body: unknown) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

const WF_GRAPH = '20202020-2020-2020-2020-202020202020';

async function mockAll(page: Page) {
  await installDefaultMocks(page);
  const now = Date.now();
  const buckets = Array.from({ length: 24 }, (_, i) => ({
    hourStart: new Date(now - (23 - i) * 3600_000).toISOString(),
    succeeded: 3 + (i % 5), failed: i % 3 === 0 ? 1 : 0, cancelled: 0,
  }));
  await page.route('**/api/stats/dashboard**', (r) => json(r, {
    workflowsTotal: 12, workflowsEnabled: 9, machinesTotal: 4, machinesReachable: 3, executionsTotal: 1344,
    last24h: { total: 120, succeeded: 110, failed: 8, running: 1, cancelled: 1 },
    last24hBuckets: buckets,
    topWorkflows: [
      { id: 'a', name: 'Windows Update — Health Check', runCount: 50, successCount: 47, failCount: 3, avgDurationMs: 42000, p95DurationMs: 61000 },
      { id: 'b', name: 'Nightly Backup', runCount: 30, successCount: 28, failCount: 2, avgDurationMs: 120000, p95DurationMs: 180000 },
    ],
    running: [{ id: 'r1', workflowId: 'a', workflowName: 'Windows Update — Health Check', status: 'Running', startedAt: new Date(now - 90_000).toISOString(), triggeredBy: 'schedule' }],
    recent: [
      { id: 'e1', workflowId: 'a', workflowName: 'Windows Update — Health Check', status: 'Succeeded', startedAt: new Date(now - 3600_000).toISOString(), completedAt: new Date(now - 3540_000).toISOString(), durationMs: 42000, triggeredBy: 'schedule' },
      { id: 'e2', workflowId: 'b', workflowName: 'Nightly Backup', status: 'Failed', startedAt: new Date(now - 7200_000).toISOString(), completedAt: new Date(now - 7080_000).toISOString(), durationMs: 120000, triggeredBy: 'manual' },
    ],
    armedTriggers: [{ workflowId: 'a', workflowName: 'Windows Update — Health Check', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(now + 3600_000).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null }],
    pendingCount: 0, runningCount: 1, longRunningCount: 0,
    failingWorkflows: [{ id: 'b', name: 'Nightly Backup', failCount: 2, runCount: 30, lastFailureAt: new Date(now - 7080_000).toISOString() }],
    editLocks: [], healthHeartbeats: [{ serviceName: 'Scheduler', lastHeartbeatAt: new Date(now - 20_000).toISOString(), expectedIntervalSeconds: 60, status: 'ok', isStale: false }],
    databaseProvider: 'PostgreSQL', clusterRole: 'leader', recentAudit: [],
  }));
  await page.route('**/api/machines', (r) => json(r, [
    { id: 'm1', name: 'WEB-PROD-01', hostname: 'web01.corp.local', winRmPort: 5986, useSsl: true, defaultCredentialId: 'c1', tags: 'prod,web', lastConnectivityCheck: new Date(now - 4 * 3600_000).toISOString(), isReachable: true, usedByWorkflowCount: 6, recentStepCount: 40, recentFailedStepCount: 1, activeRunCount: 2 },
    { id: 'm2', name: 'DB-PROD-01', hostname: 'db01.corp.local', winRmPort: 5985, useSsl: false, defaultCredentialId: null, tags: 'prod,sql', lastConnectivityCheck: null, isReachable: false, usedByWorkflowCount: 0, recentStepCount: 0, recentFailedStepCount: 0, activeRunCount: 0 },
  ]));
  await page.route('**/api/credentials', (r) => json(r, [{ id: 'c1', name: 'svc-deploy', username: 'svc', domain: 'CORP' }]));
  await page.route('**/api/workflows', (r) => json(r, [
    { id: 'wf1', name: 'Windows Update — Health Check', version: 3, activityCount: 8, triggerTypes: ['scheduleTrigger'], isEnabled: true, successCount: 47, totalCount: 50, avgDurationMs: 42000, createdAt: '2026-05-01T00:00:00Z', updatedAt: '2026-06-20T00:00:00Z', createdBy: 'admin', updatedBy: 'admin' },
    { id: 'wf2', name: 'Nightly Backup', version: 1, activityCount: 4, triggerTypes: ['scheduleTrigger', 'webhookTrigger'], isEnabled: false, successCount: 8, totalCount: 10, avgDurationMs: 120000, createdAt: '2026-05-10T00:00:00Z', updatedAt: '2026-06-18T00:00:00Z', createdBy: 'admin', updatedBy: 'ops' },
  ]));
  await page.route('**/api/executions**', (r) => json(r, [
    { id: 'ex1', workflowId: 'wf1', status: 'Succeeded', startedAt: new Date(now - 5 * 3600_000).toISOString(), completedAt: new Date(now - 5 * 3600_000 + 42000).toISOString(), stepsTotal: 8, stepsCompleted: 8, failedSteps: [], triggeredBy: 'schedule', startedByUsername: 'system', traceId: null, parentExecutionId: null },
    { id: 'ex2', workflowId: 'wf2', status: 'Failed', startedAt: new Date(now - 21 * 3600_000).toISOString(), completedAt: new Date(now - 21 * 3600_000 + 130000).toISOString(), stepsTotal: 4, stepsCompleted: 2, failedSteps: [{ stepId: 's2', stepName: 'Copy files' }], triggeredBy: 'manual', startedByUsername: 'admin', traceId: null, parentExecutionId: null },
  ]));
  await page.route('**/api/users', (r) => json(r, [
    { id: 'u1', username: 'admin', role: 'Admin', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
    { id: 'u2', username: 'ops-team', role: 'Operator', isActive: true, createdAt: '2026-03-01T00:00:00Z' },
    { id: 'u3', username: 'auditor', role: 'Viewer', isActive: false, createdAt: '2026-04-01T00:00:00Z' },
  ]));
  // folderId must be the Root sentinel (…0002): the real API returns a non-null FolderId
  // (default RootFolderId) for every variable, and the page scopes the list to the selected
  // folder — omitting it here leaves undefined, which never matches the Root folder → the
  // list renders empty.
  await page.route('**/api/global-variables', (r) => json(r, [
    { id: 'g1', name: 'API_BASE_URL', value: 'https://api.corp.local', isSecret: false, description: 'Base URL for REST calls', folderId: '00000000-0000-0000-0000-000000000002', createdAt: '2026-05-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
    { id: 'g2', name: 'DEPLOY_TOKEN', value: null, isSecret: true, description: 'CI deploy token', folderId: '00000000-0000-0000-0000-000000000002', createdAt: '2026-05-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
  ]));
  await page.route('**/api/maintenance-windows', (r) => json(r, [
    { id: 'mw1', name: 'Weekend Blackout', description: 'No prod runs on weekends', isEnabled: true, mode: 'Blackout', scopeKind: 'Global', recurrence: 'Weekly', oneTimeStartUtc: null, oneTimeEndUtc: null, weeklyDaysMask: 65, weeklyStartMinuteOfDay: 1320, weeklyEndMinuteOfDay: 120, cronExpression: null, durationMinutes: null, timeZoneId: 'UTC', targets: [], createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
    { id: 'mw2', name: 'Patch Window', description: 'Allow patching workflows', isEnabled: true, mode: 'AllowOnly', scopeKind: 'Workflows', recurrence: 'OneTime', oneTimeStartUtc: new Date(now + 86400_000).toISOString(), oneTimeEndUtc: new Date(now + 90000_000).toISOString(), weeklyDaysMask: 0, weeklyStartMinuteOfDay: null, weeklyEndMinuteOfDay: null, cronExpression: null, durationMinutes: null, timeZoneId: 'UTC', targets: [{ targetKind: 'Workflow', targetId: 'wf1' }], createdAt: '2026-06-01T00:00:00Z', updatedAt: '2026-06-01T00:00:00Z', updatedBy: 'admin' },
  ]));
  await page.route('**/api/audit**', (r) => json(r, { items: [
    { id: 'a1', timestamp: new Date(now - 3600_000).toISOString(), userId: 'u1', username: 'admin', action: 'CREATE_WORKFLOW', resourceType: 'Workflow', resourceId: 'wf123456789', details: '{"name":"Nightly Backup"}', ipAddress: '10.0.0.12' },
    { id: 'a2', timestamp: new Date(now - 7200_000).toISOString(), userId: 'u2', username: 'ops-team', action: 'EXECUTE_WORKFLOW', resourceType: 'Workflow', resourceId: 'wf987654321', details: '{}', ipAddress: '10.0.0.30' },
  ], nextCursor: null }));
  await page.route(`**/api/workflows/${WF_GRAPH}`, (r) => json(r, {
    id: WF_GRAPH, name: 'Windows Update — Health Check', description: '', isEnabled: true, version: 3,
    definitionJson: JSON.stringify({
      nodes: [
        { id: 'trig', type: 'activity', position: { x: 40, y: 180 }, data: { label: 'Schedule', activityType: 'scheduleTrigger', config: { cronExpression: '0 2 * * *' } } },
        { id: 'check', type: 'activity', position: { x: 300, y: 80 }, data: { label: 'Check Disk', activityType: 'runScript', config: {}, __liveStatus: 'Succeeded' } },
        { id: 'update', type: 'activity', position: { x: 300, y: 300 }, data: { label: 'Install Updates', activityType: 'runScript', config: {}, __liveStatus: 'Running' } },
        { id: 'mail', type: 'activity', position: { x: 580, y: 180 }, data: { label: 'Email Result', activityType: 'emailNotification', config: {} } },
      ],
      edges: [
        { id: 'e1', source: 'trig', target: 'check', type: 'labeled', data: { label: 'Always' } },
        { id: 'e2', source: 'trig', target: 'update', type: 'labeled', data: { label: 'Always' } },
        { id: 'e3', source: 'check', target: 'mail', type: 'labeled', data: { label: 'On Success', condition: 'check.success' } },
        { id: 'e4', source: 'update', target: 'mail', type: 'labeled', data: { label: 'On Success' } },
      ],
    }),
  }));
}

test('all mobile screenshots', async ({ page }) => {
  await mockAll(page);
  await page.setViewportSize(PHONE);

  await page.goto('/');
  await page.getByText(/workflows/i).first().waitFor({ timeout: 15_000 });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${DIR}/01-dashboard.png`, fullPage: true });

  await page.getByRole('button', { name: 'Open menu' }).click();
  await page.waitForTimeout(350);
  await page.screenshot({ path: `${DIR}/02-drawer.png` });
  await page.keyboard.press('Escape').catch(() => {});

  await page.goto('/workflows');
  await page.getByText('Windows Update — Health Check').first().waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700); // let the np-fade-up entrance animation finish
  await page.screenshot({ path: `${DIR}/03-workflows.png`, fullPage: true });

  await page.goto('/machines');
  await page.getByText('WEB-PROD-01').waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${DIR}/04-machines.png`, fullPage: true });

  await page.goto('/executions');
  await page.getByTestId('mobile-card-list').waitFor({ timeout: 15_000 });
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${DIR}/05-executions.png`, fullPage: true });

  await page.goto('/users');
  await page.getByText('ops-team').waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${DIR}/06-users.png`, fullPage: true });

  await page.goto('/global-variables');
  await page.getByText('API_BASE_URL').waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${DIR}/07-globals.png`, fullPage: true });

  await page.goto('/maintenance-windows');
  await page.getByText('Weekend Blackout').waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${DIR}/08-maintenance.png`, fullPage: true });

  await page.goto('/audit');
  await page.getByText('CREATE_WORKFLOW', { exact: true }).waitFor({ timeout: 15_000 });
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${DIR}/09-audit.png`, fullPage: true });

  await page.goto(`/workflows/${WF_GRAPH}`);
  await page.getByText(/read-only view/i).waitFor({ timeout: 20_000 });
  await page.locator('.react-flow__node[data-id="check"]').waitFor({ timeout: 20_000 });
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${DIR}/10-designer-readonly.png` });
});
