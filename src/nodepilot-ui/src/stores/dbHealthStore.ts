import { create } from 'zustand';

/**
 * What the SPA believes about the backend's database: the four backend states plus one only the
 * client can see. `offline` means the probe request itself failed, so the NodePilot process is
 * unreachable, which is a different problem from a running process with a missing database.
 */
export type DbHealthStatus = 'unknown' | 'ok' | 'armed' | 'unavailable' | 'offline';

export interface DbHealthState {
  status: DbHealthStatus;
  /** Server-reported outage start (ISO string); null unless status is 'unavailable'. */
  sinceUtc: string | null;
  /** Server-reported coarse cause (Unreachable | RejectedByServer | Wedged | Unknown). */
  reason: string | null;
  /**
   * Set when any request observed a DATABASE_* 503; cleared by the next probe result either way.
   * Only accelerates the poll — it never flips `status`.
   */
  suspectedAt: number | null;

  reportProbeResult: (dto: { status: string; sinceUtc: string | null; reason: string | null }) => void;
  reportProbeFailed: () => void;
  reportSuspected: () => void;
}

/**
 * Two authorities write this store:
 *
 * - `status` comes only from the health probe (`reportProbeResult` / `reportProbeFailed`). The
 *   probe asks `/healthz/database`, which answers from the backend breaker's memory.
 * - A 503 seen by any ordinary request only sets `suspectedAt`, which switches the probe from its
 *   idle cadence to the fast one. It never raises the banner on its own, because a single slow
 *   query yields a DATABASE_TIMEOUT while the database is still there.
 */
export const useDbHealthStore = create<DbHealthState>()((set) => ({
  status: 'unknown',
  sinceUtc: null,
  reason: null,
  suspectedAt: null,

  reportProbeResult: (dto) =>
    set({
      status: dto.status === 'ok' || dto.status === 'armed' || dto.status === 'unavailable'
        ? dto.status
        : 'unknown',
      sinceUtc: dto.sinceUtc,
      reason: dto.reason,
      // The probe answered, so the suspicion is settled either way. Leaving it set would pin
      // the fast poll cadence forever.
      suspectedAt: null,
    }),

  reportProbeFailed: () =>
    set({
      status: 'offline',
      sinceUtc: null,
      reason: null,
      // Cleared here as well: an unreachable process settles nothing, and keeping the flag would
      // latch the fast cadence for the rest of the session once the process returns.
      suspectedAt: null,
    }),

  reportSuspected: () => set((s) => ({ suspectedAt: s.suspectedAt ?? Date.now() })),
}));

/**
 * Module-level entry for non-React callers such as the api client. The name describes the intent
 * at the call site rather than the store plumbing behind it.
 */
export function reportDatabaseOutageSuspected(): void {
  useDbHealthStore.getState().reportSuspected();
}

/** Test seam: zustand module state leaks across vitest files without an explicit reset. */
export function resetDbHealth(): void {
  useDbHealthStore.setState({
    status: 'unknown',
    sinceUtc: null,
    reason: null,
    suspectedAt: null,
  });
}
