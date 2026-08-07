import { describe, it, expect, beforeEach } from 'vitest';
import {
  useDbHealthStore,
  reportDatabaseOutageSuspected,
  resetDbHealth,
} from '../../stores/dbHealthStore';

/**
 * The store's load-bearing rule is the two-authority split: `status` is written only by probe
 * results, while a 503 seen by ordinary requests merely raises a suspicion that accelerates the
 * poll. These tests pin that split — every failure mode here ends with either a stuck banner, a
 * false banner, or a permanently latched fast poll.
 */
describe('dbHealthStore', () => {
  beforeEach(() => resetDbHealth());

  it('report503_setsSuspectedOnly_doesNotFlipStatus', () => {
    // One slow query produces a DATABASE_TIMEOUT without the database being gone. If suspicion
    // could flip the status, every slow report query would raise the outage banner.
    reportDatabaseOutageSuspected();

    const s = useDbHealthStore.getState();
    expect(s.status).toBe('unknown');
    expect(s.suspectedAt).not.toBeNull();
  });

  it('reportSuspected_keepsTheFirstTimestamp', () => {
    reportDatabaseOutageSuspected();
    const first = useDbHealthStore.getState().suspectedAt;
    reportDatabaseOutageSuspected();
    expect(useDbHealthStore.getState().suspectedAt).toBe(first);
  });

  it('reportProbeResult_adjudicatesAndClearsSuspicion', () => {
    reportDatabaseOutageSuspected();

    useDbHealthStore.getState().reportProbeResult({ status: 'ok', sinceUtc: null, reason: null });

    const s = useDbHealthStore.getState();
    expect(s.status).toBe('ok');
    // Without this, the fast poll cadence stays latched for the rest of the session after a
    // single slow query.
    expect(s.suspectedAt).toBeNull();
  });

  it('reportProbeResult_unavailable_carriesSinceAndReason', () => {
    useDbHealthStore.getState().reportProbeResult({
      status: 'unavailable',
      sinceUtc: '2026-08-07T07:00:00Z',
      reason: 'RejectedByServer',
    });

    const s = useDbHealthStore.getState();
    expect(s.status).toBe('unavailable');
    expect(s.reason).toBe('RejectedByServer');
    expect(s.sinceUtc).toBe('2026-08-07T07:00:00Z');
  });

  it('reportProbeFailed_clearsSuspectedAt', () => {
    // A dead process cannot adjudicate anything; keeping the flag would pin `fast` forever once
    // the process returns.
    reportDatabaseOutageSuspected();

    useDbHealthStore.getState().reportProbeFailed();

    const s = useDbHealthStore.getState();
    expect(s.status).toBe('offline');
    expect(s.suspectedAt).toBeNull();
  });

  it('reportProbeResult_unknownServerStatus_mapsToUnknown', () => {
    // A future server enum value must degrade to "unknown", not crash or masquerade as ok.
    useDbHealthStore.getState().reportProbeResult({ status: 'somethingNew', sinceUtc: null, reason: null });
    expect(useDbHealthStore.getState().status).toBe('unknown');
  });
});
