import { describe, it, expect } from 'vitest';
import {
  windowFor, timeToX, buildTimelineBars, assignLanes, placeBar, axisTicks, isActiveBarStatus,
  pairCallConnectors, isOverdue, isStalled, tickStepFor, buildDensityCells, densityColumnHeight,
  OPS_WINDOW_MS, OPS_NOW_FRACTION, OPS_WINDOW_MINUTES,
  OPS_DENSITY_CELL_GAP_PX, OPS_DENSITY_MAX_H, OPS_DENSITY_MIN_H, OPS_MAX_SUB_ROWS,
  type TimelineBarInput,
} from '../../lib/opsTimeline';
import { OPS_BAR_H } from '../../components/operations/OpsTimelineBar';
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
      scope,
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
      scope,
    );
    expect(bars).toHaveLength(1);
    expect(bars[0].completedAtMs).toBe(NOW - 1000);
  });

  it('skips locally-settled entries whose start was never observed', () => {
    const bars = buildTimelineBars(
      [], [],
      { ghost: { workflowId: 'w1', status: 'Succeeded', settledAtMs: NOW, startedAtMs: null } },
      scope,
    );
    expect(bars).toHaveLength(0);
  });

  it('drops out-of-scope workflows', () => {
    const bars = buildTimelineBars(
      [{ executionId: 'r-out', workflowId: 'other', status: 'Running', startedAt: iso(NOW - MIN), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      [{ executionId: 'in', workflowId: 'w1', status: 'Succeeded', startedAt: iso(NOW - 6 * MIN), completedAt: iso(NOW - 5 * MIN), parentExecutionId: null }],
      {}, scope,
    );
    expect(bars.map((b) => b.executionId)).toEqual(['in']);
  });

  it('takes no window, so a clock tick cannot change its result', () => {
    // Load-bearing for the render path: bar building and lane allocation sit on this, and both
    // used to re-run every second only because the window they took had moved. The server already
    // windows `recent`; a bar that ages past the left edge between two polls is clamped by
    // placeBar, not dropped here. Feeding an ancient run through proves the filter is really gone.
    const bars = buildTimelineBars(
      [],
      [{ executionId: 'old', workflowId: 'w1', status: 'Succeeded', startedAt: iso(NOW - 60 * MIN), completedAt: iso(NOW - 40 * MIN), parentExecutionId: null }],
      {}, scope,
    );
    expect(bars.map((b) => b.executionId)).toEqual(['old']);
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

  it('gives a workflow with density but no bar a lane, sorted behind the ones that have bars', () => {
    // Every run of w2 fell past the raw cap. No lane would mean its density has nowhere to draw
    // and the workflow reads as idle — the exact misreading density exists to prevent.
    const { lanes, placed } = assignLanes([bar('a', 'w1', 5, 2)], nodes, new Set(['w2']));
    expect(lanes.map((l) => l.workflowId)).toEqual(['w1', 'w2']);
    // One row tall even with nothing in it, otherwise label and strip collapse to zero height.
    expect(lanes[1].subRowCount).toBe(1);
    expect(lanes[1].hasActive).toBe(false);
    expect(placed).toHaveLength(1);
  });

  it('does not duplicate a lane that has both bars and density', () => {
    const { lanes } = assignLanes([bar('a', 'w1', 5, 2)], nodes, new Set(['w1']));
    expect(lanes.map((l) => l.workflowId)).toEqual(['w1']);
    expect(lanes[0].subRowCount).toBe(1);
  });
});

describe('buildDensityCells', () => {
  // A 4 h window bucketed at 5 min, with the bar/aggregate seam 30 min before NOW.
  const WIDE = windowFor(NOW, 240 * MIN, 1000);
  const SINCE = NOW - 240 * MIN;
  const SEAM = NOW - 30 * MIN;
  const BUCKET_S = 300;

  const cells = (buckets: { bucketIndex: number; total: number; failed?: number; cancelled?: number }[]) =>
    buildDensityCells(
      buckets.map((b) => ({ failed: 0, cancelled: 0, ...b })),
      SINCE, BUCKET_S, SEAM, WIDE,
    );

  it('maps a bucket index onto the pixels of its time range', () => {
    const [cell] = cells([{ bucketIndex: 2, total: 5 }]);
    expect(cell.fromMs).toBe(SINCE + 2 * BUCKET_S * 1000);
    expect(cell.toMs).toBe(SINCE + 3 * BUCKET_S * 1000);
    expect(cell.leftPx).toBeCloseTo(timeToX(cell.fromMs, WIDE), 5);
    // The DRAWN width is deliberately one pixel short of the time range — see the gap test below.
    expect(cell.widthPx).toBeCloseTo(timeToX(cell.toMs, WIDE) - cell.leftPx - OPS_DENSITY_CELL_GAP_PX, 5);
  });

  it('leaves a gap between neighbouring buckets so the slices stay countable', () => {
    // Abutting cells were half the original defect: to(i) === from(i + 1) exactly, so 48 buckets
    // fused into one uninterrupted rectangle that read as a single long run.
    const [first, second] = cells([{ bucketIndex: 2, total: 5 }, { bucketIndex: 3, total: 5 }]);
    expect(second.leftPx - (first.leftPx + first.widthPx)).toBeCloseTo(OPS_DENSITY_CELL_GAP_PX, 5);
  });

  it('clips the bucket that straddles the seam instead of drawing over the bars', () => {
    // The seam is wherever the oldest returned run happens to sit — it does not land on a bucket
    // boundary. Half of that bucket's runs are already bars; only the older half may be drawn.
    const offSeam = NOW - 32.5 * MIN;
    const straddled = Math.floor((offSeam - SINCE) / (BUCKET_S * 1000));
    const [cell] = buildDensityCells(
      [{ bucketIndex: straddled, total: 9, failed: 0, cancelled: 0 }],
      SINCE, BUCKET_S, offSeam, WIDE,
    );
    expect(cell.toMs).toBe(offSeam);
    expect(cell.fromMs).toBeLessThan(offSeam);
  });

  it('drops buckets that lie entirely right of the seam — those runs are already bars', () => {
    const pastSeam = Math.floor((SEAM - SINCE) / (BUCKET_S * 1000)) + 1;
    expect(cells([{ bucketIndex: pastSeam, total: 9 }])).toHaveLength(0);
  });

  it('drops empty buckets and keeps a sub-pixel one at least 1 px wide', () => {
    // A slice that rounds away to nothing would read as a gap in the history — the opposite of
    // what it says. A zero-count slice, on the other hand, has nothing to say at all.
    expect(cells([{ bucketIndex: 3, total: 0 }])).toHaveLength(0);
    const narrow = buildDensityCells(
      [{ bucketIndex: 3, total: 1, failed: 0, cancelled: 0 }],
      SINCE, 1, SEAM, WIDE,
    );
    expect(narrow[0].widthPx).toBeGreaterThanOrEqual(1);
  });

  it('carries the outcome split through untouched', () => {
    const [cell] = cells([{ bucketIndex: 1, total: 20, failed: 3, cancelled: 1 }]);
    expect(cell).toMatchObject({ total: 20, failed: 3, cancelled: 1 });
  });

  it('returns nothing before the track has been measured or without a bucket width', () => {
    const unmeasured = windowFor(NOW, 240 * MIN, 0);
    expect(buildDensityCells([{ bucketIndex: 1, total: 4, failed: 0, cancelled: 0 }], SINCE, BUCKET_S, SEAM, unmeasured)).toHaveLength(0);
    expect(cells([{ bucketIndex: 1, total: 4 }]).length).toBeGreaterThan(0);
    expect(buildDensityCells([{ bucketIndex: 1, total: 4, failed: 0, cancelled: 0 }], SINCE, 0, SEAM, WIDE)).toHaveLength(0);
  });
});

describe('densityColumnHeight', () => {
  it('is load-bearing that a column can never be as tall as a run bar', () => {
    // The whole defect: a density slice drawn at (and above) bar height reads as one long run,
    // which is the misreading the aggregate exists to prevent. Height alone must disambiguate.
    expect(OPS_DENSITY_MAX_H).toBeLessThan(OPS_BAR_H);
  });

  it('gives the busiest slice the full height', () => {
    expect(densityColumnHeight(500, 500)).toBe(OPS_DENSITY_MAX_H);
  });

  it('never lets a hot lane erase a quiet one', () => {
    // Scaled against the busiest slice on the whole board, a single run beside a 500-run peak
    // rounds to zero px — and a missing column says "nothing ran here", which is a lie.
    expect(densityColumnHeight(1, 500)).toBe(OPS_DENSITY_MIN_H);
  });

  it('never shrinks as the run count grows', () => {
    const heights = [1, 5, 20, 80, 250, 500].map((t) => densityColumnHeight(t, 500));
    for (let i = 1; i < heights.length; i++) expect(heights[i]).toBeGreaterThanOrEqual(heights[i - 1]);
  });

  it('stays within its bounds for a degenerate or over-peak count', () => {
    // peak = 0 is the first-paint / unmeasured case: a floor, never NaN.
    expect(densityColumnHeight(4, 0)).toBe(OPS_DENSITY_MIN_H);
    expect(densityColumnHeight(0, 500)).toBe(OPS_DENSITY_MIN_H);
    expect(densityColumnHeight(900, 500)).toBe(OPS_DENSITY_MAX_H);
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
  it('keeps four to six axis labels at every selectable window', () => {
    const labels = (windowMin: number) => (windowMin * MIN) / tickStepFor(windowMin * MIN);
    for (const windowMin of OPS_WINDOW_MINUTES) {
      expect(labels(windowMin)).toBeGreaterThanOrEqual(4);
      expect(labels(windowMin)).toBeLessThanOrEqual(6);
    }
    expect(tickStepFor(30 * MIN)).toBe(5 * MIN);
    expect(tickStepFor(60 * MIN)).toBe(15 * MIN);
  });

  it('every step is a wall-clock divisor, so ticks land on round times', () => {
    // axisTicks aligns to multiples of the step. A 45-minute step would put the 3 h axis on 09:45 /
    // 10:30 for no gain over 30.
    for (const windowMin of OPS_WINDOW_MINUTES) {
      expect((60 * MIN) % tickStepFor(windowMin * MIN)).toBe(0);
    }
  });

  it('switches step at the boundary, not inside it', () => {
    expect(tickStepFor(30 * MIN + 1)).toBe(15 * MIN);
    // Wider windows are no longer selectable; the last branch is the floor for anything handed in.
    expect(tickStepFor(240 * MIN)).toBe(15 * MIN);
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
      {}, new Set(['w1']),
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

describe('assignLanes — sub-row ceiling', () => {
  const nodes = new Map<string, OpsNode>([['w1', node('w1', 'Alpha')]]);

  /** N runs that all overlap each other, so every one of them wants its own sub-row. */
  const overlapping = (count: number): TimelineBarInput[] =>
    Array.from({ length: count }, (_, i) => ({
      executionId: `e${i}`, workflowId: 'w1', status: 'Succeeded',
      startedAtMs: NOW - 10 * MIN, completedAtMs: NOW - MIN,
      parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null,
    }));

  it('stops stacking at the ceiling instead of growing a lane without bound', () => {
    // Unbounded, a lane is a function of a workflow's concurrency: the bar cap's worth of overlapping
    // runs would be ~152 000 px tall, and the anchored layer promotes that into one composited surface.
    const { lanes } = assignLanes(overlapping(500), nodes);
    expect(lanes[0].subRowCount).toBe(OPS_MAX_SUB_ROWS);
    expect(lanes[0].subRowsCapped).toBe(true);
  });

  it('never drops a bar to stay under the ceiling', () => {
    // Overlapping is crowded; losing a run would be a lie. Every input must come back placed, and
    // inside the ceiling.
    const { placed } = assignLanes(overlapping(500), nodes);
    expect(placed).toHaveLength(500);
    expect(new Set(placed.map((p) => p.executionId)).size).toBe(500);
    expect(Math.max(...placed.map((p) => p.subRow))).toBeLessThan(OPS_MAX_SUB_ROWS);
  });

  it('leaves a lane below the ceiling untouched and unflagged', () => {
    const { lanes, placed } = assignLanes(overlapping(4), nodes);
    expect(lanes[0].subRowCount).toBe(4);
    expect(lanes[0].subRowsCapped).toBe(false);
    expect(placed.map((p) => p.subRow).sort()).toEqual([0, 1, 2, 3]);
  });
});
