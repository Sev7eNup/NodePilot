/**
 * The demo's most important test.
 *
 * The seed graphs are workflow JSON the repository already ships, authored against whatever
 * activity catalog existed at the time. If one of them has drifted, the designer renders
 * unknown nodes and the lint panel lights up red — in the one screen the demo exists for.
 * This test turns graph selection into a mechanical check instead of a hope.
 *
 * It also pins the derivation rule: the dashboard must be a reduction over the execution
 * history, never a set of numbers written next to it.
 */
import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { ACTIVITY_CATALOG } from '../../lib/activityCatalog.generated';
import { lintWorkflow } from '../../lib/workflowLint';
import { simulateWorkflow } from '../../lib/workflowSimulation';
import { seedGraphs } from '../../../demo/seed/graphs';
import { buildWorld } from '../../../demo/seed/build';
import { buildDashboard } from '../../../demo/seed/dashboard';
import { credentialIds, machineIds } from '../../../demo/seed/entities';
import { definitionOf } from '../../../demo/state/world';
import { ROOT_FOLDER_ID as WORKFLOW_ROOT } from '../../api/sharedFolders';
import { ROOT_FOLDER_ID as GLOBALS_ROOT } from '../../api/globalFolders';

const NOW = Date.parse('2026-09-18T12:00:00.000Z');
const ANNOTATION_TYPES = new Set(['note', 'group']);
const knownTypes = new Set<string>(ACTIVITY_CATALOG.map((entry) => entry.type));

const graphs = seedGraphs(machineIds(), credentialIds());
const world = buildWorld(NOW);

describe('demo seed graphs', () => {
  it('ships the workflows the demo advertises', () => {
    expect(graphs.length).toBeGreaterThanOrEqual(4);
    expect(new Set(graphs.map((g) => g.key)).size).toBe(graphs.length);
  });

  it.each(graphs.map((g) => [g.key, g] as const))('%s uses only catalogued activity types', (_key, graph) => {
    const unknown = graph.nodes
      .map((node) => node.data?.activityType)
      .filter((type): type is string => typeof type === 'string')
      .filter((type) => !ANNOTATION_TYPES.has(type) && !type.startsWith('custom:') && !knownTypes.has(type));
    expect(unknown).toEqual([]);
  });

  it.each(graphs.map((g) => [g.key, g] as const))('%s passes the canvas linter with no errors', (_key, graph) => {
    const known = graphs.map((g) => ({ id: g.key, name: g.name }));
    const result = lintWorkflow(graph.nodes as unknown as Node[], graph.edges as unknown as Edge[], known);
    // Errors block publishing in the product, so a seeded graph that carries one would make
    // the demo's publish button dead on arrival.
    expect(result.errors.map((issue) => `${issue.code}: ${issue.message}`)).toEqual([]);
  });

  it.each(graphs.map((g) => [g.key, g] as const))('%s reaches at least one step from its triggers', (_key, graph) => {
    expect(simulateWorkflow(graph.nodes, graph.edges).order.length).toBeGreaterThan(0);
  });

  it('points every machine and credential reference at the demo world', () => {
    const machines = new Set(machineIds());
    const credentials = new Set(credentialIds());
    for (const graph of graphs) {
      for (const node of graph.nodes) {
        const machine = node.data?.targetMachineId;
        if (typeof machine === 'string' && machine) expect(machines.has(machine)).toBe(true);
        const credential = (node.data as { credentialId?: string | null } | undefined)?.credentialId;
        if (typeof credential === 'string' && credential) expect(credentials.has(credential)).toBe(true);
      }
    }
  });
});

describe('demo world referential integrity', () => {
  it('links every execution to a workflow that exists', () => {
    const ids = new Set(world.workflows.map((w) => w.id));
    for (const execution of world.executions) expect(ids.has(execution.workflowId)).toBe(true);
  });

  it('links every step to a node of its own workflow', () => {
    for (const [executionId, steps] of world.steps) {
      const execution = world.executions.find((e) => e.id === executionId);
      expect(execution).toBeDefined();
      const workflow = world.workflows.find((w) => w.id === execution!.workflowId)!;
      const nodeIds = new Set(definitionOf(workflow).nodes.map((n) => n.id));
      for (const step of steps) expect(nodeIds.has(step.stepId)).toBe(true);
    }
  });

  it('gives every workflow counters that match its runs', () => {
    for (const workflow of world.workflows) {
      const runs = world.executions.filter((e) => e.workflowId === workflow.id);
      expect(workflow.totalCount).toBe(runs.length);
      expect(workflow.successCount).toBe(runs.filter((e) => e.status === 'Succeeded').length);
      expect(workflow.lastExecution?.id ?? null).toBe(runs[0]?.id ?? null);
    }
  });

  it('counts folder contents from the workflow list', () => {
    for (const folder of world.folders) {
      expect(folder.workflowCount).toBe(world.workflows.filter((w) => w.folderId === folder.id).length);
    }
  });

  it('files globals under their own root, not the workflow-folder one', () => {
    // Two different sentinels, one GUID apart. The globals page filters by the folder it has
    // selected, so the wrong constant renders an empty list while the API returns five rows —
    // a failure that looks like "no data" rather than like a bug.
    expect(GLOBALS_ROOT).not.toBe(WORKFLOW_ROOT);
    const globalFolderIds = new Set(world.globalFolders.map((f) => f.id));
    expect(globalFolderIds.has(GLOBALS_ROOT)).toBe(true);
    for (const variable of world.globals) expect(globalFolderIds.has(variable.folderId)).toBe(true);
    for (const folder of world.folders) expect(folder.id).not.toBe(GLOBALS_ROOT);
    for (const folder of world.globalFolders) expect(folder.id).not.toBe(WORKFLOW_ROOT);
  });

  it('seeds workflows unlocked and enabled', () => {
    // "Locked and enabled" is unreachable in the product, because locking disables the
    // workflow atomically. Seeding it would show a state the engine never produces.
    for (const workflow of world.workflows) {
      expect(workflow.checkedOutByUserId).toBeNull();
      expect(workflow.isEnabled).toBe(true);
    }
  });
});

describe('demo dashboard', () => {
  const stats = buildDashboard(world, 24, NOW);

  it('derives its totals from the world rather than stating them', () => {
    expect(stats.workflowsTotal).toBe(world.workflows.length);
    expect(stats.machinesTotal).toBe(world.machines.length);
    expect(stats.executionsTotal).toBe(world.executions.length);
    expect(stats.machinesReachable).toBe(world.machines.filter((m) => m.isReachable).length);
  });

  it('derives the window counts from the executions in that window', () => {
    const since = NOW - 24 * 60 * 60_000;
    const inWindow = world.executions.filter((e) => Date.parse(e.startedAt) >= since);
    expect(stats.last24h.total).toBe(inWindow.length);
    expect(stats.last24h.succeeded).toBe(inWindow.filter((e) => e.status === 'Succeeded').length);
    expect(stats.last24h.failed).toBe(inWindow.filter((e) => e.status === 'Failed').length);
  });

  it('spreads runs across the window instead of stacking them on the last bucket', () => {
    // Without a per-workflow phase offset every workflow's newest run lands just before now,
    // and the hourly chart shows one implausible spike at the right edge.
    const populated = stats.last24hBuckets.filter((b) => b.succeeded + b.failed + b.cancelled > 0);
    expect(populated.length).toBeGreaterThan(1);
  });

  it('puts recent runs inside the window Live-Ops opens on', () => {
    // Live-Ops defaults to 30 minutes. A seed whose newest run is older than that shows a
    // visitor an empty console on their very first look — and the per-run jitter is wide
    // enough to cause exactly that if it is applied to the newest run.
    const newest = Math.max(...world.executions.map((e) => Date.parse(e.startedAt)));
    expect((NOW - newest) / 60_000).toBeLessThan(30);
  });

  it('reads as a healthy installation without looking staged', () => {
    // The demo is a shop window: a fleet that mostly works. But not a flat 100 % — an empty
    // "most frequent failure causes" panel next to a perfect gauge reads as fabricated.
    const total = world.executions.length;
    const succeeded = world.executions.filter((e) => e.status === 'Succeeded').length;
    const failed = world.executions.filter((e) => e.status === 'Failed').length;
    const successRate = succeeded / total;

    expect(successRate, `success rate ${(successRate * 100).toFixed(1)}%`).toBeGreaterThan(0.9);
    expect(successRate, `success rate ${(successRate * 100).toFixed(1)}%`).toBeLessThan(0.99);
    expect(failed, 'no failure at all leaves the failure-causes panel empty').toBeGreaterThan(0);

    // "Retries needed" counts retried runs, not failed ones. Nothing in the seed is a retry, so
    // the dashboard opens at 0 % — and only moves if a visitor retries something themselves.
    expect(stats.retryStats.retriedCount).toBe(0);
    expect(stats.retryStats.finishedCount).toBeGreaterThan(0);
    expect(world.executions.some((e) => e.triggeredBy === 'retry')).toBe(false);
  });

  it('shows a healthy window on the dashboard too', () => {
    // The 24-hour window is what a visitor sees first, and a thin window swings wildly — so it
    // is asserted separately from the lifetime rate.
    const inWindow = stats.last24h;
    expect(inWindow.total, 'a near-empty window makes every rate meaningless').toBeGreaterThan(10);
    const rate = inWindow.succeeded / inWindow.total;
    expect(rate, `24h success rate ${(rate * 100).toFixed(1)}% over ${inWindow.total} runs`).toBeGreaterThan(0.9);
  });

  it('reports no running executions before anything is started', () => {
    expect(stats.running).toEqual([]);
    expect(stats.runningCount).toBe(0);
  });
});
