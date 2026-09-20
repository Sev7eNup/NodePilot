/**
 * Composes the demo world from the authored facts and the derivations.
 *
 * Order matters and is the point: workflows are built from the seed graphs, the history is
 * derived from those workflows, and the per-workflow and per-machine counters are then
 * derived from the history. No counter is ever written by hand.
 */
import { ROOT_FOLDER_ID } from '../../src/api/sharedFolders';
import type { Workflow } from '../../src/types/api';
import type { DemoWorkflowVersion, DemoWorld } from '../state/world';
import { demoId } from '../state/ids';
import { seedGraphs } from './graphs';
import { buildHistory } from './executions';
import { buildCustomActivities } from './customActivities';
import {
  DEMO_USER,
  buildCredentials,
  buildFolderPermissions,
  buildFolders,
  buildGlobalFolders,
  buildGlobals,
  buildMachines,
  buildMaintenanceWindows,
  buildUsers,
  credentialIds,
  folderIdOf,
  iso,
  machineIds,
} from './entities';

const DAY = 24 * 60 * 60_000;

export function buildWorld(now: number = Date.now()): DemoWorld {
  const credentials = buildCredentials(now);
  const machines = buildMachines(now, credentials);
  const folders = buildFolders(now);
  const folderPermissions = buildFolderPermissions(now);
  const globals = buildGlobals(now);
  const globalFolders = buildGlobalFolders(now);
  const customActivities = buildCustomActivities(now);
  const maintenanceWindows = buildMaintenanceWindows(now);
  const users = buildUsers(now);

  const graphs = seedGraphs(machineIds(), credentialIds());

  // Seed workflows are unlocked and enabled — the state a published workflow is actually in.
  // Locking one disables it (the product does that atomically), so seeding "locked and
  // enabled" would show a state the engine never produces and would bypass the run gate.
  const workflows: Workflow[] = graphs.map((graph, index) => {
    const folder = folders.find((f) => f.id === folderIdOf(graph.folderKey));
    const definitionJson = JSON.stringify({ nodes: graph.nodes, edges: graph.edges });
    const triggerTypes = [
      ...new Set(
        graph.nodes
          .filter((n) => !n.data?.disabled && (n.data?.activityType ?? '').endsWith('Trigger'))
          .map((n) => n.data!.activityType!),
      ),
    ];
    return {
      id: demoId(`workflow:${graph.key}`),
      name: graph.name,
      description: graph.description,
      version: 3 + index,
      isEnabled: true,
      createdAt: iso(-(60 - index * 4) * DAY, now),
      updatedAt: iso(-(3 + index) * DAY, now),
      createdBy: DEMO_USER.username,
      updatedBy: DEMO_USER.username,
      activityCount: graph.nodes.filter((n) => n.data?.activityType !== 'note').length,
      triggerTypes,
      lastExecution: null,
      successCount: 0,
      totalCount: 0,
      avgDurationMs: null,
      checkedOutByUserId: null,
      checkedOutByUserName: null,
      checkedOutAt: null,
      maxConcurrentExecutions: index === 1 ? 1 : null,
      folderId: folder?.id ?? ROOT_FOLDER_ID,
      folderPath: folder?.path ?? '/',
      capabilities: { canRead: true, canRun: true, canEdit: true, canDelete: true, canAdmin: true },
      definitionJson,
    };
  });

  const { executions, steps } = buildHistory(workflows, now);

  // Per-workflow run counters, derived from the history just built.
  for (const workflow of workflows) {
    const runs = executions.filter((e) => e.workflowId === workflow.id);
    const durations = runs
      .filter((e) => e.completedAt)
      .map((e) => Date.parse(e.completedAt!) - Date.parse(e.startedAt));
    const latest = runs[0];
    workflow.totalCount = runs.length;
    workflow.successCount = runs.filter((e) => e.status === 'Succeeded').length;
    workflow.avgDurationMs = durations.length === 0
      ? null
      : Math.round(durations.reduce((a, b) => a + b, 0) / durations.length);
    workflow.lastExecution = latest
      ? {
          id: latest.id,
          status: latest.status,
          startedAt: latest.startedAt,
          completedAt: latest.completedAt,
          durationMs: latest.completedAt ? Date.parse(latest.completedAt) - Date.parse(latest.startedAt) : null,
        }
      : null;
  }

  // Machine counters, derived from the graphs (usage) and the history (recent activity).
  const sevenDaysAgo = now - 7 * DAY;
  for (const machine of machines) {
    machine.usedByWorkflowCount = workflows.filter((w) => w.definitionJson.includes(machine.id)).length;
    let recent = 0;
    let failed = 0;
    for (const [executionId, rows] of steps) {
      const execution = executions.find((e) => e.id === executionId);
      if (!execution || Date.parse(execution.startedAt) < sevenDaysAgo) continue;
      for (const row of rows) {
        if (row.targetMachine !== machine.id) continue;
        recent += 1;
        if (row.status === 'Failed') failed += 1;
      }
    }
    machine.recentStepCount = recent;
    machine.recentFailedStepCount = failed;
    machine.activeRunCount = 0;
  }

  // Folder workflow counts.
  for (const folder of folders) {
    folder.workflowCount = workflows.filter((w) => w.folderId === folder.id).length;
  }

  // One historical version per workflow, so the diff and rollback surfaces have something
  // to show. The stored definition drops the newest node **and every edge touching it**:
  // keeping those edges would leave dangling references, the linter would reject the graph,
  // and a rollback would land the visitor on a workflow that cannot be published.
  const versions = new Map<string, DemoWorkflowVersion[]>();
  for (const workflow of workflows) {
    const parsed = JSON.parse(workflow.definitionJson) as {
      nodes: { id: string }[]
      edges: { source: string; target: string }[]
    };
    const dropped = parsed.nodes.at(-1)?.id;
    const previous = JSON.stringify({
      nodes: parsed.nodes.slice(0, -1),
      edges: parsed.edges.filter((e) => e.source !== dropped && e.target !== dropped),
    });
    versions.set(workflow.id, [
      {
        version: workflow.version,
        isCurrent: true,
        createdAt: workflow.updatedAt,
        createdBy: workflow.updatedBy,
        changeNote: null,
        definitionJson: workflow.definitionJson,
      },
      {
        version: workflow.version - 1,
        isCurrent: false,
        createdAt: iso(-20 * DAY, now),
        createdBy: DEMO_USER.username,
        changeNote: 'Before the final step was added',
        definitionJson: previous,
      },
    ]);
  }

  // Variable counts per folder, derived like every other counter.
  for (const folder of globalFolders) {
    folder.variableCount = globals.filter((g) => g.folderId === folder.id).length;
  }

  return {
    workflows,
    versions,
    executions,
    steps,
    machines,
    credentials,
    globals,
    globalFolders,
    folders,
    folderPermissions,
    customActivities,
    // Filled on the first save; the seed ships no prior versions of its own definitions.
    customActivityVersions: new Map(),
    maintenanceWindows,
    users,
  };
}
