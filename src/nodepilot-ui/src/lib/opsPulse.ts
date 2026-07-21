// System-pulse derivation for the Mission-Control header: collapses the folder-scoped recent
// executions plus global infrastructure signals (heartbeats, machines, queue) into a single
// NOMINAL / DEGRADED / INCIDENT state with the contributing reasons. Pure — the caller passes
// its clock.

export type PulseState = 'nominal' | 'degraded' | 'incident';

export type PulseReason =
  | 'failureSpike'
  | 'recentFailure'
  | 'staleHeartbeat'
  | 'allHeartbeatsStale'
  | 'machinesUnreachable'
  | 'allMachinesUnreachable'
  | 'longRunning';

export interface PulseInput {
  nowMs: number;
  /** Folder-scoped recently finished executions (status + completion time). */
  recent: { status: string; completedAtMs: number }[];
  /** Global background-service heartbeats. */
  heartbeats: { isStale: boolean }[];
  machinesTotal: number;
  machinesReachable: number;
  longRunningCount: number;
}

const INCIDENT_FAILURE_COUNT = 5;
const INCIDENT_FAILURE_WINDOW_MS = 10 * 60_000;
const DEGRADED_FAILURE_WINDOW_MS = 15 * 60_000;

export function derivePulseState(i: PulseInput): { state: PulseState; reasons: PulseReason[] } {
  const reasons: PulseReason[] = [];

  const failuresIn = (windowMs: number) =>
    i.recent.filter((r) => r.status === 'Failed' && i.nowMs - r.completedAtMs <= windowMs).length;

  const spikeFailures = failuresIn(INCIDENT_FAILURE_WINDOW_MS);
  const recentFailures = failuresIn(DEGRADED_FAILURE_WINDOW_MS);
  const staleHeartbeats = i.heartbeats.filter((h) => h.isStale).length;
  const allStale = i.heartbeats.length > 0 && staleHeartbeats === i.heartbeats.length;
  const allMachinesDown = i.machinesTotal > 0 && i.machinesReachable === 0;
  const someMachinesDown = i.machinesReachable < i.machinesTotal;

  // Incident-grade signals.
  if (spikeFailures >= INCIDENT_FAILURE_COUNT) reasons.push('failureSpike');
  if (allMachinesDown) reasons.push('allMachinesUnreachable');
  if (allStale) reasons.push('allHeartbeatsStale');
  const incident = reasons.length > 0;

  // Degraded-grade signals (also collected during an incident so the health rail can
  // highlight every contributor).
  if (!incident && recentFailures > 0) reasons.push('recentFailure');
  if (!allStale && staleHeartbeats > 0) reasons.push('staleHeartbeat');
  if (!allMachinesDown && someMachinesDown) reasons.push('machinesUnreachable');
  if (i.longRunningCount > 0) reasons.push('longRunning');

  if (incident) return { state: 'incident', reasons };
  return { state: reasons.length > 0 ? 'degraded' : 'nominal', reasons };
}
