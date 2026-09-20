/**
 * The demo's fake backend: route matching, the fetch patch, and the handler contract.
 *
 * The handler assertions matter most. A mock that answers with the wrong shape does not fail
 * loudly — it renders a page that is subtly wrong, or throws deep inside a component. These
 * check the shapes the app actually reads.
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { api } from '../../api/client';
import { adminSettings } from '../../api/adminSettings';
import { getAllPages, getPage } from '../../api/paging';
import { ROOT_FOLDER_ID, sharedFoldersApi } from '../../api/sharedFolders';
import { ROOT_FOLDER_ID as GLOBALS_ROOT_FOLDER_ID, globalFoldersApi } from '../../api/globalFolders';
import { cancelAllForWorkflow } from '../../api/operations';
import type { OperationsGraph, WorkflowCoverageResponse } from '../../types/api';
import { alertingApi } from '../../api/alerting';
import { systemAlertingApi } from '../../api/systemAlerting';
import { matchRoute, route } from '../../../demo/net/router';
import { installDemoBackend, uninstallDemoBackend } from '../../../demo/net/install';
import { setLatencyEnabled } from '../../../demo/net/latency';
import { demoRoutes } from '../../../demo/handlers';
import { demoSettingsSections } from '../../../demo/handlers/settings';
import { demoSystemAlertSourceIds } from '../../../demo/handlers/alerting';
import { demoAuditEntryCount } from '../../../demo/handlers/audit';
import { configureWorld, getWorld, resetWorld } from '../../../demo/state/world';
import { buildWorld } from '../../../demo/seed/build';
import { stopAllRuns } from '../../../demo/run/player';
import { resetFakeHub } from '../../../demo/hub/fakeHub';

const NOW = Date.parse('2026-09-18T12:00:00.000Z');

describe('route matching', () => {
  const routes = [
    route('GET', '/workflows', () => new Response('list')),
    route('GET', '/workflows/names', () => new Response('names')),
    route('GET', '/workflows/:id', () => new Response('one')),
    route('POST', '/workflows/:id/steps/:stepId/test', () => new Response('test')),
  ];

  it('prefers a literal segment over a parameter when it is registered first', () => {
    expect(matchRoute(routes, 'GET', '/workflows/names')?.route.pattern).toBe('/workflows/names');
  });

  it('captures parameters', () => {
    const match = matchRoute(routes, 'POST', '/workflows/abc/steps/step-1/test');
    expect(match?.params).toEqual({ id: 'abc', stepId: 'step-1' });
  });

  it('does not match a longer path against a shorter pattern', () => {
    expect(matchRoute(routes, 'GET', '/workflows/abc/versions')).toBeNull();
  });

  it('separates methods', () => {
    expect(matchRoute(routes, 'DELETE', '/workflows/abc')).toBeNull();
  });

  it('decodes percent-encoded parameters', () => {
    expect(matchRoute(routes, 'GET', '/workflows/a%20b')?.params.id).toBe('a b');
  });
});

describe('the fetch patch', () => {
  beforeEach(() => {
    setLatencyEnabled(false);
    configureWorld(() => buildWorld(NOW));
    installDemoBackend(demoRoutes);
  });

  afterEach(() => {
    stopAllRuns();
    resetFakeHub();
    uninstallDemoBackend();
    setLatencyEnabled(true);
  });

  it('answers /api requests without touching the network', async () => {
    const response = await fetch('/api/auth/me');
    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('application/json');
  });

  it('answers /healthz/database, which sits outside /api', async () => {
    // The app polls this from boot and treats a non-JSON content type as an outage.
    const response = await fetch('/healthz/database');
    expect(response.headers.get('content-type')).toContain('application/json');
    await expect(response.json()).resolves.toMatchObject({ status: 'ok' });
  });

  it('never answers 401 on any route, for any method', async () => {
    // A 401 sends the client to `/login` at the origin root, which on a sub-path deployment
    // is a page outside the demo with no way back.
    for (const candidate of demoRoutes) {
      const path = candidate.pattern.replaceAll(/:[^/]+/g, 'x');
      const response = await fetch(`/api${path}`, { method: candidate.method });
      expect(response.status, `${candidate.method} ${path}`).not.toBe(401);
    }
  });

  it('accepts a Request object as input', async () => {
    const response = await fetch(new Request('http://localhost/api/auth/me'));
    expect(response.status).toBe(200);
  });

  it('reports a path it does not handle instead of guessing silently', async () => {
    const response = await fetch('/api/definitely/not/a/route');
    expect(response.status).toBe(200);
    // Objects, not arrays: an empty array read as an object is truthy and throws on access.
    await expect(response.json()).resolves.toEqual({});
  });

  it('restores the original fetch on uninstall', async () => {
    const patched = globalThis.fetch;
    uninstallDemoBackend();
    expect(globalThis.fetch).not.toBe(patched);
    installDemoBackend(demoRoutes);
  });
});

describe('handler contracts', () => {
  beforeEach(() => {
    setLatencyEnabled(false);
    configureWorld(() => buildWorld(NOW));
    installDemoBackend(demoRoutes);
  });

  afterEach(() => {
    stopAllRuns();
    resetFakeHub();
    uninstallDemoBackend();
    resetWorld();
    setLatencyEnabled(true);
  });

  const json = async <T,>(path: string, init?: RequestInit): Promise<T> => {
    const response = await fetch(`/api${path}`, init);
    return (await response.json()) as T;
  };

  it('omits the definition from list rows and includes it on a single read', async () => {
    const rows = await json<Record<string, unknown>[]>('/workflows');
    expect(rows[0]).not.toHaveProperty('definitionJson');
    expect(rows[0]).toHaveProperty('hasManualTriggerParameters');
    const one = await json<Record<string, unknown>>(`/workflows/${rows[0].id as string}`);
    expect(typeof one.definitionJson).toBe('string');
  });

  it('answers the dashboard with every key the page reads', async () => {
    const stats = await json<Record<string, unknown>>('/stats/dashboard?windowHours=24');
    for (const key of [
      'workflowsTotal', 'workflowsEnabled', 'machinesTotal', 'machinesReachable', 'executionsTotal',
      'last24h', 'last24hBuckets', 'topWorkflows', 'running', 'recent', 'pendingCount', 'runningCount',
      'longRunningCount', 'longRunningSeconds', 'retryStats', 'failingWorkflows', 'editLocks',
      'healthHeartbeats', 'databaseProvider', 'clusterRole', 'recentAudit', 'llmEnabled',
    ]) {
      expect(stats, key).toHaveProperty(key);
    }
  });

  it('answers the duration trend in the shape the chart reads', async () => {
    const trend = await json<{ buckets: Record<string, unknown>[] }>('/stats/duration-trend?windowHours=24');
    expect(trend.buckets[0]).toHaveProperty('startedAt');
    expect(trend.buckets[0]).toHaveProperty('count');
    expect(trend.buckets[0]).toHaveProperty('medianMs');
    expect(trend.buckets[0]).toHaveProperty('p95Ms');
  });

  it('holds the lock invariant: locking disables, publishing re-enables and unlocks', async () => {
    const id = getWorld().workflows[0].id;

    const locked = await json<Record<string, unknown>>(`/workflows/${id}/lock`, { method: 'POST' });
    // The product does this atomically; seeding or faking "locked and enabled" would be a
    // state the engine never produces, and it would bypass the run gate.
    expect(locked.isEnabled).toBe(false);
    expect(locked.checkedOutByUserId).not.toBeNull();

    const published = await json<Record<string, unknown>>(`/workflows/${id}/publish`, {
      method: 'POST',
      body: JSON.stringify({ name: 'Renamed', description: null, definitionJson: '{"nodes":[],"edges":[]}' }),
    });
    expect(published.isEnabled).toBe(true);
    expect(published.checkedOutByUserId).toBeNull();
    expect(published.name).toBe('Renamed');
  });

  it('refuses to run a disabled workflow', async () => {
    const id = getWorld().workflows[0].id;
    await fetch(`/api/workflows/${id}/lock`, { method: 'POST' });
    const response = await fetch(`/api/workflows/${id}/execute`, { method: 'POST' });
    expect(response.status).toBe(400);
  });

  it('refuses to save a workflow the caller has not checked out', async () => {
    const id = getWorld().workflows[0].id;
    const response = await fetch(`/api/workflows/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ name: 'Nope' }),
    });
    expect(response.status).toBe(409);
  });

  it('snapshots a version on publish so the diff has something to compare', async () => {
    const id = getWorld().workflows[0].id;
    const before = await json<unknown[]>(`/workflows/${id}/versions`);
    await fetch(`/api/workflows/${id}/lock`, { method: 'POST' });
    await fetch(`/api/workflows/${id}/publish`, {
      method: 'POST',
      body: JSON.stringify({ definitionJson: '{"nodes":[],"edges":[]}' }),
    });
    const after = await json<unknown[]>(`/workflows/${id}/versions`);
    expect(after.length).toBe(before.length + 1);
  });

  it('explains, rather than fakes, what needs a real server', async () => {
    const id = getWorld().workflows[0].id;
    for (const path of [
      `/workflows/${id}/steps/step-1/test`,
      '/workflows/import',
      '/backup/export',
      '/dbadmin/query',
      '/ai/chat',
    ]) {
      const response = await fetch(`/api${path}`, { method: 'POST' });
      expect(response.status, path).toBe(501);
      await expect(response.json()).resolves.toMatchObject({ code: 'DEMO_NOT_AVAILABLE' });
    }
  });

  it('filters executions by workflow and terminal state', async () => {
    const id = getWorld().workflows[0].id;
    const page = await json<{ items: { workflowId: string; status: string }[]; total: number }>(
      `/executions?workflowId=${id}&terminalOnly=true`,
    );
    expect(page.items.length).toBeGreaterThan(0);
    expect(page.total).toBe(page.items.length);
    expect(page.items.every((r) => r.workflowId === id)).toBe(true);
    expect(page.items.every((r) => r.status !== 'Running')).toBe(true);
  });
});

/**
 * These drive the demo backend through the product's own API client and typed helpers rather
 * than through hand-written URLs and hand-read JSON.
 *
 * That difference matters: the first round of these tests asserted the shapes the handlers
 * produced, which is circular — a handler answering `{folderId}` while the client sends
 * `{targetFolderId}` passes every time. Going through the real callers makes a key mismatch
 * impossible to miss.
 */
describe('contracts read through the real clients', () => {
  beforeEach(() => {
    setLatencyEnabled(false);
    configureWorld(() => buildWorld(NOW));
    installDemoBackend(demoRoutes);
  });

  afterEach(() => {
    stopAllRuns();
    resetFakeHub();
    uninstallDemoBackend();
    resetWorld();
    setLatencyEnabled(true);
  });

  it('moves a workflow with the body the folder client actually sends', async () => {
    const workflow = getWorld().workflows[0];
    const target = getWorld().folders.find((f) => f.id !== workflow.folderId && f.id !== ROOT_FOLDER_ID)!;

    await sharedFoldersApi.moveWorkflowToFolder(workflow.id, target.id);

    expect(getWorld().workflows.find((w) => w.id === workflow.id)!.folderId).toBe(target.id);
  });

  it('pages executions through getPage, and reaches the second page', async () => {
    const workflowId = getWorld().workflows[0].id;
    const all = getWorld().executions.filter((e) => e.workflowId === workflowId);
    expect(all.length).toBeGreaterThan(3);

    const first = await getPage<{ id: string }>(`/executions?workflowId=${workflowId}`, 1, 2);
    expect(first.total).toBe(all.length);
    expect(first.totalPages).toBe(Math.ceil(all.length / 2));
    expect(first.items).toHaveLength(2);

    const second = await getPage<{ id: string }>(`/executions?workflowId=${workflowId}`, 2, 2);
    expect(second.items[0].id).not.toBe(first.items[0].id);

    // Every run has to stay reachable, not just the first page.
    const everything = await getAllPages<{ id: string }>(`/executions?workflowId=${workflowId}`, 2);
    expect(everything).toHaveLength(all.length);
  });

  it('serves every settings section the admin UI asks for', async () => {
    for (const section of demoSettingsSections) {
      const response = await adminSettings.getSection<Record<string, unknown>>(section);
      expect(response.sectionPath, section).toBe(section);
      // `useSectionForm` indexes into effectiveSource without an optional chain and replaces
      // its fallback with `payload`; both have to be real objects or the page throws.
      expect(response.effectiveSource, section).toBeTypeOf('object');
      expect(response.payload, section).toBeTypeOf('object');
      expect(response.payload, section).not.toBeNull();
      expect(Object.keys(response.payload as object).length, section).toBeGreaterThan(0);
    }
  });

  it('keys the designer annotation overlays by step id', async () => {
    const workflow = getWorld().workflows[0];
    const parsed = JSON.parse(workflow.definitionJson) as { nodes: { id: string }[] };
    const nodeIds = parsed.nodes.map((n) => n.id);

    const health = await api.get<Record<string, { status: string; startedAt: string }[]>>(
      `/workflows/${workflow.id}/step-health?stepIds=${nodeIds.join(',')}&limit=8`,
    );
    const stats = await api.get<Record<string, { totalRuns: number }>>(
      `/workflows/${workflow.id}/step-stats?windowDays=30`,
    );

    // Objects indexed by node id — an array leaves every sparkline and badge empty.
    expect(Array.isArray(health)).toBe(false);
    expect(Array.isArray(stats)).toBe(false);
    expect(nodeIds.filter((id) => health[id]?.length).length).toBeGreaterThan(0);
    expect(Object.keys(stats).some((id) => nodeIds.includes(id))).toBe(true);
  });

  it('reports coverage with the field names the heatmap reads', async () => {
    const workflow = getWorld().workflows[0];
    const coverage = await api.get<WorkflowCoverageResponse>(`/workflows/${workflow.id}/coverage?windowDays=30`);

    expect(coverage.totalExecutions).toBeGreaterThan(0);
    expect(coverage.nodes.length).toBeGreaterThan(0);
    for (const node of coverage.nodes) {
      expect(node).toHaveProperty('stepId');
      expect(node).toHaveProperty('executedCount');
      expect(node).toHaveProperty('skippedCount');
    }
    // A demo where every node reads as never executed is the same as no coverage at all.
    expect(coverage.nodes.some((n) => n.executedCount > 0)).toBe(true);
  });

  it('gives the Live-Ops snapshot the identifiers the timeline filters on', async () => {
    // Built against the wall clock for this one test: the handler windows against `Date.now()`,
    // while the rest of the suite pins the world to a fixed instant. With a fixed world the
    // 60-minute window empties as soon as the two drift apart, and the test starts failing for
    // a reason that has nothing to do with what it checks.
    configureWorld(() => buildWorld(Date.now()));

    const graph = await api.get<OperationsGraph>('/operations/graph?windowMinutes=60');

    expect(graph.nodes.length).toBeGreaterThan(0);
    for (const node of graph.nodes) expect(node.workflowId).toBeTruthy();
    expect(graph.recent.length).toBeGreaterThan(0);
    for (const run of graph.recent) {
      expect(run.executionId).toBeTruthy();
      expect(run.workflowId).toBeTruthy();
      expect(run.completedAt).toBeTruthy();
    }
    expect(graph.meta.windowMinutes).toBe(60);
    expect(graph.meta.recentSinceUtc).toBeTruthy();
  });

  it('links every failure cause to the run it came from', async () => {
    const causes = await api.get<{ groups: { latestExecutionId: string; latestStartedAt: string }[] }>(
      '/stats/failure-causes?windowHours=720',
    );
    expect(causes.groups.length).toBeGreaterThan(0);
    const ids = new Set(getWorld().executions.map((e) => e.id));
    for (const group of causes.groups) {
      expect(ids.has(group.latestExecutionId)).toBe(true);
      expect(Number.isNaN(Date.parse(group.latestStartedAt))).toBe(false);
    }
  });

  it('removes the whole subtree on a recursive folder delete', async () => {
    const world = getWorld();
    const parent = world.folders.find((f) => f.id !== ROOT_FOLDER_ID)!;
    const child = await sharedFoldersApi.create(parent.id, 'Nested');
    const moved = world.workflows.find((w) => w.folderId === parent.id)!;
    await sharedFoldersApi.moveWorkflowToFolder(moved.id, child.id);

    await sharedFoldersApi.deleteRecursive(parent.id);

    const after = getWorld();
    expect(after.folders.some((f) => f.id === parent.id || f.id === child.id)).toBe(false);
    expect(after.workflows.some((w) => w.id === moved.id)).toBe(false);
    // The point of the fix: nothing may reference a workflow that no longer exists, or the
    // dashboard renders those runs as "Unknown".
    const workflowIds = new Set(after.workflows.map((w) => w.id));
    expect(after.executions.filter((e) => !workflowIds.has(e.workflowId))).toEqual([]);
    for (const executionId of after.steps.keys()) {
      expect(after.executions.some((e) => e.id === executionId)).toBe(true);
    }
  });

  it('refuses a change it cannot make instead of reporting success', async () => {
    // Answering 200 would make the change look like it worked while nothing happened. Editing a
    // database row is server-only by nature, so it stays unrouted on purpose and exercises the
    // fallback rather than a handler.
    const response = await fetch('/api/dbadmin/tables/Workflows/rows', {
      method: 'PATCH',
      body: JSON.stringify({ key: {}, values: {} }),
    });
    expect(response.status).toBe(501);
    expect((await response.json()).code).toBe('DEMO_NOT_AVAILABLE');
  });

  it('carries a folder rename down to its descendants', async () => {
    // Rewriting only the renamed folder leaves children on the old prefix, and breadcrumbs
    // then disagree with the tree. The product recomputes the whole subtree.
    const parent = await sharedFoldersApi.create(ROOT_FOLDER_ID, 'Parent');
    const child = await sharedFoldersApi.create(parent.id, 'Child');
    expect(child.path).toBe('/Parent/Child');

    await sharedFoldersApi.rename(parent.id, 'Renamed');

    const after = await sharedFoldersApi.list();
    expect(after.find((f) => f.id === parent.id)!.path).toBe('/Renamed');
    expect(after.find((f) => f.id === child.id)!.path).toBe('/Renamed/Child');
  });

  it('grants, changes and revokes a folder permission', async () => {
    const folder = getWorld().folders.find((f) => f.id !== ROOT_FOLDER_ID)!;
    const before = (await sharedFoldersApi.listPermissions(folder.id)).length;

    const granted = await sharedFoldersApi.grantPermission(folder.id, 'User', 'carol', 'FolderViewer');
    expect((await sharedFoldersApi.listPermissions(folder.id))).toHaveLength(before + 1);

    await sharedFoldersApi.updatePermission(folder.id, granted.id, 'FolderOperator');
    const updated = (await sharedFoldersApi.listPermissions(folder.id)).find((p) => p.id === granted.id)!;
    expect(updated.role).toBe('FolderOperator');

    await sharedFoldersApi.revokePermission(folder.id, granted.id);
    expect(await sharedFoldersApi.listPermissions(folder.id)).toHaveLength(before);
  });

  it('runs the whole global-folder cycle the tree drives', async () => {
    const created = await globalFoldersApi.create(GLOBALS_ROOT_FOLDER_ID, 'Scratch');
    const nested = await globalFoldersApi.create(created.id, 'Nested');

    await globalFoldersApi.rename(created.id, 'Renamed');
    expect((await globalFoldersApi.list()).find((f) => f.id === nested.id)!.path).toBe('/Renamed/Nested');

    // A variable dragged onto a folder has to actually land in it.
    const variable = getWorld().globals[0];
    await globalFoldersApi.moveVariableToFolder(variable.id, nested.id);
    expect(getWorld().globals.find((g) => g.id === variable.id)!.folderId).toBe(nested.id);

    const removed = await globalFoldersApi.deleteRecursive(created.id);
    expect(removed.deletedFolders).toBe(2);
    expect(removed.deletedVariables).toBe(1);
    expect(getWorld().globals.some((g) => g.id === variable.id)).toBe(false);
  });

  it('creates, edits and deletes a user', async () => {
    const before = getWorld().users.length;

    const created = await api.post<{ id: string; role: string }>('/users', { username: 'carol', role: 'Operator' });
    expect(getWorld().users).toHaveLength(before + 1);

    await api.put(`/users/${created.id}`, { role: 'Viewer' });
    expect(getWorld().users.find((u) => u.id === created.id)!.role).toBe('Viewer');

    await api.delete(`/users/${created.id}`);
    expect(getWorld().users).toHaveLength(before);
  });

  it('creates, edits and deletes a maintenance window', async () => {
    const before = getWorld().maintenanceWindows.length;
    expect(before).toBeGreaterThan(0);

    const created = await api.post<{ id: string }>('/maintenance-windows', { name: 'Ad hoc', scopeKind: 'Global' });
    await api.put(`/maintenance-windows/${created.id}`, { name: 'Renamed' });
    expect(getWorld().maintenanceWindows.find((w) => w.id === created.id)!.name).toBe('Renamed');

    await api.delete(`/maintenance-windows/${created.id}`);
    expect(getWorld().maintenanceWindows).toHaveLength(before);
  });

  it('rolls a custom activity back to what it actually held before', async () => {
    // Returning the current definition made the dialog report success while nothing changed.
    const definition = getWorld().customActivities[0];
    // Copied out, not held by reference: the world hands back the live object, so reading
    // `definition.version` after the mutations would compare a value with itself.
    const original = definition.scriptTemplate;
    const originalVersion = definition.version;

    await api.put(`/custom-activities/${definition.id}`, { scriptTemplate: 'Write-Output "changed"' });
    expect(getWorld().customActivities[0].scriptTemplate).toBe('Write-Output "changed"');

    const versions = await api.get<{ version: number }[]>(`/custom-activities/${definition.id}/versions`);
    expect(versions).toHaveLength(1);

    await api.post(`/custom-activities/${definition.id}/rollback/${versions[0].version}`, {});
    const after = getWorld().customActivities[0];
    expect(after.scriptTemplate).toBe(original);
    // A rollback moves forward: one version for the save, one for the rollback itself.
    expect(after.version).toBe(originalVersion + 2);
  });

  it('refuses a rollback to a version that was never stored', async () => {
    const definition = getWorld().customActivities[0];
    const response = await fetch(`/api/custom-activities/${definition.id}/rollback/999`, { method: 'POST' });
    expect(response.status).toBe(404);
  });

  it('reports cancel-all in the shape the toast reads', async () => {
    // `{cancelled: n}` made the toast say "undefined" on an otherwise successful path.
    const result = await cancelAllForWorkflow(getWorld().workflows[0].id);
    expect(result.total).toBeTypeOf('number');
    expect(result.signalled).toBeTypeOf('number');
  });

  it('offers real backup sections so Export reaches its own refusal', async () => {
    // An empty manifest selected nothing, and the page's own "pick a section" validation
    // fired before the request was ever made.
    const manifest = await api.get<{ sections: { section: string; count: number }[] }>('/backup/manifest');
    expect(manifest.sections.length).toBeGreaterThan(0);
    expect(manifest.sections.some((s) => s.section === 'workflows')).toBe(true);
  });

  it('derives the support log from the executions rather than stating one', async () => {
    const world = getWorld();
    const page = await api.get<{ items: { eventType: string; executionId: string | null }[]; hasMore: boolean }>(
      '/diagnostics/support-events?take=500');

    expect(page.items.length).toBeGreaterThan(0);
    // Every row has to point at a run that exists, or the log contradicts the executions list.
    const executionIds = new Set(world.executions.map((e) => e.id));
    for (const row of page.items) {
      if (row.executionId) expect(executionIds.has(row.executionId)).toBe(true);
    }
    // A failed run has to be visible as one; an all-green log would be the demo flattering itself.
    expect(page.items.some((r) => r.eventType === 'EXECUTION_FAILED')).toBe(true);
    expect(page.items.some((r) => r.eventType === 'STEP_FAILED')).toBe(true);

    const tail = await api.get<{ lines: string[]; lineCount: number }>('/diagnostics/support-log');
    expect(tail.lines.length).toBe(tail.lineCount);
    expect(tail.lines.length).toBeGreaterThan(0);
  });

  it('filters support events by type, the way the table does', async () => {
    const filtered = await api.get<{ items: { eventType: string }[] }>(
      '/diagnostics/support-events?eventType=EXECUTION_FAILED&take=500');
    expect(filtered.items.length).toBeGreaterThan(0);
    for (const row of filtered.items) expect(row.eventType).toBe('EXECUTION_FAILED');
  });

  it('refuses the support downloads instead of handing over a file that lies', async () => {
    for (const path of ['/api/diagnostics/support-log/download?date=2026-09-18',
                        '/api/diagnostics/support-events/export?format=csv']) {
      const response = await fetch(path);
      expect(response.status, path).toBe(501);
      expect((await response.json()).code, path).toBe('DEMO_NOT_AVAILABLE');
    }
  });

  it('never returns a credential password it was sent', async () => {
    const credential = getWorld().credentials[0];
    await api.put(`/credentials/${credential.id}`, { name: credential.name, password: 'hunter2' });

    const rows = await api.get<Record<string, unknown>[]>('/credentials');
    for (const row of rows) expect(row.password).toBeUndefined();
  });

  it('keeps every stored version publishable', async () => {
    const world = getWorld();
    for (const workflow of world.workflows) {
      for (const version of world.versions.get(workflow.id) ?? []) {
        const definition = JSON.parse(version.definitionJson) as {
          nodes: { id: string }[];
          edges: { source: string; target: string }[];
        };
        const ids = new Set(definition.nodes.map((n) => n.id));
        // A historical version that kept edges to a removed node cannot be published after a
        // rollback: the linter rejects the dangling reference.
        for (const edge of definition.edges) {
          expect(ids.has(edge.source), `${workflow.name} v${version.version}`).toBe(true);
          expect(ids.has(edge.target), `${workflow.name} v${version.version}`).toBe(true);
        }
      }
    }
  });

  it('records a version when the designer saves, not only when it publishes', async () => {
    const workflow = getWorld().workflows[0];
    const before = (getWorld().versions.get(workflow.id) ?? []).length;

    await fetch(`/api/workflows/${workflow.id}/lock`, { method: 'POST' });
    await fetch(`/api/workflows/${workflow.id}`, {
      method: 'PUT',
      body: JSON.stringify({ definitionJson: '{"nodes":[],"edges":[]}' }),
    });

    expect((getWorld().versions.get(workflow.id) ?? []).length).toBe(before + 1);
  });
});

/**
 * Alerting catalogs.
 *
 * Both are read without a guard — `SystemAlertsSection` does `catalog.sources.length` directly —
 * so an array where an object belongs does not degrade the page, it takes it down.
 */
describe('alerting catalogs', () => {
  beforeEach(() => {
    setLatencyEnabled(false);
    configureWorld(() => buildWorld(NOW));
    installDemoBackend(demoRoutes);
  });

  afterEach(() => {
    stopAllRuns();
    resetFakeHub();
    uninstallDemoBackend();
    resetWorld();
    setLatencyEnabled(true);
  });

  it('serves the system catalog as an object with sources', async () => {
    const catalog = await systemAlertingApi.catalog();
    expect(Array.isArray(catalog)).toBe(false);
    expect(Array.isArray(catalog.sources)).toBe(true);
    expect(catalog.sources.length).toBeGreaterThan(0);
    for (const source of catalog.sources) {
      expect(source.sourceId).toBeTruthy();
      expect(source.category).toBeTruthy();
      expect(Array.isArray(source.fields)).toBe(true);
      expect(Array.isArray(source.parameters)).toBe(true);
      expect(Array.isArray(source.presets)).toBe(true);
    }
    expect(catalog.sources.map((s) => s.sourceId)).toEqual(demoSystemAlertSourceIds);
  });

  it('serves the rule catalog with the four lists the editor reads', async () => {
    const catalog = await alertingApi.catalog();
    expect(Array.isArray(catalog.eventTypes)).toBe(true);
    expect(Array.isArray(catalog.eventFields)).toBe(true);
    expect(Array.isArray(catalog.channels)).toBe(true);
    expect(Array.isArray(catalog.dedupTemplateFields)).toBe(true);
    expect(catalog.eventTypes.length).toBeGreaterThan(0);
  });

  it('starts with no rules and no policies, the product idle state', async () => {
    await expect(alertingApi.list()).resolves.toEqual([]);
    await expect(systemAlertingApi.list()).resolves.toEqual([]);
  });
});

/**
 * Audit log and its export.
 *
 * The export is the only surface in the app that leaves via a plain `<a href="/api/...">`, which
 * the fetch patch cannot see — so it is also the only one that could navigate a visitor out of
 * the demo. Serving it properly is what lets the demo's click guard answer it in place.
 */
describe('audit log', () => {
  beforeEach(() => {
    setLatencyEnabled(false);
    configureWorld(() => buildWorld(NOW));
    installDemoBackend(demoRoutes);
  });

  afterEach(() => {
    stopAllRuns();
    resetFakeHub();
    uninstallDemoBackend();
    resetWorld();
    setLatencyEnabled(true);
  });

  it('answers with the cursor-paged envelope the page reads, not a bare array', async () => {
    const first = await api.get<{ items: { id: string; timestamp: string }[]; nextCursor: { id: string; timestamp: string } | null }>(
      '/audit?take=5',
    );
    expect(Array.isArray(first)).toBe(false);
    expect(first.items).toHaveLength(5);
    expect(first.nextCursor).not.toBeNull();

    const second = await api.get<{ items: { id: string }[] }>(
      `/audit?take=5&cursorTimestamp=${encodeURIComponent(first.nextCursor!.timestamp)}&cursorId=${first.nextCursor!.id}`,
    );
    // The cursor has to advance, or "load more" loops on the same page forever.
    expect(second.items[0].id).not.toBe(first.items[0].id);
  });

  it('derives its entries from the world instead of stating them', async () => {
    const world = getWorld();
    const page = await api.get<{ items: { action: string; resourceId: string | null }[] }>('/audit?take=500');
    expect(page.items.length).toBe(demoAuditEntryCount());

    const workflowIds = new Set(world.workflows.map((w) => w.id));
    const executionIds = new Set(world.executions.map((e) => e.id));
    for (const entry of page.items) {
      if (entry.action.startsWith('WORKFLOW_')) expect(workflowIds.has(entry.resourceId ?? '')).toBe(true);
      if (entry.action === 'EXECUTION_STARTED') expect(executionIds.has(entry.resourceId ?? '')).toBe(true);
    }
    // An empty audit log beside forty-odd executions is a visible contradiction.
    expect(page.items.some((e) => e.action === 'EXECUTION_STARTED')).toBe(true);
  });

  it('filters by action the way the quick-filter chips do', async () => {
    const page = await api.get<{ items: { action: string }[] }>('/audit?take=500&action=WORKFLOW_PUBLISHED');
    expect(page.items.length).toBeGreaterThan(0);
    expect(page.items.every((e) => e.action === 'WORKFLOW_PUBLISHED')).toBe(true);
  });

  it('serves a real file for both export formats', async () => {
    for (const [format, type, extension] of [
      ['csv', 'text/csv', '.csv'],
      ['ndjson', 'application/x-ndjson', '.ndjson'],
    ] as const) {
      const response = await fetch(`/api/audit/export?format=${format}`);
      expect(response.status, format).toBe(200);
      expect(response.headers.get('content-type'), format).toContain(type);
      // Content-Disposition is what the demo's click guard keys on to save the blob.
      expect(response.headers.get('content-disposition'), format).toContain(extension);
      const body = await response.text();
      expect(body.length, format).toBeGreaterThan(0);
      expect(body.split('\n').length, format).toBeGreaterThan(1);
    }
  });
});
