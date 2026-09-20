/**
 * Workflow endpoints: list, read, the edit-lock lifecycle, and running.
 *
 * The lock lifecycle is reproduced rather than short-circuited. `POST /lock` disables the
 * workflow the way the product does it atomically, and `useWorkflowExecution` refuses to run
 * a disabled workflow — so a visitor who edits has to publish before running, which is the
 * real flow and shows off the edit lock instead of hiding it.
 */
import { ROOT_FOLDER_ID } from '../../src/api/sharedFolders';
import type {
  Workflow,
  WorkflowContractInput,
  WorkflowContractOutput,
  WorkflowContractResponse,
} from '../../src/types/api';
import { route, type RequestContext, type Route } from '../net/router';
import { badRequest, conflict, download, json, noContent, notFound, notInDemo } from '../net/respond';
import { definitionOf, findWorkflow, getWorld, type GraphNode } from '../state/world';
import { runtimeId } from '../state/ids';
import { DEMO_USER } from '../seed/entities';
import { buildStepStats } from '../seed/dashboard';
import { cancelRun, startRun } from '../run/player';
import { FILE_WORKFLOW_ID, fileInputError } from '../run/fileScenario';

interface SaveBody {
  name?: string;
  description?: string | null;
  definitionJson?: string;
  folderId?: string;
}

function nodesOf(workflow: Workflow): GraphNode[] {
  return definitionOf(workflow).nodes;
}

function manualTriggerParameters(workflow: Workflow): { name: string; type?: string; required?: boolean; default?: string | null; description?: string | null }[] {
  const trigger = nodesOf(workflow).find((n) => n.data?.activityType === 'manualTrigger');
  const params = trigger?.data?.config?.parameters;
  return Array.isArray(params) ? (params as { name: string }[]) : [];
}

function listItem(workflow: Workflow) {
  const { definitionJson: _definitionJson, ...summary } = workflow;
  return { ...summary, hasManualTriggerParameters: manualTriggerParameters(workflow).length > 0 };
}

function applySave(workflow: Workflow, body: SaveBody | undefined): void {
  if (!body) return;
  if (typeof body.name === 'string' && body.name.trim()) workflow.name = body.name.trim();
  if (body.description !== undefined) workflow.description = body.description;
  if (typeof body.definitionJson === 'string') workflow.definitionJson = body.definitionJson;
  workflow.updatedAt = new Date().toISOString();
  workflow.updatedBy = DEMO_USER.username;
  workflow.activityCount = nodesOf(workflow).filter((n) => n.data?.activityType !== 'note').length;
  workflow.triggerTypes = [
    ...new Set(
      nodesOf(workflow)
        .filter((n) => !n.data?.disabled && (n.data?.activityType ?? '').endsWith('Trigger'))
        .map((n) => n.data!.activityType!),
    ),
  ];
}

/**
 * Records a version so the diff and rollback surfaces have something to compare against.
 * Saving does this too, not just publishing — otherwise a workflow created and edited in the
 * demo has no history at all and the diff modal opens empty.
 *
 * Capped the way `Retention:WorkflowVersions` caps it, so an autosave loop cannot grow the
 * list without bound.
 */
const MAX_VERSIONS_PER_WORKFLOW = 50;

function snapshotVersion(workflow: Workflow, changeNote: string | null): void {
  const world = getWorld();
  const history = world.versions.get(workflow.id) ?? [];
  for (const entry of history) entry.isCurrent = false;
  workflow.version += 1;
  history.unshift({
    version: workflow.version,
    isCurrent: true,
    createdAt: new Date().toISOString(),
    createdBy: DEMO_USER.username,
    changeNote,
    definitionJson: workflow.definitionJson,
  });
  world.versions.set(workflow.id, history.slice(0, MAX_VERSIONS_PER_WORKFLOW));
}

/** Every step row belonging to one workflow's runs. */
function stepRowsOf(workflowId: string) {
  const world = getWorld();
  const ids = new Set(world.executions.filter((e) => e.workflowId === workflowId).map((e) => e.id));
  return [...world.steps.entries()].filter(([id]) => ids.has(id)).flatMap(([, rows]) => rows);
}

function withWorkflow(ctx: RequestContext, run: (workflow: Workflow) => Response | Promise<Response>) {
  const workflow = findWorkflow(ctx.params.id);
  if (!workflow) return notFound('Workflow');
  return run(workflow);
}

function buildContract(workflow: Workflow): WorkflowContractResponse {
  const nodes = nodesOf(workflow);
  const inputs: WorkflowContractInput[] = manualTriggerParameters(workflow).map((p) => ({
    name: p.name,
    type: p.type ?? 'string',
    required: p.required ?? false,
    default: p.default ?? null,
    description: p.description ?? null,
    hasConflict: false,
  }));
  const returnNodes = nodes.filter((n) => n.data?.activityType === 'returnData');
  const dataKeys = new Set<string>();
  for (const node of returnNodes) {
    const data = node.data?.config?.data;
    if (data && typeof data === 'object') for (const key of Object.keys(data)) dataKeys.add(key);
  }
  const source: WorkflowContractOutput['source'] = returnNodes.length > 1 ? 'multiple' : 'single';
  const outputs: WorkflowContractOutput[] = [
    ...[...dataKeys].map((name) => ({ name, source })),
    { name: '__executionId', source: 'system' as const },
    { name: '__status', source: 'system' as const },
    { name: '__workflowId', source: 'system' as const },
    { name: '__workflowName', source: 'system' as const },
  ];
  return {
    workflowId: workflow.id,
    workflowName: workflow.name,
    hasManualTrigger: nodes.some((n) => n.data?.activityType === 'manualTrigger'),
    hasReturnData: returnNodes.length > 0,
    hasMultipleReturnDataNodes: returnNodes.length > 1,
    inputs,
    outputs,
  };
}

export const workflowRoutes: Route[] = [
  route('GET', '/workflows', (ctx) => {
    const folderId = ctx.query.get('folderId');
    const search = ctx.query.get('search')?.toLowerCase();
    let rows = getWorld().workflows;
    if (folderId) rows = rows.filter((w) => w.folderId === folderId);
    if (search) rows = rows.filter((w) => w.name.toLowerCase().includes(search));
    return json(rows.map(listItem));
  }),

  route('GET', '/workflows/names', () =>
    json(getWorld().workflows.map((w) => ({ id: w.id, name: w.name })))),

  route('GET', '/workflows/export', () =>
    download(
      JSON.stringify(
        {
          schema: 'nodepilot-workflow-export/v1',
          exportVersion: 1,
          exportedAt: new Date().toISOString(),
          workflows: getWorld().workflows.map((w) => ({
            name: w.name,
            description: w.description,
            definition: JSON.parse(w.definitionJson) as unknown,
          })),
        },
        null,
        2,
      ),
      'nodepilot-workflows.json',
    )),

  route('POST', '/workflows/import', () => notInDemo('Importing workflows')),
  route('POST', '/workflows/import-scorch', () => notInDemo('Importing System Center Orchestrator runbooks')),

  route('POST', '/workflows', async (ctx) => {
    const body = await ctx.body<SaveBody>();
    const now = new Date().toISOString();
    const workflow: Workflow = {
      id: runtimeId('workflow'),
      name: body?.name?.trim() || 'New Workflow',
      description: body?.description ?? null,
      version: 1,
      isEnabled: false,
      createdAt: now,
      updatedAt: now,
      createdBy: DEMO_USER.username,
      updatedBy: DEMO_USER.username,
      activityCount: 0,
      triggerTypes: [],
      lastExecution: null,
      successCount: 0,
      totalCount: 0,
      avgDurationMs: null,
      // Created workflows come back checked out, matching the product: a new workflow opens
      // ready to edit rather than read-only.
      checkedOutByUserId: DEMO_USER.id,
      checkedOutByUserName: DEMO_USER.username,
      checkedOutAt: now,
      maxConcurrentExecutions: null,
      folderId: body?.folderId ?? ROOT_FOLDER_ID,
      folderPath: '/',
      capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
      definitionJson: body?.definitionJson ?? '{"nodes":[],"edges":[]}',
    };
    getWorld().workflows.unshift(workflow);
    getWorld().versions.set(workflow.id, []);
    return json(workflow, 201);
  }),

  route('GET', '/workflows/:id', (ctx) => withWorkflow(ctx, (w) => json(w))),

  route('PUT', '/workflows/:id', (ctx) =>
    withWorkflow(ctx, async (workflow) => {
      if (workflow.checkedOutByUserId !== DEMO_USER.id) {
        return conflict('WORKFLOW_LOCKED', 'Check the workflow out before saving.');
      }
      const body = await ctx.body<SaveBody>();
      const changed = typeof body?.definitionJson === 'string' && body.definitionJson !== workflow.definitionJson;
      applySave(workflow, body);
      if (changed) snapshotVersion(workflow, 'Saved from the designer');
      return json(workflow);
    })),

  route('DELETE', '/workflows/:id', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const world = getWorld();
      world.workflows = world.workflows.filter((w) => w.id !== workflow.id);
      world.executions = world.executions.filter((e) => e.workflowId !== workflow.id);
      world.versions.delete(workflow.id);
      return noContent();
    })),

  route('POST', '/workflows/:id/duplicate', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const now = new Date().toISOString();
      const copy: Workflow = {
        ...workflow,
        id: runtimeId('workflow'),
        name: `${workflow.name} (Copy)`,
        version: 1,
        isEnabled: false,
        createdAt: now,
        updatedAt: now,
        lastExecution: null,
        successCount: 0,
        totalCount: 0,
        avgDurationMs: null,
        checkedOutByUserId: null,
        checkedOutByUserName: null,
        checkedOutAt: null,
      };
      getWorld().workflows.unshift(copy);
      getWorld().versions.set(copy.id, []);
      return json(copy, 201);
    })),

  // --- edit lock -----------------------------------------------------------

  route('POST', '/workflows/:id/lock', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      if (workflow.checkedOutByUserId && workflow.checkedOutByUserId !== DEMO_USER.id) {
        return conflict('WORKFLOW_ALREADY_LOCKED', 'Another user holds the edit lock.');
      }
      // Locking disables the workflow, atomically, exactly as the product does.
      workflow.isEnabled = false;
      workflow.checkedOutByUserId = DEMO_USER.id;
      workflow.checkedOutByUserName = DEMO_USER.username;
      workflow.checkedOutAt = new Date().toISOString();
      return json(workflow);
    })),

  route('POST', '/workflows/:id/unlock', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      workflow.checkedOutByUserId = null;
      workflow.checkedOutByUserName = null;
      workflow.checkedOutAt = null;
      return json(workflow);
    })),

  route('POST', '/workflows/:id/force-unlock', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      workflow.checkedOutByUserId = null;
      workflow.checkedOutByUserName = null;
      workflow.checkedOutAt = null;
      return json(workflow);
    })),

  route('POST', '/workflows/:id/publish', (ctx) =>
    withWorkflow(ctx, async (workflow) => {
      applySave(workflow, await ctx.body<SaveBody>());
      snapshotVersion(workflow, 'Published from the designer');
      workflow.isEnabled = true;
      workflow.checkedOutByUserId = null;
      workflow.checkedOutByUserName = null;
      workflow.checkedOutAt = null;
      return json(workflow);
    })),

  route('POST', '/workflows/:id/enable', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      if (workflow.checkedOutByUserId) {
        return conflict('WORKFLOW_LOCKED', 'Publish or unlock the workflow before enabling it.');
      }
      if (workflow.isEnabled) return noContent();
      workflow.isEnabled = true;
      return json(workflow);
    })),

  route('POST', '/workflows/:id/disable', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      workflow.isEnabled = false;
      return json(workflow);
    })),

  // --- running -------------------------------------------------------------

  route('POST', '/workflows/:id/execute', async (ctx) => {
    const body = await ctx.body<{ parameters?: Record<string, string> }>();
    const parameters = body?.parameters ?? {};
    if (Object.values(parameters).some(value => typeof value !== 'string')) return badRequest('INVALID_PARAMETERS', 'Parameters must be strings.');
    if (ctx.params.id === FILE_WORKFLOW_ID) {
      const error = fileInputError(parameters);
      if (error) return badRequest('INVALID_PARAMETERS', error);
    }
    return withWorkflow(ctx, (workflow) => {
      if (!workflow.isEnabled) {
        return badRequest('WORKFLOW_DISABLED', 'Publish the workflow before running it.');
      }
      const execution = startRun(workflow.id, 'manual', parameters);
      return execution ? json(execution, 202) : notFound('Workflow');
    });
  }),

  route('POST', '/workflows/:id/cancel-all', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const running = getWorld().executions.filter(
        (e) => e.workflowId === workflow.id && (e.status === 'Running' || e.status === 'Pending'),
      );
      for (const execution of running) cancelRun(execution.id);
      // `CancelAllResult` in src/api/operations.ts: the toast reads `total`, so any other key
      // reports "undefined" on a path that otherwise looks like a success.
      return json({ total: running.length, signalled: running.length });
    })),

  route('PUT', '/workflows/:id/concurrency-limit', (ctx) =>
    withWorkflow(ctx, async (workflow) => {
      const body = await ctx.body<{ maxConcurrentExecutions?: number | null }>();
      if (!body || !('maxConcurrentExecutions' in body)) {
        return badRequest('MISSING_PROPERTY', 'maxConcurrentExecutions is required.');
      }
      workflow.maxConcurrentExecutions = body.maxConcurrentExecutions ?? null;
      return json(workflow);
    })),

  route('POST', '/workflows/:id/move-folder', (ctx) =>
    withWorkflow(ctx, async (workflow) => {
      // The client sends `targetFolderId` (see api/sharedFolders.ts); `folderId` is the key
      // the global-variable move uses, and reading that one here answered every drag with a 404.
      const body = await ctx.body<{ targetFolderId?: string }>();
      const folder = getWorld().folders.find((f) => f.id === body?.targetFolderId);
      if (!folder) return notFound('Folder');
      workflow.folderId = folder.id;
      workflow.folderPath = folder.path;
      for (const candidate of getWorld().folders) {
        candidate.workflowCount = getWorld().workflows.filter((w) => w.folderId === candidate.id).length;
      }
      return json(workflow);
    })),

  // --- versions ------------------------------------------------------------

  route('GET', '/workflows/:id/versions', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const history = getWorld().versions.get(workflow.id) ?? [];
      return json(history.map(({ definitionJson: _definitionJson, ...row }) => row));
    })),

  route('GET', '/workflows/:id/versions/:version', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const history = getWorld().versions.get(workflow.id) ?? [];
      const entry = history.find((v) => String(v.version) === ctx.params.version);
      if (!entry) return notFound('Workflow version');
      return json({ version: entry.version, definition: JSON.parse(entry.definitionJson) as unknown });
    })),

  route('POST', '/workflows/:id/rollback/:version', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const history = getWorld().versions.get(workflow.id) ?? [];
      const entry = history.find((v) => String(v.version) === ctx.params.version);
      if (!entry) return notFound('Workflow version');
      workflow.definitionJson = entry.definitionJson;
      snapshotVersion(workflow, `Rolled back to version ${entry.version}`);
      return json(workflow);
    })),

  route('GET', '/workflows/:id/export', (ctx) =>
    withWorkflow(ctx, (workflow) =>
      download(
        JSON.stringify(
          {
            schema: 'nodepilot-workflow-export/v1',
            exportVersion: 1,
            exportedAt: new Date().toISOString(),
            workflows: [
              {
                name: workflow.name,
                description: workflow.description,
                definition: JSON.parse(workflow.definitionJson) as unknown,
              },
            ],
          },
          null,
          2,
        ),
        `${workflow.name.replace(/[^\w.-]+/g, '-')}.json`,
      ))),

  // --- analysis surfaces ---------------------------------------------------

  // A `startWorkflow` node may reference its child by NAME, so the designer resolves the
  // contract over this path rather than the by-id one. Resolution mirrors WorkflowNameResolver:
  // exact case wins, case-insensitive otherwise.
  //
  // An unknown name must answer 404. `useWorkflowContract` treats only 404 as "no contract" and
  // then falls back to the free-form parameter table; anything else is taken as a contract, and
  // `ContractMappingTable` iterates `contract.inputs` without a guard — a 200 with an empty body
  // blanks the entire designer.
  route('GET', '/workflows/by-name/:name/contract', (ctx) => {
    const wanted = ctx.params.name;
    const rows = getWorld().workflows;
    const match = rows.find((w) => w.name === wanted)
      ?? rows.find((w) => w.name.toLowerCase() === wanted.toLowerCase());
    return match ? json(buildContract(match)) : notFound('Workflow');
  }),

  route('GET', '/workflows/:id/contract', (ctx) => withWorkflow(ctx, (w) => json(buildContract(w)))),

  // The designer's annotation overlays index these two by step id. An array leaves every
  // sparkline and performance badge empty, because `stepHealth[node.id]` finds nothing.
  route('GET', '/workflows/:id/step-stats', (ctx) =>
    withWorkflow(ctx, (workflow) => json(buildStepStats(stepRowsOf(workflow.id)))),
  ),

  route('GET', '/workflows/:id/step-health', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const wanted = new Set((ctx.query.get('stepIds') ?? '').split(',').filter(Boolean));
      const limit = Number(ctx.query.get('limit') ?? 8);
      const health: Record<string, { status: string; startedAt: string }[]> = {};
      for (const step of stepRowsOf(workflow.id)) {
        if (wanted.size > 0 && !wanted.has(step.stepId)) continue;
        if (!step.startedAt) continue;
        (health[step.stepId] ??= []).push({ status: step.status, startedAt: step.startedAt });
      }
      for (const stepId of Object.keys(health)) {
        health[stepId] = health[stepId]
          .sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt))
          .slice(0, limit);
      }
      return json(health);
    })),

  route('GET', '/workflows/:id/coverage', (ctx) =>
    withWorkflow(ctx, (workflow) => {
      const world = getWorld();
      const runs = world.executions.filter((e) => e.workflowId === workflow.id);
      const windowDays = Number(ctx.query.get('windowDays') ?? 30);
      const since = Date.now() - windowDays * 24 * 60 * 60_000;
      const inWindow = runs.filter((e) => Date.parse(e.startedAt) >= since);
      const ids = new Set(inWindow.map((e) => e.id));
      const rows = [...world.steps.entries()].filter(([id]) => ids.has(id)).flatMap(([, steps]) => steps);

      const nodes = nodesOf(workflow)
        .filter((n) => n.data?.activityType !== 'note' && n.data?.activityType !== 'group')
        .map((node) => {
          const runsOfNode = rows.filter((r) => r.stepId === node.id);
          const succeeded = runsOfNode.filter((r) => r.status === 'Succeeded');
          const failures = runsOfNode.filter((r) => r.status === 'Failed');
          const newest = (list: typeof runsOfNode) =>
            list.map((r) => r.completedAt ?? r.startedAt).filter((v): v is string => !!v)
              .sort((a, b) => Date.parse(b) - Date.parse(a))[0] ?? null;
          return {
            stepId: node.id,
            executedCount: runsOfNode.length,
            failedCount: failures.length,
            skippedCount: runsOfNode.filter((r) => r.status === 'Skipped').length,
            lastExecutedAt: newest(runsOfNode),
            lastSucceededAt: newest(succeeded),
            lastFailedAt: newest(failures),
          };
        });

      return json({
        workflowId: workflow.id,
        windowDays,
        totalExecutions: inWindow.length,
        oldestExecutionInWindow: inWindow.at(-1)?.startedAt ?? null,
        nodes,
      });
    })),

  // Genuinely server-side: a step test runs PowerShell on a remote host.
  route('POST', '/workflows/:id/steps/:stepId/test', () => notInDemo('Testing a single step')),
  route('GET', '/workflows/:id/steps/:stepId/test-context', () =>
    json({ executionId: null, executedAt: null, status: null, variables: [] })),
  route('GET', '/workflows/:id/steps/:stepId/test-context/runs', () => json([])),
];
