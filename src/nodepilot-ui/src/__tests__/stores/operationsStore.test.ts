import { describe, it, expect, beforeEach } from 'vitest';
import {
  useOperationsStore, effectiveStatusFor,
} from '../../stores/operationsStore';

beforeEach(() => useOperationsStore.getState().reset());

const NO_RECENT = new Set<string>();

function runningCount(workflowId: string): number | undefined {
  const list = useOperationsStore.getState().runningExecsByWorkflow[workflowId];
  return list ? list.length : undefined;
}

describe('operationsStore', () => {
  it('seedRunning groups execution ids by workflow and parses startedAt', () => {
    useOperationsStore.getState().seedRunning(
      [
        { executionId: 'e1', workflowId: 'w1', status: 'Running', parentExecutionId: null, startedAt: '2026-07-19T10:00:00Z' },
        { executionId: 'e2', workflowId: 'w1', status: 'Pending', parentExecutionId: null, startedAt: '2026-07-19T10:01:00Z' },
        { executionId: 'e3', workflowId: 'w2', status: 'Running', parentExecutionId: null, startedAt: '2026-07-19T10:02:00Z' },
      ],
      { w1: null, w2: null },
      NO_RECENT,
    );
    const s = useOperationsStore.getState();
    expect(runningCount('w1')).toBe(2);
    expect(runningCount('w2')).toBe(1);
    expect(s.runningExecsByWorkflow.w1[0].startedAtMs).toBe(Date.parse('2026-07-19T10:00:00Z'));
  });

  it('applyStatus adds a running execution and removes it on terminal status', () => {
    const { applyStatus } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    expect(runningCount('w1')).toBe(1);

    applyStatus('e1', 'w1', 'Succeeded');
    expect(runningCount('w1')).toBe(0);
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBe('Succeeded');
  });

  it('applyStatus is idempotent for duplicate running events and updates per-exec status', () => {
    const { applyStatus } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Pending');
    const list = useOperationsStore.getState().runningExecsByWorkflow.w1;
    expect(list).toHaveLength(1);
    expect(list[0].id).toBe('e1');
    expect(list[0].status).toBe('Pending');
  });

  it('applyStatus records last status even for an untracked execution', () => {
    useOperationsStore.getState().applyStatus('ghost', 'w1', 'Failed');
    expect(runningCount('w1')).toBeUndefined();
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBe('Failed');
  });

  // --- race-safe reconciliation (R2/R3 Codex findings) ---

  it('seedRunning skips tombstoned execution ids so a stale snapshot cannot resurrect a run', () => {
    const { applyStatus, seedRunning } = useOperationsStore.getState();
    // Run e1 terminates via SignalR â†’ tombstoned + removed from running.
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Failed');
    expect(runningCount('w1')).toBe(0);

    // A stale snapshot still lists e1 as running. seedRunning must NOT resurrect it.
    seedRunning(
      [{ executionId: 'e1', workflowId: 'w1', status: 'Running', parentExecutionId: null, startedAt: '' }],
      { w1: 'Failed' },
      NO_RECENT,
    );
    expect(runningCount('w1')).toBeUndefined();
  });

  it('applyStatus does not resurrect a tombstoned execution via a late active event', () => {
    const { applyStatus } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Succeeded');
    // A delayed Running event arrives after the terminal â€” must be ignored.
    applyStatus('e1', 'w1', 'Running');
    expect(runningCount('w1')).toBe(0);
  });

  it('seedRunning reconciles liveStatusByWorkflow from the authoritative snapshot lastStatus', () => {
    const { applyStatus, seedRunning } = useOperationsStore.getState();
    // Run A for w1 ended Succeeded (live overlay = Succeeded). Run B starts, its terminal Failed
    // event is missed; the next snapshot removes B from running[] and reports lastStatus=Failed.
    applyStatus('a', 'w1', 'Running');
    applyStatus('a', 'w1', 'Succeeded');
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBe('Succeeded');

    seedRunning([], { w1: 'Failed' }, NO_RECENT);
    // With nothing active, the snapshot's lastStatus wins (replaces the stale Succeeded overlay).
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBe('Failed');
  });

  it('seedRunning leaves the live overlay untouched for workflows that are still active', () => {
    const { applyStatus, seedRunning } = useOperationsStore.getState();
    applyStatus('a', 'w1', 'Running');
    applyStatus('a', 'w1', 'Succeeded'); // previous run succeeded (overlay)
    applyStatus('b', 'w1', 'Running');   // current run active
    seedRunning([{ executionId: 'b', workflowId: 'w1', status: 'Running', parentExecutionId: null, startedAt: '' }], { w1: 'Succeeded' }, NO_RECENT);
    // Active â†’ overlay preserved (effectiveStatus reports Running anyway); lastStatus not forced.
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBe('Succeeded');
    expect(runningCount('w1')).toBe(1);
  });

  it('seedRunning clears live overlay for workflows that dropped out of the snapshot', () => {
    const { applyStatus, seedRunning } = useOperationsStore.getState();
    applyStatus('a', 'w1', 'Running');
    applyStatus('a', 'w1', 'Failed');
    // w1 no longer accessible (RBAC change) â€” not in the snapshot at all.
    seedRunning([], {}, NO_RECENT);
    expect(useOperationsStore.getState().liveStatusByWorkflow.w1).toBeUndefined();
  });

  // --- ticker events ---

  it('applyStatus records ticker events newest-first and dedupes repeated deliveries', () => {
    const { applyStatus } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Running'); // duplicate delivery â€” deduped
    applyStatus('e1', 'w1', 'Succeeded');
    const events = useOperationsStore.getState().tickerEvents;
    expect(events).toHaveLength(2);
    expect(events[0].status).toBe('Succeeded');
    expect(events[1].status).toBe('Running');
  });

  it('ticker is capped at 50 entries', () => {
    const { applyStatus } = useOperationsStore.getState();
    for (let n = 0; n < 60; n++) applyStatus(`e${n}`, 'w1', 'Running');
    const events = useOperationsStore.getState().tickerEvents;
    expect(events).toHaveLength(50);
    expect(events[0].executionId).toBe('e59'); // newest first
  });

  it('unrecognized statuses do not produce ticker events', () => {
    useOperationsStore.getState().applyStatus('e1', 'w1', 'SomethingWeird');
    expect(useOperationsStore.getState().tickerEvents).toHaveLength(0);
  });

  // --- locally-settled overlay ---

  it('terminal transition records a locally-settled entry carrying the observed start time', () => {
    const { seedRunning, applyStatus } = useOperationsStore.getState();
    seedRunning(
      [{ executionId: 'e1', workflowId: 'w1', status: 'Running', parentExecutionId: null, startedAt: '2026-07-19T10:00:00Z' }],
      { w1: null },
      NO_RECENT,
    );
    applyStatus('e1', 'w1', 'Failed');
    const settled = useOperationsStore.getState().locallySettled.e1;
    expect(settled.status).toBe('Failed');
    expect(settled.workflowId).toBe('w1');
    expect(settled.startedAtMs).toBe(Date.parse('2026-07-19T10:00:00Z'));
    expect(settled.settledAtMs).toBeGreaterThan(0);
  });

  it('terminal transition for a never-seen run records startedAtMs null', () => {
    useOperationsStore.getState().applyStatus('ghost', 'w1', 'Succeeded');
    expect(useOperationsStore.getState().locallySettled.ghost.startedAtMs).toBeNull();
  });

  it('seedRunning drops locally-settled entries confirmed by the snapshot recent list', () => {
    const { applyStatus, seedRunning } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Succeeded');
    applyStatus('e2', 'w1', 'Running');
    applyStatus('e2', 'w1', 'Failed');
    expect(Object.keys(useOperationsStore.getState().locallySettled)).toHaveLength(2);

    // Snapshot now carries e1 in recent[] â€” its local overlay is superseded; e2 stays.
    seedRunning([], { w1: 'Failed' }, new Set(['e1']));
    const settled = useOperationsStore.getState().locallySettled;
    expect(settled.e1).toBeUndefined();
    expect(settled.e2).toBeDefined();
  });

  it('reset clears ticker and locally-settled state', () => {
    const { applyStatus } = useOperationsStore.getState();
    applyStatus('e1', 'w1', 'Running');
    applyStatus('e1', 'w1', 'Succeeded');
    useOperationsStore.getState().reset();
    const s = useOperationsStore.getState();
    expect(s.tickerEvents).toHaveLength(0);
    expect(s.locallySettled).toEqual({});
    expect(s.runningExecsByWorkflow).toEqual({});
  });

  // --- effectiveStatusFor (Pending-sensitive cascade) ---

  it('effectiveStatusFor prefers Running, then Pending, then live overlay, then lastStatus', () => {
    expect(effectiveStatusFor([{ id: 'a', status: 'Pending', startedAtMs: 0 }, { id: 'b', status: 'Running', startedAtMs: 0 }], 'Succeeded', 'Succeeded')).toBe('Running');
    expect(effectiveStatusFor([{ id: 'a', status: 'Pending', startedAtMs: 0 }], 'Succeeded', 'Succeeded')).toBe('Pending');
    expect(effectiveStatusFor(undefined, 'Failed', 'Succeeded')).toBe('Failed'); // live overlay beats lastStatus
    expect(effectiveStatusFor(undefined, undefined, 'Failed')).toBe('Failed'); // lastStatus fallback
    expect(effectiveStatusFor(undefined, undefined, null)).toBeNull();
  });

  it('effectiveStatusFor does not collapse a pure-Pending workflow to Running', () => {
    expect(effectiveStatusFor([{ id: 'a', status: 'Pending', startedAtMs: 0 }], undefined, 'Succeeded')).toBe('Pending');
  });
});
