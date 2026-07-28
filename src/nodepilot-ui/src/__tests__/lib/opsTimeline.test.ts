import { describe, it, expect } from 'vitest';
import {
  windowFor, timeToX, buildTimelineBars, assignLanes, placeBar, axisTicks, isActiveBarStatus,
  pairCallConnectors, isOverdue, isStalled, tickStepFor, OPS_WINDOW_MS, OPS_NOW_FRACTION,
  type TimelineBarInput,
} from '../../lib/opsTimeline';
import type { OpsNode } from '../../types/api';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;
const W = windowFor(NOW, OPS_WINDOW_MS, 1000);

function iso(ms: number): string {
  return new Date(ms).toISOString();
}

function node(workflowId: string, name: string, folderPath = '/'): OpsNode {
  return { workflowId, name, folderId: 'f1', folderPath, isEnabled: true, runningCount: 0, lastStatus: null, callFrequency: null, canRun: true, canEdit: true };
}

describe('windowFor / timeToX', () => {
  it('places NOW at the configured fraction of the track', () => {
    expect(timeToX(NOW, W)).toBeCloseTo(1000 * OPS_NOW_FRACTION, 5);
  });

  it('places the window start at x=0 and maps linearly', () => {
    expect(timeToX(W.startMs, W)).toBe(0);
    const quarter = W.startMs + (NOW - W.startMs) / 4;
    expect(timeToX(quarter, W)).toBeCloseTo((1000 * OPS_NOW_FRACTION) / 4, 5);
  });

  it('returns 0 for a zero-width track', () => {
    const w0 = windowFor(NOW, OPS_WINDOW_MS, 0);
    expect(timeToX(NOW, w0)).toBe(0);
  });
});

describe('buildTimelineBars', () => {
  const scope = new Set(['w1', 'w2']);

  it('merges running + recent, snapshot recent beats locallySettled per executionId', () => {
    const bars = buildTimelineBars(
      [{ executionId: 'run1', workflowId: 'w1', status: 'Running', startedAt: iso(NOW - 4 * MIN), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      [{ executionId: 'done1', workflowId: 'w1', status: 'Failed', startedAt: iso(NOW - 10 * MIN), completedAt: iso(NOW - 8 * MIN), parentExecutionId: null }],
      { done1: { workflowId: 'w1', status: 'Failed', settledAtMs: NOW - 7 * MIN, startedAtMs: NOW - 10 * MIN } },
      W, scope,
    );
    expect(bars).toHaveLength(2);
    const done = bars.find((b) => b.executionId === 'done1')!;
    expect(done.completedAtMs).toBe(NOW - 8 * MIN); // snapshot CompletedAt wins over settledAtMs
    const run = bars.find((b) => b.executionId === 'run1')!;
    expect(run.completedAtMs).toBeNull();
  });

  it('renders locally-settled runs not yet present in recent', () => {
    const bars = buildTimelineBars(
      [], [],
      { fresh: { workflowId: 'w1', status: 'Succeeded', settledAtMs: NOW - 1000, startedAtMs: NOW - 2 * MIN } },
      W, scope,
    );
    expect(bars).toHaveLength(1);
    expect(bars[0].completedAtMs).toBe(NOW - 1000);
  });

  it('skips locally-settled entries whose start was never observed', () => {
    const bars = buildTimelineBars(
      [], [],
      { ghost: { workflowId: 'w1', status: 'Succeeded', settledAtMs: NOW, startedAtMs: null } },
      W, scope,
    );
    expect(bars).toHaveLength(0);
  });

  it('drops bars fully left of the window and out-of-scope workflows', () => {
    const bars = buildTimelineBars(
      [{ executionId: 'r-out', workflowId: 'other', status: 'Running', startedAt: iso(NOW - MIN), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      [
        { executionId: 'old', workflowId: 'w1', status: 'Succeeded', startedAt: iso(NOW - 60 * MIN), completedAt: iso(NOW - 40 * MIN), parentExecutionId: null },
        { executionId: 'in', workflowId: 'w1', status: 'Succeeded', startedAt: iso(NOW - 6 * MIN), completedAt: iso(NOW - 5 * MIN), parentExecutionId: null },
      ],
      {}, W, scope,
    );
    expect(bars.map((b) => b.executionId)).toEqual(['in']);
  });
});

describe('assignLanes', () => {
  const nodes = new Map<string, OpsNode>([
    ['w1', node('w1', 'Alpha', '/ops')],
    ['w2', node('w2', 'Beta')],
    ['w3', node('w3', 'Gamma')],
  ]);

  function bar(executionId: string, workflowId: string, startOffsetMin: number, endOffsetMin: number | null, status = 'Succeeded'): TimelineBarInput {
    return {
      executionId, workflowId, status: endOffsetMin === null ? 'Running' : status,
      startedAtMs: NOW - startOffsetMin * MIN,
      completedAtMs: endOffsetMin === null ? null : NOW - endOffsetMin * MIN,
      parentExecutionId: null,
      stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null,
    };
  }

  it('active lanes come first (latest start first), then settled by latest completion', () => {
    const { lanes } = assignLanes([
      bar('a', 'w1', 10, 8),            // settled, completed -8m
      bar('b', 'w2', 3, null),          // active, started -3m
      bar('c', 'w3', 5, null),          // active, started -5m
    ], nodes);
    expect(lanes.map((l) => l.workflowId)).toEqual(['w2', 'w3', 'w1']);
    expect(lanes[0].hasActive).toBe(true);
    expect(lanes[2].hasActive).toBe(false);
    expect(lanes[0].name).toBe('Beta');
    expect(lanes[2].folderPath).toBe('/ops');
  });

  it('overlapping executions in a lane stack into sub-rows; sequential ones share sub-row 0', () => {
    const { lanes, placed } = assignLanes([
      bar('a', 'w1', 15, 10),  // -15..-10
      bar('b', 'w1', 12, 5),   // -12..-5 overlaps a â†’ sub-row 1
      bar('c', 'w1', 9, 7),    // -9..-7 fits after a in sub-row 0
    ], nodes);
    expect(lanes[0].subRowCount).toBe(2);
    const byId = Object.fromEntries(placed.map((p) => [p.executionId, p]));
    expect(byId.a.subRow).toBe(0);
    expect(byId.b.subRow).toBe(1);
    expect(byId.c.subRow).toBe(0);
  });

  it('a running bar blocks its sub-row indefinitely', () => {
    const { lanes, placed } = assignLanes([
      bar('a', 'w1', 10, null),  // running since -10m
      bar('b', 'w1', 5, 3),      // would fit after a if a had ended â€” it hasn't
    ], nodes);
    expect(lanes[0].subRowCount).toBe(2);
    expect(placed.find((p) => p.executionId === 'b')!.subRow).toBe(1);
  });

  it('is deterministic: ties broken by workflowId', () => {
    const t = NOW - 4 * MIN;
    const a: TimelineBarInput = { executionId: 'x', workflowId: 'w2', status: 'Succeeded', startedAtMs: t, completedAtMs: NOW - MIN, parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null };
    const b: TimelineBarInput = { executionId: 'y', workflowId: 'w1', status: 'Succeeded', startedAtMs: t, completedAtMs: NOW - MIN, parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null };
    const { lanes } = assignLanes([a, b], nodes);
    expect(lanes.map((l) => l.workflowId)).toEqual(['w1', 'w2']);
  });

  it('falls back to the workflowId when the node is unknown', () => {
    const { lanes } = assignLanes([bar('a', 'wX', 5, 2)], nodes);
    expect(lanes[0].name).toBe('wX');
    expect(lanes[0].folderPath).toBe('/');
  });

  it('indents sub-workflow lanes directly under their caller lane', () => {
    const parentBar = { ...bar('p1', 'w1', 6, null) };
    const childBar = { ...bar('c1', 'w2', 3, null), parentExecutionId: 'p1' };
    const otherBar = { ...bar('o1', 'w3', 1, null) }; // latest start — would sort first without hierarchy
    const { lanes } = assignLanes([parentBar, childBar, otherBar], nodes);
    expect(lanes.map((l) => l.workflowId)).toEqual(['w3', 'w1', 'w2']); // child glued under its caller
    expect(lanes.map((l) => l.depth)).toEqual([0, 0, 1]);
  });

  it('keeps a child lane top-level when its parent run is not visible', () => {
    const orphan = { ...bar('c1', 'w2', 3, null), parentExecutionId: 'gone' };
    const { lanes } = assignLanes([orphan], nodes);
    expect(lanes[0].depth).toBe(0);
  });
});

describe('placeBar', () => {
  it('clamps bars starting before the window and flags them clipped', () => {
    const placed = placeBar(
      { executionId: 'a', workflowId: 'w1', status: 'Succeeded', startedAtMs: NOW - 40 * MIN, completedAtMs: NOW - 5 * MIN, parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null, laneIndex: 0, subRow: 0 },
      W,
    );
    expect(placed.clippedLeft).toBe(true);
    expect(placed.leftPx).toBe(0);
    expect(placed.widthPx).toBeCloseTo(timeToX(NOW - 5 * MIN, W), 5);
  });

  it('running bars extend to NOW', () => {
    const placed = placeBar(
      { executionId: 'a', workflowId: 'w1', status: 'Running', startedAtMs: NOW - 5 * MIN, completedAtMs: null, parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null, laneIndex: 0, subRow: 0 },
      W,
    );
    expect(placed.clippedLeft).toBe(false);
    expect(placed.leftPx + placed.widthPx).toBeCloseTo(timeToX(NOW, W), 5);
  });
});

describe('axisTicks', () => {
  it('aligns ticks to wall-clock 5-minute multiples within [start..now]', () => {
    const ticks = axisTicks(W);
    expect(ticks.length).toBeGreaterThanOrEqual(4);
    for (const t of ticks) {
      expect(t.atMs % (5 * MIN)).toBe(0);
      expect(t.atMs).toBeGreaterThanOrEqual(W.startMs);
      expect(t.atMs).toBeLessThanOrEqual(NOW);
      expect(t.xPx).toBeGreaterThanOrEqual(0);
    }
  });
});

describe('pairCallConnectors', () => {
  function placed(executionId: string, parentExecutionId: string | null): Parameters<typeof pairCallConnectors>[0][number] {
    return {
      executionId, workflowId: 'w1', status: 'Running', parentExecutionId,
      startedAtMs: NOW - MIN, completedAtMs: null,
      stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null,
      leftPx: 10, widthPx: 20, clippedLeft: false, laneIndex: 0, subRow: 0,
    };
  }

  it('pairs children with their visible parent bar', () => {
    const parent = placed('p1', null);
    const child = placed('c1', 'p1');
    const pairs = pairCallConnectors([parent, child]);
    expect(pairs).toHaveLength(1);
    expect(pairs[0].parent.executionId).toBe('p1');
    expect(pairs[0].child.executionId).toBe('c1');
  });

  it('skips children whose parent is not visible and top-level runs', () => {
    const pairs = pairCallConnectors([placed('a', null), placed('orphan', 'gone')]);
    expect(pairs).toHaveLength(0);
  });
});

describe('isActiveBarStatus', () => {
  it('treats Running/Pending/Paused as active, terminal states as settled', () => {
    expect(isActiveBarStatus('Running')).toBe(true);
    expect(isActiveBarStatus('Pending')).toBe(true);
    expect(isActiveBarStatus('Paused')).toBe(true);
    expect(isActiveBarStatus('Succeeded')).toBe(false);
    expect(isActiveBarStatus('Failed')).toBe(false);
  });
});

describe('isOverdue', () => {
  const OVERDUE_MS = 10 * MIN;
  const bar = (p: Partial<TimelineBarInput> = {}) => ({
    status: 'Running', startedAtMs: NOW - 30 * MIN, completedAtMs: null,
    stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null, ...p,
  });

  it('flags a Running bar older than the threshold', () => {
    expect(isOverdue(bar(), NOW, OVERDUE_MS)).toBe(true);
  });

  it('is inclusive exactly at the threshold and false just below it', () => {
    expect(isOverdue(bar({ startedAtMs: NOW - OVERDUE_MS }), NOW, OVERDUE_MS)).toBe(true);
    expect(isOverdue(bar({ startedAtMs: NOW - OVERDUE_MS + 1 }), NOW, OVERDUE_MS)).toBe(false);
  });

  it('never flags a settled bar, however old', () => {
    expect(isOverdue(bar({ completedAtMs: NOW - MIN }), NOW, OVERDUE_MS)).toBe(false);
  });

  // The threshold belongs to the alerting collector, which looks at Running alone. Pending
  // (queued, never started) and Paused (breakpoint) are different conditions entirely.
  it.each(['Pending', 'Paused'])('does not flag %s — the alerting threshold is Running-only', (status) => {
    expect(isOverdue(bar({ status }), NOW, OVERDUE_MS)).toBe(false);
  });
});

describe('tickStepFor', () => {
  it('keeps roughly four axis labels at every selectable window', () => {
    expect(tickStepFor(20 * MIN)).toBe(5 * MIN);
    expect(tickStepFor(60 * MIN)).toBe(15 * MIN);
    expect(tickStepFor(240 * MIN)).toBe(60 * MIN);
  });

  it('switches step at the boundaries, not inside them', () => {
    expect(tickStepFor(20 * MIN + 1)).toBe(15 * MIN);
    expect(tickStepFor(60 * MIN + 1)).toBe(60 * MIN);
  });
});

describe('isStalled', () => {
  const STALL_MS = 10 * MIN;
  const bar = (p: Record<string, unknown> = {}) => ({
    status: 'Running', completedAtMs: null, lastProgressAtMs: NOW - 30 * MIN, ...p,
  } as Parameters<typeof isStalled>[0]);

  it('flags a run whose last step finished longer ago than the threshold', () => {
    expect(isStalled(bar(), NOW, STALL_MS)).toBe(true);
  });

  it('does not flag a run that is still completing steps', () => {
    expect(isStalled(bar({ lastProgressAtMs: NOW - MIN }), NOW, STALL_MS)).toBe(false);
  });

  // Unknown must never render as stalled: not-enriched runs and freshly started ones both
  // carry null, and "we did not look" is not "it is stuck".
  it('does not flag a run with no activity data', () => {
    expect(isStalled(bar({ lastProgressAtMs: null }), NOW, STALL_MS)).toBe(false);
  });

  it('never flags a settled run', () => {
    expect(isStalled(bar({ completedAtMs: NOW - MIN }), NOW, STALL_MS)).toBe(false);
  });
});

describe('buildTimelineBars — activity fields', () => {
  it('carries activity for running bars and nulls it for settled ones', () => {
    const bars = buildTimelineBars(
      [{
        executionId: 'r1', workflowId: 'w1', status: 'Running',
        startedAt: iso(NOW - 5 * MIN), parentExecutionId: null,
        stepsFinished: 4, lastCompletedStepName: 'Copy files',
        lastProgressAt: iso(NOW - 2 * MIN), activeStepCount: 0,
      }],
      [{
        executionId: 'd1', workflowId: 'w1', status: 'Succeeded',
        startedAt: iso(NOW - 9 * MIN), completedAt: iso(NOW - 8 * MIN), parentExecutionId: null,
      }],
      {}, W, new Set(['w1']),
    );
    const running = bars.find((b) => b.executionId === 'r1')!;
    expect(running.stepsFinished).toBe(4);
    expect(running.lastCompletedStepName).toBe('Copy files');
    expect(running.lastProgressAtMs).toBe(NOW - 2 * MIN);

    const settled = bars.find((b) => b.executionId === 'd1')!;
    expect(settled.stepsFinished).toBeNull();
    expect(settled.lastProgressAtMs).toBeNull();
  });
});
