import { describe, it, expect } from 'vitest';
import { derivePulseState, type PulseInput } from '../../lib/opsPulse';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function input(overrides: Partial<PulseInput> = {}): PulseInput {
  return {
    nowMs: NOW,
    recent: [],
    heartbeats: [],
    machinesTotal: 0,
    machinesReachable: 0,
    longRunningCount: 0,
    ...overrides,
  };
}

function failures(count: number, ageMin: number) {
  return Array.from({ length: count }, () => ({ status: 'Failed', completedAtMs: NOW - ageMin * MIN }));
}

describe('derivePulseState', () => {
  it('all clear → nominal with no reasons', () => {
    const r = derivePulseState(input({ machinesTotal: 3, machinesReachable: 3, heartbeats: [{ isStale: false }] }));
    expect(r).toEqual({ state: 'nominal', reasons: [] });
  });

  it('one recent failure → degraded with recentFailure', () => {
    const r = derivePulseState(input({ recent: failures(1, 12) }));
    expect(r.state).toBe('degraded');
    expect(r.reasons).toEqual(['recentFailure']);
  });

  it('failures older than 15 min do not degrade', () => {
    const r = derivePulseState(input({ recent: failures(3, 20) }));
    expect(r.state).toBe('nominal');
  });

  it('succeeded runs never degrade', () => {
    const r = derivePulseState(input({ recent: [{ status: 'Succeeded', completedAtMs: NOW - MIN }] }));
    expect(r.state).toBe('nominal');
  });

  it('5 failures within 10 min → incident with failureSpike', () => {
    const r = derivePulseState(input({ recent: failures(5, 5) }));
    expect(r.state).toBe('incident');
    expect(r.reasons).toContain('failureSpike');
  });

  it('4 failures within 10 min stay degraded (boundary)', () => {
    const r = derivePulseState(input({ recent: failures(4, 5) }));
    expect(r.state).toBe('degraded');
    expect(r.reasons).toEqual(['recentFailure']);
  });

  it('one stale heartbeat → degraded', () => {
    const r = derivePulseState(input({ heartbeats: [{ isStale: true }, { isStale: false }] }));
    expect(r.state).toBe('degraded');
    expect(r.reasons).toEqual(['staleHeartbeat']);
  });

  it('all heartbeats stale → incident', () => {
    const r = derivePulseState(input({ heartbeats: [{ isStale: true }, { isStale: true }] }));
    expect(r.state).toBe('incident');
    expect(r.reasons).toContain('allHeartbeatsStale');
  });

  it('no heartbeats at all is not an incident', () => {
    const r = derivePulseState(input({ heartbeats: [] }));
    expect(r.state).toBe('nominal');
  });

  it('some machines unreachable → degraded; all unreachable → incident; 0/0 → no reason', () => {
    expect(derivePulseState(input({ machinesTotal: 5, machinesReachable: 3 }))).toEqual({
      state: 'degraded', reasons: ['machinesUnreachable'],
    });
    const down = derivePulseState(input({ machinesTotal: 5, machinesReachable: 0 }));
    expect(down.state).toBe('incident');
    expect(down.reasons).toContain('allMachinesUnreachable');
    expect(derivePulseState(input({ machinesTotal: 0, machinesReachable: 0 })).state).toBe('nominal');
  });

  it('long-running executions → degraded', () => {
    const r = derivePulseState(input({ longRunningCount: 2 }));
    expect(r.state).toBe('degraded');
    expect(r.reasons).toEqual(['longRunning']);
  });

  it('incident accumulates degraded-grade reasons for the health rail', () => {
    const r = derivePulseState(input({
      recent: failures(6, 5),
      heartbeats: [{ isStale: true }, { isStale: false }],
      machinesTotal: 3,
      machinesReachable: 2,
      longRunningCount: 1,
    }));
    expect(r.state).toBe('incident');
    expect(r.reasons).toEqual(expect.arrayContaining(['failureSpike', 'staleHeartbeat', 'machinesUnreachable', 'longRunning']));
  });
});
