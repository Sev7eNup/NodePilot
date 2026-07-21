import { api } from './client';
import type { OperationsGraph, WorkflowExecution } from '../types/api';

/** RBAC-folder-scoped snapshot for the live-ops Mission-Control view. */
export function getOperationsGraph() {
  return api.get<OperationsGraph>('/operations/graph');
}

/** Upcoming scheduled start of an enabled, non-manual-triggered workflow. */
export interface OpsArmedTrigger {
  workflowId: string;
  workflowName: string;
  triggerTypes: string[];
  nextFireUtc: string | null;
  nextFireKind: 'cron' | 'event-driven' | 'polling' | null;
  pollIntervalSeconds: number | null;
}

export interface OpsHeartbeat {
  serviceName: string;
  lastHeartbeatAt: string;
  expectedIntervalSeconds: number;
  status: string | null;
  isStale: boolean;
}

/**
 * The subset of `/stats/dashboard` the Mission-Control view needs (departure board, health
 * rail, pulse header). The endpoint returns far more; extra fields are simply ignored.
 */
export interface OpsDashboardStats {
  machinesTotal: number;
  machinesReachable: number;
  pendingCount: number;
  runningCount: number;
  longRunningCount: number;
  clusterRole: string | null;
  healthHeartbeats: OpsHeartbeat[];
  armedTriggers?: OpsArmedTrigger[];
}

/** Global (RBAC-scoped) operational stats: queue depth, machines, heartbeats, next fires. */
export function getOpsDashboardStats() {
  return api.get<OpsDashboardStats>('/stats/dashboard?windowHours=24');
}

/** Cancel a single running execution (reuses the existing executions endpoint). */
export function cancelExecution(executionId: string) {
  return api.post<void>(`/executions/${executionId}/cancel`);
}

/** Execution detail for the drilldown (error, triggeredBy, parent link). */
export function getExecution(executionId: string) {
  return api.get<WorkflowExecution>(`/executions/${executionId}`);
}
