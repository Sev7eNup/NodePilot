import { api } from './client';
import type { OperationsGraph, WorkflowExecution } from '../types/api';

/**
 * RBAC-folder-scoped snapshot for the live-ops Mission-Control view.
 * `windowMinutes` selects how far back settled runs are returned (server accepts 30|60 and
 * clamps everything else to 30);
 * running executions are always returned in full, regardless of the window.
 */
export function getOperationsGraph(windowMinutes = 30) {
  return api.get<OperationsGraph>(`/operations/graph?windowMinutes=${windowMinutes}`);
}

/** Upcoming scheduled start of an enabled, non-manual-triggered workflow. */
export interface OpsArmedTrigger {
  workflowId: string;
  workflowName: string;
  triggerTypes: string[];
  nextFireUtc: string | null;
  nextFireKind: 'cron' | 'event-driven' | 'polling' | null;
  pollIntervalSeconds: number | null;
  /**
   * Maintenance window that will suppress this start, evaluated by the backend at the
   * predicted fire time (the same predicate TriggerOrchestrator applies when it fires).
   * Null when nothing blocks it.
   */
  blockedByWindowName: string | null;
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

/** Execution detail for the drilldown (error, triggeredBy, parent link, failed steps). */
export function getExecution(executionId: string) {
  return api.get<WorkflowExecution>(`/executions/${executionId}`);
}

/** Rerun a terminal execution with the original input parameters. Creates a NEW execution. */
export function retryExecution(executionId: string) {
  return api.post<WorkflowExecution>(`/executions/${executionId}/retry`);
}

/**
 * Cancel every Running/Pending execution of a workflow.
 * `total` = runs found, `signalled` = runs reached in-memory; the remainder are force-cancelled
 * in the DB (orphans from a previous API process). Report `total` to the user — `signalled`
 * undercounts.
 */
export interface CancelAllResult {
  total: number;
  signalled: number;
}

export function cancelAllForWorkflow(workflowId: string) {
  return api.post<CancelAllResult>(`/workflows/${workflowId}/cancel-all`);
}

/** Kill switch: stop the workflow from firing again. Leaves in-flight runs alone. */
export function disableWorkflow(workflowId: string) {
  return api.post<void>(`/workflows/${workflowId}/disable`);
}

/**
 * Quarantine = disable + cancel-all (the documented incident response).
 *
 * Disable FIRST and this order is load-bearing: cancelling the runs while the triggers are
 * still armed just lets TriggerOrchestrator start them again on its next 5 s sync.
 *
 * NOT atomic — two requests. If cancel-all fails after disable succeeded, the workflow is
 * safely off but its runs keep going; that partial state is reported separately so the
 * operator can retry just the cancel (see `QuarantineOutcome.disabled`).
 */
export interface QuarantineOutcome {
  /** The disable step went through — the workflow can no longer be triggered. */
  disabled: boolean;
  /** Null when cancel-all did not complete; the caller must offer a retry of that step alone. */
  cancelled: CancelAllResult | null;
}

export async function quarantineWorkflow(workflowId: string): Promise<QuarantineOutcome> {
  await disableWorkflow(workflowId);
  try {
    return { disabled: true, cancelled: await cancelAllForWorkflow(workflowId) };
  } catch {
    return { disabled: true, cancelled: null };
  }
}
