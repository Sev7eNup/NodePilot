/**
 * Dashboard aggregate, computed from the execution history.
 *
 * Every number on the dashboard is a reduction over `world.executions`, so it cannot
 * disagree with the executions list or a workflow's own run counters. The seed never states
 * a total.
 */
import type { StepExecution, Workflow, WorkflowExecution } from '../../src/types/api';
import type { DemoWorld } from '../state/world';
import { DEMO_HOST, DEMO_USER } from './entities';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** Mirror of the `DashboardStats` shape `DashboardPage` reads. */
export interface DemoDashboardStats {
  workflowsTotal: number;
  workflowsEnabled: number;
  machinesTotal: number;
  machinesReachable: number;
  executionsTotal: number;
  last24h: { total: number; succeeded: number; failed: number; running: number; cancelled: number };
  last24hBuckets: { hourStart: string; succeeded: number; failed: number; cancelled: number }[];
  topWorkflows: { id: string; name: string; runCount: number; successCount: number; failCount: number; avgDurationMs: number | null; p95DurationMs: number | null }[];
  running: { id: string; workflowId: string; workflowName: string; status: string; startedAt: string; triggeredBy: string | null }[];
  recent: { id: string; workflowId: string; workflowName: string; status: string; startedAt: string; completedAt: string | null; durationMs: number | null; triggeredBy: string | null }[];
  armedTriggers: { workflowId: string; workflowName: string; triggerTypes: string[]; nextFireUtc: string | null; nextFireKind: 'cron' | 'event-driven' | 'polling' | null; pollIntervalSeconds: number | null }[];
  pendingCount: number;
  runningCount: number;
  longRunningCount: number;
  longRunningSeconds: number;
  retryStats: { finishedCount: number; retriedCount: number };
  failingWorkflows: { id: string; name: string; failCount: number; runCount: number; lastFailureAt: string | null }[];
  editLocks: { workflowId: string; workflowName: string; lockOwnerUserName: string; lockedAt: string }[];
  healthHeartbeats: { serviceName: string; lastHeartbeatAt: string; expectedIntervalSeconds: number; status: string | null; isStale: boolean }[];
  databaseProvider: string;
  clusterRole: string | null;
  recentAudit: { timestamp: string; actorUserName: string | null; action: string; resourceType: string | null; resourceId: string | null }[] | null;
  llmEnabled: boolean;
}

function durationOf(execution: WorkflowExecution): number | null {
  if (!execution.completedAt) return null;
  return Date.parse(execution.completedAt) - Date.parse(execution.startedAt);
}

function percentile(values: number[], fraction: number): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.floor(fraction * sorted.length));
  return sorted[index];
}

/**
 * Bucket geometry per window, matching what the chart labels: two-minute buckets for the
 * one-hour window, hourly buckets up to a day, daily buckets beyond.
 */
function bucketPlan(windowHours: number): { count: number; widthMs: number } {
  if (windowHours === 1) return { count: 30, widthMs: 2 * MINUTE };
  if (windowHours <= 24) return { count: windowHours, widthMs: HOUR };
  return { count: Math.round(windowHours / 24), widthMs: DAY };
}

/** Distinct trigger activity types declared by a workflow definition. */
function triggerTypesOf(workflow: Workflow): string[] {
  try {
    const parsed = JSON.parse(workflow.definitionJson) as { nodes?: { data?: { activityType?: string; disabled?: boolean } }[] };
    const types = (parsed.nodes ?? [])
      .filter((n) => !n.data?.disabled && (n.data?.activityType ?? '').endsWith('Trigger'))
      .map((n) => n.data!.activityType!);
    return [...new Set(types)];
  } catch {
    return [];
  }
}

export function buildDashboard(world: DemoWorld, windowHours: number, now: number): DemoDashboardStats {
  const nameById = new Map(world.workflows.map((w) => [w.id, w.name]));
  const windowStart = now - windowHours * HOUR;
  const inWindow = world.executions.filter((e) => Date.parse(e.startedAt) >= windowStart);

  const countBy = (status: string, list: WorkflowExecution[]) => list.filter((e) => e.status === status).length;

  const { count, widthMs } = bucketPlan(windowHours);
  const bucketStart = now - count * widthMs;
  const last24hBuckets = Array.from({ length: count }, (_, i) => {
    const from = bucketStart + i * widthMs;
    const slice = world.executions.filter((e) => {
      const at = Date.parse(e.startedAt);
      return at >= from && at < from + widthMs;
    });
    return {
      hourStart: new Date(from).toISOString(),
      succeeded: countBy('Succeeded', slice),
      failed: countBy('Failed', slice),
      cancelled: countBy('Cancelled', slice),
    };
  });

  const topWorkflows = world.workflows
    .map((workflow) => {
      const runs = inWindow.filter((e) => e.workflowId === workflow.id);
      const durations = runs.map(durationOf).filter((d): d is number => d !== null);
      return {
        id: workflow.id,
        name: workflow.name,
        runCount: runs.length,
        successCount: countBy('Succeeded', runs),
        failCount: countBy('Failed', runs),
        avgDurationMs: durations.length === 0 ? null : Math.round(durations.reduce((a, b) => a + b, 0) / durations.length),
        p95DurationMs: percentile(durations, 0.95),
      };
    })
    .filter((row) => row.runCount > 0)
    .sort((a, b) => b.runCount - a.runCount)
    .slice(0, 5);

  const running = world.executions
    .filter((e) => e.status === 'Running' || e.status === 'Paused')
    .map((e) => ({
      id: e.id,
      workflowId: e.workflowId,
      workflowName: nameById.get(e.workflowId) ?? 'Unknown',
      status: e.status,
      startedAt: e.startedAt,
      triggeredBy: e.triggeredBy,
    }));

  const recent = world.executions.slice(0, 12).map((e) => ({
    id: e.id,
    workflowId: e.workflowId,
    workflowName: nameById.get(e.workflowId) ?? 'Unknown',
    status: e.status,
    startedAt: e.startedAt,
    completedAt: e.completedAt,
    durationMs: durationOf(e),
    triggeredBy: e.triggeredBy,
  }));

  const failingWorkflows = world.workflows
    .map((workflow) => {
      const runs = inWindow.filter((e) => e.workflowId === workflow.id);
      const failures = runs.filter((e) => e.status === 'Failed');
      return {
        id: workflow.id,
        name: workflow.name,
        failCount: failures.length,
        runCount: runs.length,
        lastFailureAt: failures[0]?.completedAt ?? null,
      };
    })
    .filter((row) => row.failCount > 0)
    .sort((a, b) => b.failCount - a.failCount);

  const armedTriggers = world.workflows
    .filter((w) => w.isEnabled)
    .map((workflow) => {
      const types = triggerTypesOf(workflow);
      const hasCron = types.includes('scheduleTrigger');
      return {
        workflowId: workflow.id,
        workflowName: workflow.name,
        triggerTypes: types,
        nextFireUtc: hasCron ? new Date(now + 3 * HOUR).toISOString() : null,
        nextFireKind: hasCron ? ('cron' as const) : types.length > 0 ? ('event-driven' as const) : null,
        pollIntervalSeconds: null,
      };
    })
    .filter((row) => row.triggerTypes.length > 0);

  const editLocks = world.workflows
    .filter((w) => w.checkedOutByUserId)
    .map((w) => ({
      workflowId: w.id,
      workflowName: w.name,
      lockOwnerUserName: w.checkedOutByUserName ?? DEMO_USER.username,
      lockedAt: w.checkedOutAt ?? new Date(now).toISOString(),
    }));

  return {
    workflowsTotal: world.workflows.length,
    workflowsEnabled: world.workflows.filter((w) => w.isEnabled).length,
    machinesTotal: world.machines.length,
    machinesReachable: world.machines.filter((m) => m.isReachable).length,
    executionsTotal: world.executions.length,
    last24h: {
      total: inWindow.length,
      succeeded: countBy('Succeeded', inWindow),
      failed: countBy('Failed', inWindow),
      running: countBy('Running', inWindow),
      cancelled: countBy('Cancelled', inWindow),
    },
    last24hBuckets,
    topWorkflows,
    running,
    recent,
    armedTriggers,
    pendingCount: countBy('Pending', world.executions),
    runningCount: running.length,
    longRunningCount: 0,
    longRunningSeconds: 900,
    // "Retries needed" counts runs that were actually retried, not runs that failed — the two
    // are different things, and equating them reported a retry rate the demo never had. Nothing
    // in the seed is a retry, so this reads 0; clicking Retry on a run makes it move, because
    // the retry handler starts the replay with `triggeredBy: 'retry'`.
    retryStats: {
      finishedCount: world.executions.filter((e) => e.completedAt).length,
      retriedCount: world.executions.filter((e) => e.triggeredBy === 'retry').length,
    },
    failingWorkflows,
    editLocks,
    healthHeartbeats: [
      { serviceName: 'Scheduler', lastHeartbeatAt: new Date(now - 12_000).toISOString(), expectedIntervalSeconds: 30, status: 'ok', isStale: false },
      { serviceName: 'TriggerOrchestrator', lastHeartbeatAt: new Date(now - 4_000).toISOString(), expectedIntervalSeconds: 30, status: 'ok', isStale: false },
    ],
    databaseProvider: 'PostgreSQL',
    clusterRole: null,
    recentAudit: null,
    llmEnabled: false,
  };
}

/** Counters the sidebar badge polls, derived from the same world. */
export function buildSidebarCounts(world: DemoWorld) {
  return {
    workflowsTotal: world.workflows.length,
    runningCount: world.executions.filter((e) => e.status === 'Running' || e.status === 'Paused').length,
    machinesTotal: world.machines.length,
  };
}

/** Host chip shown in the top bar. */
export function hostInfo() {
  return DEMO_HOST;
}

/**
 * Per-step aggregates for the designer's performance overlay, keyed by step id.
 *
 * An object, not a list: `useNodeAnnotations` looks each node up as `stepStats[node.id]`, so
 * an array silently yields nothing and every badge stays empty.
 */
export function buildStepStats(steps: StepExecution[]): Record<string, {
  totalRuns: number; failedRuns: number; failureRate: number;
  avgDurationMs: number; p95DurationMs: number; lastDurationMs: number;
}> {
  const byStep = new Map<string, StepExecution[]>();
  for (const step of steps) {
    const list = byStep.get(step.stepId) ?? [];
    list.push(step);
    byStep.set(step.stepId, list);
  }

  const durationOf = (row: StepExecution) =>
    row.startedAt && row.completedAt ? Date.parse(row.completedAt) - Date.parse(row.startedAt) : null;

  const stats: Record<string, {
    totalRuns: number; failedRuns: number; failureRate: number;
    avgDurationMs: number; p95DurationMs: number; lastDurationMs: number;
  }> = {};

  for (const [stepId, rows] of byStep) {
    const ordered = [...rows].sort((a, b) => Date.parse(b.startedAt ?? '') - Date.parse(a.startedAt ?? ''));
    const durations = ordered.map(durationOf).filter((d): d is number => d !== null);
    const failedRuns = rows.filter((r) => r.status === 'Failed').length;
    stats[stepId] = {
      totalRuns: rows.length,
      failedRuns,
      failureRate: rows.length === 0 ? 0 : failedRuns / rows.length,
      avgDurationMs: durations.length === 0 ? 0 : Math.round(durations.reduce((a, b) => a + b, 0) / durations.length),
      p95DurationMs: percentile(durations, 0.95) ?? 0,
      lastDurationMs: durations[0] ?? 0,
    };
  }
  return stats;
}
