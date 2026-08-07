import { create } from 'zustand';

/**
 * What the SPA currently believes about the backend's database, mirroring the four backend states
 * plus the one only the client can see: `offline` means the PROBE REQUEST itself failed, i.e. the
 * NodePilot process is unreachable — a different problem than "process fine, database gone".
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
 * Two-authority model, and the split is the design:
 *
 * - `status` is written ONLY by the health probe's results (`reportProbeResult` /
 *   `reportProbeFailed`). The probe asks `/healthz/database`, which answers from the backend
 *   breaker's memory — a positive source of truth.
 * - A 503 seen by any ordinary request merely sets `suspectedAt`, which switches the probe from
 *   its idle cadence to the fast one. It never raises the banner by itself: a single slow report
 *   query produces a DATABASE_TIMEOUT without the database being gone, and a banner that cries
 *   wolf on every slow query would be ignored by the time it mattered.
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
      // The probe answered — whatever raised the suspicion is adjudicated now, in either
      // direction. Leaving it set would pin the fast poll cadence forever.
      suspectedAt: null,
    }),

  reportProbeFailed: () =>
    set({
      status: 'offline',
      sinceUtc: null,
      reason: null,
      // Must clear here too: a dead process cannot adjudicate anything, and keeping the flag
      // would leave `fast` latched for the rest of the session once the process returns.
      suspectedAt: null,
    }),

  reportSuspected: () => set((s) => ({ suspectedAt: s.suspectedAt ?? Date.now() })),
}));

/**
 * Module-level entry for non-React callers (the api client). Named so the import reads as what it
 * does at the call site rather than as store plumbing.
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
