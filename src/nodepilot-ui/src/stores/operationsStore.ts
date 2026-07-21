import { create } from 'zustand';
import type { OpsRunningExecution } from '../types/api';

// Status strings that mean "a run is currently in flight".
const ACTIVE = new Set(['Running', 'Pending']);
// Status strings that mark a run as finished. Only these (and never active) arrive via the
// running[] snapshot's per-exec `status` — terminal transitions come exclusively via SignalR.
const TERMINAL = new Set(['Succeeded', 'Failed', 'Cancelled', 'Canceled', 'TimedOut']);

// Tombstones are kept for 24h purely as a MEMORY BOUND. The race-safety comes from the fact
// that execution ids are GUIDs and are never reused — so a tombstoned id can never refer to
// a fresh, genuinely-active run. 24h is far longer than any snapshot staleness, which means a
// tombstone is always present when a stale snapshot tries to resurrect a just-terminated run.
const TOMBSTONE_TTL_MS = 24 * 60 * 60 * 1000;

// Locally-settled runs only need to survive until the next snapshot (5 s poll + debounce)
// confirms them via recent[]; 3 min is a generous upper bound before they expire on their own.
const LOCAL_SETTLED_TTL_MS = 3 * 60_000;

// Ring-buffer cap for the live event ticker.
const TICKER_CAP = 50;

/** A live execution entry: id + its most recent active status (Running|Pending). */
export interface LiveExec {
  id: string;
  status: string;
  /** Start time (from the snapshot, or first-seen time for SignalR-discovered runs). */
  startedAtMs: number;
}

/** One row of the live event ticker (an ExecutionStatusChanged delta as received). */
export interface TickerEvent {
  executionId: string;
  workflowId: string;
  status: string;
  /** Client receive time. */
  atMs: number;
}

/**
 * A run that terminated via SignalR but is not yet confirmed by a snapshot's recent[] list.
 * Lets the timeline render the settled bar instantly instead of flickering out until the
 * debounced refetch lands. startedAtMs is null when the run was never seen running.
 */
export interface LocalSettled {
  workflowId: string;
  status: string;
  settledAtMs: number;
  startedAtMs: number | null;
}

export interface OpsState {
  /** workflowId -> live executions currently Running/Pending (with per-exec status). */
  runningExecsByWorkflow: Record<string, LiveExec[]>;
  /** workflowId -> status string of the most recent terminal transition seen live / from snapshot. */
  liveStatusByWorkflow: Record<string, string>;
  /** executionId -> terminal-at-ms tombstone. Prevents a stale snapshot / late active event from
   *  resurrecting a terminated run. GUIDs are never reused, so a tombstoned id is gone for good. */
  terminalTombstones: Record<string, number>;
  /** Newest-first live event feed (capped). */
  tickerEvents: TickerEvent[];
  /** executionId -> locally-settled overlay entry (TTL-pruned, superseded by snapshot recent[]). */
  locallySettled: Record<string, LocalSettled>;

  /**
   * Race-safe reconcile against an authoritative snapshot.
   * @param running        snapshot's running[] (only Running|Pending entries).
   * @param lastStatusByWf snapshot's per-workflow lastStatus (from nodes; only Succeeded|Failed|
   *                        Cancelled|null). Used to reconcile the terminal overlay: for workflows
   *                        with no active live execs, the snapshot's lastStatus wins and replaces
   *                        any stale live terminal status. Entries for workflows no longer in the
   *                        snapshot (RBAC/scope change) are cleared.
   * @param recentIds      executionIds present in the snapshot's recent[] — locally-settled
   *                        entries for those are dropped (the snapshot's CompletedAt is
   *                        authoritative).
   */
  seedRunning: (
    running: OpsRunningExecution[],
    lastStatusByWf: Record<string, string | null>,
    recentIds: Set<string>,
  ) => void;
  applyStatus: (executionId: string, workflowId: string, status: string) => void;
  reset: () => void;
}

function pruneTombstones(tombs: Record<string, number>): Record<string, number> {
  const now = Date.now();
  let changed = false;
  const out: Record<string, number> = {};
  for (const [id, at] of Object.entries(tombs)) {
    if (now - at < TOMBSTONE_TTL_MS) out[id] = at;
    else changed = true;
  }
  return changed ? out : tombs;
}

function pruneSettled(
  settled: Record<string, LocalSettled>,
  recentIds?: Set<string>,
): Record<string, LocalSettled> {
  const now = Date.now();
  let changed = false;
  const out: Record<string, LocalSettled> = {};
  for (const [id, s] of Object.entries(settled)) {
    if (now - s.settledAtMs >= LOCAL_SETTLED_TTL_MS || recentIds?.has(id)) {
      changed = true;
      continue;
    }
    out[id] = s;
  }
  return changed ? out : settled;
}

function pushTicker(events: TickerEvent[], evt: TickerEvent): TickerEvent[] {
  // Dedupe repeated deliveries of the same transition (at-least-once SignalR semantics).
  const newest = events[0];
  if (newest && newest.executionId === evt.executionId && newest.status === evt.status) return events;
  return [evt, ...events].slice(0, TICKER_CAP);
}

export const useOperationsStore = create<OpsState>((set) => ({
  runningExecsByWorkflow: {},
  liveStatusByWorkflow: {},
  terminalTombstones: {},
  tickerEvents: [],
  locallySettled: {},

  seedRunning: (running, lastStatusByWf, recentIds) =>
    set((state) => {
      const tombs = pruneTombstones(state.terminalTombstones);

      // Rebuild live executions from the authoritative snapshot, skipping tombstoned ids so a
      // stale snapshot cannot resurrect a run that already terminated via SignalR.
      const byWf: Record<string, LiveExec[]> = {};
      for (const r of running) {
        if (tombs[r.executionId] !== undefined) continue;
        (byWf[r.workflowId] ??= []).push({
          id: r.executionId,
          status: r.status,
          startedAtMs: Date.parse(r.startedAt),
        });
      }

      // Reconcile the terminal overlay from the snapshot. For workflows with no active live execs,
      // the snapshot's lastStatus is authoritative (replaces a stale live terminal status). For
      // workflows still active, leave the overlay — effectiveStatus will report Running/Pending.
      // Workflows that dropped out of the snapshot entirely are cleared.
      const liveStatus: Record<string, string> = {};
      for (const [wfId, last] of Object.entries(lastStatusByWf)) {
        if ((byWf[wfId]?.length ?? 0) > 0) {
          // Preserve any live terminal status already recorded for this workflow.
          if (state.liveStatusByWorkflow[wfId] !== undefined) liveStatus[wfId] = state.liveStatusByWorkflow[wfId];
          continue;
        }
        if (last !== null) liveStatus[wfId] = last;
      }

      return {
        runningExecsByWorkflow: byWf,
        liveStatusByWorkflow: liveStatus,
        terminalTombstones: tombs,
        locallySettled: pruneSettled(state.locallySettled, recentIds),
      };
    }),

  applyStatus: (executionId, workflowId, status) =>
    set((state) => {
      const tombs = pruneTombstones(state.terminalTombstones);
      const now = Date.now();

      if (TERMINAL.has(status)) {
        // Terminal: tombstone the id, drop it from running, record the terminal overlay and
        // the locally-settled bar (so the timeline keeps drawing it until the snapshot lands).
        const cur = state.runningExecsByWorkflow[workflowId] ?? [];
        const existing = cur.find((e) => e.id === executionId);
        const next = cur.filter((e) => e.id !== executionId);
        const runningExecsByWorkflow = next.length === cur.length
          ? state.runningExecsByWorkflow
          : { ...state.runningExecsByWorkflow, [workflowId]: next };
        return {
          runningExecsByWorkflow,
          liveStatusByWorkflow: { ...state.liveStatusByWorkflow, [workflowId]: status },
          terminalTombstones: { ...tombs, [executionId]: now },
          tickerEvents: pushTicker(state.tickerEvents, { executionId, workflowId, status, atMs: now }),
          locallySettled: {
            ...pruneSettled(state.locallySettled),
            [executionId]: {
              workflowId,
              status,
              settledAtMs: now,
              startedAtMs: existing?.startedAtMs ?? null,
            },
          },
        };
      }

      if (ACTIVE.has(status)) {
        // Late active event for an already-terminated run: do NOT resurrect.
        if (tombs[executionId] !== undefined) return { terminalTombstones: tombs };
        const cur = state.runningExecsByWorkflow[workflowId] ?? [];
        const existing = cur.find((e) => e.id === executionId);
        const next = existing
          ? cur.map((e) => (e.id === executionId ? { ...e, status } : e))
          : [...cur, { id: executionId, status, startedAtMs: now }];
        return {
          runningExecsByWorkflow: { ...state.runningExecsByWorkflow, [workflowId]: next },
          terminalTombstones: tombs,
          tickerEvents: pushTicker(state.tickerEvents, { executionId, workflowId, status, atMs: now }),
        };
      }

      // Unrecognized status — only prune tombstones.
      return { terminalTombstones: tombs };
    }),

  reset: () => set({
    runningExecsByWorkflow: {},
    liveStatusByWorkflow: {},
    terminalTombstones: {},
    tickerEvents: [],
    locallySettled: {},
  }),
}));

/**
 * Pending-sensitive effective status cascade. Order matters:
 *   any Running -> 'Running'; else any Pending -> 'Pending'; else live terminal overlay; else
 *   the snapshot's lastStatus (passed in by the caller from node data); else null.
 */
export function effectiveStatusFor(
  running: LiveExec[] | undefined,
  liveStatus: string | undefined,
  lastStatus: string | null | undefined,
): string | null {
  if (running?.some((e) => e.status === 'Running')) return 'Running';
  if (running?.some((e) => e.status === 'Pending')) return 'Pending';
  if (liveStatus) return liveStatus;
  return lastStatus ?? null;
}

/** Count live executions with `status` (Running|Pending) across the folder-scoped workflows. */
export function countScoped(
  runningExecsByWorkflow: Record<string, LiveExec[]>,
  scopedWorkflowIds: Set<string>,
  status: string,
): number {
  let n = 0;
  for (const wfId of scopedWorkflowIds) {
    const list = runningExecsByWorkflow[wfId];
    if (list) n += list.filter((e) => e.status === status).length;
  }
  return n;
}
