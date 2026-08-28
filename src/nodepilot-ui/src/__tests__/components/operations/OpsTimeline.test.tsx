import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { OpsTimeline } from '../../../components/operations/OpsTimeline';
import { OPS_BAR_H } from '../../../components/operations/OpsTimelineBar';
import { OPS_DENSITY_MAX_H, timeToX, windowFor } from '../../../lib/opsTimeline';
import type { OpsNode } from '../../../types/api';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function node(workflowId: string, name: string): OpsNode {
  return { workflowId, name, folderId: 'f1', folderPath: '/ops', isEnabled: true, runningCount: 0, lastStatus: null, callFrequency: null, canRun: true, canEdit: true };
}

const NODES = new Map([['w1', node('w1', 'Nightly Backup')], ['w2', node('w2', 'Report Gen')]]);

// Kept at module level so their identity survives a rerender. The timeline re-anchors its settled
// layer when `recent` changes reference, matching React Query's structural sharing, where a poll
// that changed nothing keeps the same array. Rebuilding these literals per render would make every
// clock tick look like a fresh snapshot and defeat the anchoring.
const DEFAULT_RUNNING = [{ executionId: 'run-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }];
// Same reason as the arrays below: OperationsPage derives both of these with `useMemo` from
// `scopedNodes`, so they keep their identity between polls. Rebuilding them per render would
// invalidate every downstream memo and make a tick look like a new snapshot.
const DEFAULT_SCOPE = new Set(['w1', 'w2']);
const NO_DENSITY: [] = [];
const NO_LOCALLY_SETTLED = {};
const DEFAULT_RECENT = [{ executionId: 'done-1', workflowId: 'w2', status: 'Failed', startedAt: new Date(NOW - 10 * MIN).toISOString(), completedAt: new Date(NOW - 8 * MIN).toISOString(), parentExecutionId: null }];

function renderTimeline(overrides: Partial<Parameters<typeof OpsTimeline>[0]> = {}) {
  const onSelect = vi.fn();
  const el = (extra: Partial<Parameters<typeof OpsTimeline>[0]>) => (
    <OpsTimeline
      nowMs={NOW}
      running={DEFAULT_RUNNING}
      recent={DEFAULT_RECENT}
      density={NO_DENSITY}
      locallySettled={NO_LOCALLY_SETTLED}
      scopedWorkflowIds={DEFAULT_SCOPE}
      nodesById={NODES}
      selectedExecutionId={null}
      nextStart={null}
      overdueMs={10 * MIN}
      windowMs={30 * MIN}
      historyFromMs={null}
      recentSinceMs={NOW - 30 * MIN}
      densityBucketSeconds={0}
      densityCapped={false}
      onSelect={onSelect}
      {...overrides}
      {...extra}
    />
  );
  const { rerender } = render(el({}));
  // Advances only the clock, which is the axis the anchored render path is built on.
  const tick = (nowMs: number) => rerender(el({ nowMs }));
  return { onSelect, tick, rerender: (extra: Partial<Parameters<typeof OpsTimeline>[0]>) => rerender(el(extra)) };
}

/** Reads the pixel value out of a `translateX(-12.5px)` transform. */
function translateXOf(el: HTMLElement): number {
  const m = /translateX\((-?[\d.]+)px\)/.exec(el.style.transform);
  return m ? parseFloat(m[1]) : NaN;
}

describe('OpsTimeline', () => {
  it('renders a lane per workflow with name + folder and one bar per execution', () => {
    renderTimeline();
    expect(screen.getByTestId('ops-timeline')).toBeInTheDocument();
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('Report Gen')).toBeInTheDocument();
    expect(screen.getAllByText('/ops')).toHaveLength(2);
    // One bar button per execution, labeled workflow + status.
    expect(screen.getByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
    // NOW marker on the axis.
    expect(screen.getByText('Now')).toBeInTheDocument();
  });

  it('keeps wall-clock labels clear of the NOW marker', () => {
    renderTimeline();
    const nowX = parseFloat(screen.getByTestId('ops-now-label').style.left);
    const ticks = screen.getAllByTestId('ops-time-tick');

    expect(ticks.length).toBeGreaterThan(0);
    for (const tick of ticks) {
      expect(Math.abs(parseFloat(tick.style.left) - nowX)).toBeGreaterThanOrEqual(36);
    }
  });

  it('clicking a bar selects its execution', () => {
    const { onSelect } = renderTimeline();
    fireEvent.click(screen.getByTitle(/Nightly Backup · Running/));
    expect(onSelect).toHaveBeenCalledWith('run-1');
  });

  it('shows a copyable execution-id chip next to each workflow name in the lane label', () => {
    renderTimeline();
    // 8-char prefix of the execution id, in parens, behind the workflow name.
    expect(screen.getByText('(run-1)')).toBeInTheDocument();
    expect(screen.getByText('(done-1)')).toBeInTheDocument();
  });

  it('renders each overlapping run as its own labeled row instead of a ×N badge', () => {
    // Two concurrent runs of the same workflow stack into two sub-rows; each sub-row gets
    // its own full-name label + job-id chip, and no ×2 multiplier is shown.
    renderTimeline({
      running: [
        { executionId: 'run-a', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
        { executionId: 'run-b', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
      ],
      recent: [],
    });
    expect(screen.getAllByText('Nightly Backup')).toHaveLength(2);
    expect(screen.queryByText(/×2/)).not.toBeInTheDocument();
    expect(screen.getByText('(run-a)')).toBeInTheDocument();
    expect(screen.getByText('(run-b)')).toBeInTheDocument();
  });

  it('marks the selected bar', () => {
    renderTimeline({ selectedExecutionId: 'run-1' });
    expect(screen.getByTitle(/Nightly Backup · Running/)).toHaveAttribute('aria-pressed', 'true');
  });

  it('renders locally-settled runs not yet confirmed by the snapshot', () => {
    renderTimeline({
      running: [],
      recent: [],
      locallySettled: {
        'fresh-1': { workflowId: 'w1', status: 'Succeeded', settledAtMs: NOW - 1000, startedAtMs: NOW - 2 * MIN },
      },
    });
    expect(screen.getByTitle(/Nightly Backup · Succeeded/)).toBeInTheDocument();
  });

  it('shows the idle hero with the next scheduled start when nothing is in the window', () => {
    renderTimeline({
      running: [], recent: [], locallySettled: {},
      nextStart: { name: 'Inventory Sync', atMs: NOW + 15 * MIN },
    });
    expect(screen.getByText('Nothing is running right now.')).toBeInTheDocument();
    expect(screen.getByText(/Inventory Sync/)).toBeInTheDocument();
    expect(screen.queryByTestId('ops-timeline')).not.toBeInTheDocument();
  });

  it('draws a call connector between a parent bar and its sub-workflow bar', () => {
    renderTimeline({
      running: [
        { executionId: 'parent-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 5 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
        { executionId: 'child-1', workflowId: 'w2', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: 'parent-1', stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
      ],
      recent: [],
    });
    expect(screen.getByTestId('ops-connectors').querySelectorAll('.np-ops-connector')).toHaveLength(1);
  });

  it('renders no connector layer when no visible parent/child pair exists', () => {
    renderTimeline();
    expect(screen.queryByTestId('ops-connectors')).not.toBeInTheDocument();
  });

  it('filters out-of-scope workflows', () => {
    renderTimeline({ scopedWorkflowIds: new Set(['w2']) });
    expect(screen.queryByTitle(/Nightly Backup/)).not.toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
  });
});

describe('OpsTimeline — overdue runs', () => {
  it('marks a Running bar past the threshold and lifts it into the stuck strip', () => {
    renderTimeline({
      running: [{ executionId: 'run-old', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    const bar = screen.getByTitle(/Nightly Backup · Running/);
    expect(bar.className).toContain('np-ops-bar--overdue');
    // A 3-hour hang is clamped to the window edge and otherwise looks like a 21-minute run,
    // so the strip is what surfaces it.
    expect(screen.getByLabelText('Stuck / long-running')).toBeInTheDocument();
  });

  it('leaves a young run unmarked and renders no strip', () => {
    renderTimeline();
    const bar = screen.getByTitle(/Nightly Backup · Running/);
    expect(bar.className).not.toContain('np-ops-bar--overdue');
    expect(screen.queryByLabelText('Stuck / long-running')).not.toBeInTheDocument();
  });

  it('states the real start time on a bar clipped at the window edge', () => {
    renderTimeline({
      running: [{ executionId: 'run-old', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    expect(screen.getByTitle(/Nightly Backup · Running/).textContent).toMatch(/‹ \d{2}:\d{2}/);
  });

  it('does not mark a long-queued Pending run as overdue', () => {
    renderTimeline({
      running: [{ executionId: 'run-pending', workflowId: 'w1', status: 'Pending', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    expect(screen.queryByLabelText('Stuck / long-running')).not.toBeInTheDocument();
  });
});

describe('OpsTimeline — density for the stretch bars cannot reach', () => {
  // A capped 1 h window on a busy system: thousands of finished runs exist, so the snapshot ships
  // the newest slice as bars and the rest as bucket counts. Without those counts everything older
  // than the slice would render as an empty hatched band.
  const DENSITY_ARGS = {
    running: [],
    recent: [{
      executionId: 'newest', workflowId: 'w1', status: 'Succeeded',
      startedAt: new Date(NOW - 31 * MIN).toISOString(),
      completedAt: new Date(NOW - 30 * MIN).toISOString(), parentExecutionId: null,
    }],
    windowMs: 60 * MIN,
    recentSinceMs: NOW - 60 * MIN,
    historyFromMs: NOW - 20 * MIN,   // seam: bars start here, density covers what is left of it
    densityBucketSeconds: 75,
    density: [{
      workflowId: 'w1',
      buckets: [
        { bucketIndex: 3, total: 12, failed: 0, cancelled: 0 },
        { bucketIndex: 4, total: 20, failed: 3, cancelled: 1 },
      ],
    }],
  };

  it('draws one cell per bucket left of the seam and names its runs in the tooltip', () => {
    renderTimeline(DENSITY_ARGS);
    const cells = screen.getAllByTestId('ops-density-cell');
    expect(cells).toHaveLength(2);
    expect(cells[1].getAttribute('title')).toContain('20 runs');
    expect(cells[1].getAttribute('title')).toContain('3 failed');
    expect(cells[1].getAttribute('title')).toContain('1 cancelled');
  });

  it('omits the zero counts from a clean bucket rather than printing "0 failed"', () => {
    renderTimeline(DENSITY_ARGS);
    const cells = screen.getAllByTestId('ops-density-cell');
    expect(cells[0].getAttribute('title')).toContain('12 runs');
    expect(cells[0].getAttribute('title')).not.toContain('failed');
  });

  it('states the window total and suppresses the "no history" band', () => {
    renderTimeline(DENSITY_ARGS);
    // 12 + 20 runs counted, 3 of them failed: the totals for the whole window.
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('32 finished runs');
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('3 failed');
    // The band claims nothing finished in that stretch, so density replaces it.
    expect(screen.queryByTestId('ops-history-gap')).not.toBeInTheDocument();
  });

  it('says so when the aggregate itself was capped', () => {
    renderTimeline({ ...DENSITY_ARGS, densityCapped: true });
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('More than');
  });

  it('gives a workflow with density but no bar a lane of its own', () => {
    // Every run of w2 fell past the raw cap. Without a lane its history has nowhere to draw and
    // the workflow reads as idle, which is the misreading density exists to prevent.
    renderTimeline({
      ...DENSITY_ARGS,
      density: [{ workflowId: 'w2', buckets: [{ bucketIndex: 5, total: 7, failed: 0, cancelled: 0 }] }],
    });
    expect(screen.getByText('Report Gen')).toBeInTheDocument();
    expect(screen.getAllByTestId('ops-density-cell')).toHaveLength(1);
  });

  it('drops density for out-of-scope workflows, notice included', () => {
    // w2 stays visible so the lane view still renders, but w1's density must not leak into a
    // folder-filtered board, nor into the run total the notice states.
    renderTimeline({
      ...DENSITY_ARGS,
      scopedWorkflowIds: new Set(['w2']),
      recent: [{
        executionId: 'w2-run', workflowId: 'w2', status: 'Succeeded',
        startedAt: new Date(NOW - 31 * MIN).toISOString(),
        completedAt: new Date(NOW - 30 * MIN).toISOString(), parentExecutionId: null,
      }],
    });
    expect(screen.getByTestId('ops-timeline')).toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-cell')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-notice')).not.toBeInTheDocument();
  });

  it('renders nothing extra on a quiet snapshot', () => {
    renderTimeline();
    expect(screen.queryByTestId('ops-density-cell')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-notice')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-axis')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-rug')).not.toBeInTheDocument();
  });

  it('encodes the run count as column height on a shared baseline', () => {
    // The bar-chart property: 20 runs stands taller than 12, and both sit on the same baseline.
    // Drawing every slice at full lane height would fuse them into one slab.
    renderTimeline(DENSITY_ARGS);
    const [twelve, twenty] = screen.getAllByTestId('ops-density-cell');
    const bottom = (el: HTMLElement) => parseFloat(el.style.top) + parseFloat(el.style.height);
    expect(parseFloat(twenty.style.height)).toBeGreaterThan(parseFloat(twelve.style.height));
    expect(bottom(twenty)).toBeCloseTo(bottom(twelve), 5);
  });

  it('keeps every column too short to pass for a run bar', () => {
    // An aggregate that reaches bar height reads as a single long successful run covering half
    // the track.
    renderTimeline(DENSITY_ARGS);
    for (const cell of screen.getAllByTestId('ops-density-cell')) {
      expect(parseFloat(cell.style.height)).toBeLessThanOrEqual(OPS_DENSITY_MAX_H);
      expect(parseFloat(cell.style.height)).toBeLessThan(OPS_BAR_H);
    }
  });

  it('carries no status fill and no count-driven ink', () => {
    // A status fill would make the aggregate look like a succeeded run, and an opacity ramp
    // would double-encode a count that height already carries.
    renderTimeline(DENSITY_ARGS);
    for (const cell of screen.getAllByTestId('ops-density-cell')) {
      expect(cell.style.background).toBe('');
      expect(cell.style.opacity).toBe('');
    }
  });

  it('hangs a failure rug under the baseline where the column cannot show it', () => {
    // A proportional stack would be only a pixel or two tall at the failure ratios this view
    // sees, so the rug encodes presence rather than proportion.
    renderTimeline(DENSITY_ARGS);
    const rugs = screen.getAllByTestId('ops-density-rug');
    expect(rugs).toHaveLength(1);
    expect(rugs[0].style.background).toBe('var(--color-error)');
    const [, twenty] = screen.getAllByTestId('ops-density-cell');
    const columnBottom = parseFloat(twenty.style.top) + parseFloat(twenty.style.height);
    expect(parseFloat(rugs[0].style.top)).toBeGreaterThan(columnBottom);
    expect(rugs[0].getAttribute('title')).toContain('3 failed');
  });

  it('marks a cancelled-only slice too, subordinate to failures', () => {
    renderTimeline({
      ...DENSITY_ARGS,
      density: [{ workflowId: 'w1', buckets: [{ bucketIndex: 4, total: 9, failed: 0, cancelled: 2 }] }],
    });
    const rugs = screen.getAllByTestId('ops-density-rug');
    expect(rugs).toHaveLength(1);
    expect(rugs[0].style.background).toBe('var(--color-skipped)');
  });

  it('draws one baseline per density lane, spanning exactly its columns', () => {
    renderTimeline(DENSITY_ARGS);
    const axes = screen.getAllByTestId('ops-density-axis');
    expect(axes).toHaveLength(1);
    const cells = screen.getAllByTestId('ops-density-cell');
    const left = parseFloat(axes[0].style.left);
    const right = left + parseFloat(axes[0].style.width);
    expect(left).toBeCloseTo(parseFloat(cells[0].style.left), 5);
    expect(right).toBeCloseTo(parseFloat(cells[1].style.left) + parseFloat(cells[1].style.width), 5);
  });
});

describe('OpsTimeline — duration stays comparable at wide windows', () => {
  // At a 1 h window a 2-minute run and a 20-second run are both only a few pixels wide. A
  // bar-width floor would flatten them to the same length and hide which run took longer.
  const wideRuns = {
    running: [],
    recent: [
      { executionId: 'long', workflowId: 'w1', status: 'Succeeded',
        startedAt: new Date(NOW - 12 * MIN).toISOString(),
        completedAt: new Date(NOW - 10 * MIN).toISOString(), parentExecutionId: null },
      { executionId: 'short', workflowId: 'w2', status: 'Succeeded',
        startedAt: new Date(NOW - 6 * MIN).toISOString(),
        completedAt: new Date(NOW - 6 * MIN + 20_000).toISOString(), parentExecutionId: null },
    ],
  };

  it('writes the duration beside bars too narrow to hold it', () => {
    renderTimeline({ ...wideRuns, windowMs: 60 * MIN });
    // Both runs are too narrow at 1 h to carry an inside label, so the text
    // beside the bar is what keeps them distinguishable.
    expect(screen.getByText('2:00')).toBeInTheDocument();
    expect(screen.getByText('0:20')).toBeInTheDocument();
  });

  it('does not duplicate the duration when it already fits inside the bar', () => {
    renderTimeline({
      running: [],
      recent: [{
        executionId: 'wide', workflowId: 'w1', status: 'Succeeded',
        startedAt: new Date(NOW - 12 * MIN).toISOString(),
        completedAt: new Date(NOW - 2 * MIN).toISOString(), parentExecutionId: null,
      }],
      windowMs: 30 * MIN,
    });
    expect(screen.getAllByText('10:00')).toHaveLength(1);
  });
});

describe('OpsTimeline — a clock tick must not touch the settled bars', () => {
  // The board carries up to 4000 settled bars. Their geometry is frozen at the snapshot and the
  // whole layer is translated once per tick, so a tick costs one compositor property instead of a
  // `left` write plus a layout pass per bar. These tests pin that arrangement, not its speed.

  it('moves the anchored layer and leaves the bars inside it alone', () => {
    const { tick } = renderTimeline();
    const settled = screen.getByTitle(/Report Gen · Failed/);
    const layer = screen.getByTestId('ops-shift-layer');

    expect(layer).toContainElement(settled);
    const leftBefore = settled.style.left;
    expect(translateXOf(layer)).toBeCloseTo(0, 5); // anchor == live window at snapshot time

    tick(NOW + 60_000);

    // The bar did not move in its own coordinate system, so nothing rewrote its geometry.
    expect(settled.style.left).toBe(leftBefore);
    // The layer did move: one property change covers every settled bar at once.
    expect(translateXOf(layer)).toBeLessThan(0);
  });

  it('lands the translated bar exactly where the live window would put it', () => {
    // The shortcut is only valid if the anchored position plus the shift equals the live
    // position. Both windows share a span, so the difference is a pure translation.
    const { tick } = renderTimeline();
    const settled = screen.getByTitle(/Report Gen · Failed/);
    const layer = screen.getByTestId('ops-shift-layer');

    tick(NOW + 60_000);

    const live = windowFor(NOW + 60_000, 30 * MIN, 600);
    const drawn = parseFloat(settled.style.left) + translateXOf(layer);
    expect(drawn).toBeCloseTo(timeToX(NOW - 10 * MIN, live), 4);
  });

  it('keeps a running bar out of the layer so it can grow toward NOW', () => {
    // A translation slides a bar; it cannot stretch one. A running bar's right edge has to track
    // the NOW line, so it is placed against the live window every tick and stays outside.
    const { tick } = renderTimeline();
    // getByTitle, not getByRole: this bar is wide enough to print its duration inside, so its
    // accessible name is that text and not the title attribute.
    const running = screen.getByTitle(/Nightly Backup · Running/);
    expect(screen.getByTestId('ops-shift-layer')).not.toContainElement(running);

    const widthBefore = parseFloat(running.style.width);
    tick(NOW + 60_000);
    expect(parseFloat(running.style.width)).toBeGreaterThan(widthBefore);
  });
});

describe('OpsTimeline — re-anchoring', () => {
  it('re-anchors when a new snapshot arrives, so the layer never drifts unboundedly', () => {
    // The offset is only ever one poll interval wide because fresh data resets it. Without this,
    // a long-lived tab would translate the layer further and further from its own coordinates.
    const { tick, rerender } = renderTimeline();
    tick(NOW + 60_000);
    expect(translateXOf(screen.getByTestId('ops-shift-layer'))).toBeLessThan(0);

    // Same content, new array identity: what a poll that changed something looks like.
    rerender({ recent: [...DEFAULT_RECENT], nowMs: NOW + 60_000 });
    expect(translateXOf(screen.getByTestId('ops-shift-layer'))).toBeCloseTo(0, 5);
  });
});

describe('OpsTimeline — a clock tick must not touch the density either', () => {
  // Density is frozen history, like a settled bar, and it is the bulkier of the two: a busy board
  // draws about 24 lanes of up to 48 buckets each. It has to be anchored the same way, or every
  // one of those cells is recomputed and re-rendered once a second while the bars beside them
  // sit still.
  const ARGS = {
    running: [],
    recent: [{
      executionId: 'newest', workflowId: 'w1', status: 'Succeeded',
      startedAt: new Date(NOW - 31 * MIN).toISOString(),
      completedAt: new Date(NOW - 30 * MIN).toISOString(), parentExecutionId: null,
    }],
    windowMs: 60 * MIN,
    recentSinceMs: NOW - 60 * MIN,
    historyFromMs: NOW - 20 * MIN,
    densityBucketSeconds: 75,
    density: [{
      workflowId: 'w1',
      buckets: [
        { bucketIndex: 3, total: 12, failed: 0, cancelled: 0 },
        { bucketIndex: 4, total: 20, failed: 3, cancelled: 1 },
      ],
    }],
  };

  it('draws every density mark inside the anchored layer', () => {
    renderTimeline(ARGS);
    const layer = screen.getByTestId('ops-shift-layer');
    expect(layer).toContainElement(screen.getAllByTestId('ops-density-cell')[0]);
    expect(layer).toContainElement(screen.getByTestId('ops-density-axis'));
    expect(layer).toContainElement(screen.getByTestId('ops-density-rug'));
  });

  it('leaves the cells alone and moves only the layer', () => {
    const { tick } = renderTimeline(ARGS);
    const cell = screen.getAllByTestId('ops-density-cell')[1];
    const layer = screen.getByTestId('ops-shift-layer');
    const before = { left: cell.style.left, width: cell.style.width, height: cell.style.height, top: cell.style.top };

    tick(NOW + 60_000);

    expect(cell.style.left).toBe(before.left);
    expect(cell.style.width).toBe(before.width);
    expect(cell.style.height).toBe(before.height);
    expect(cell.style.top).toBe(before.top);
    expect(translateXOf(layer)).toBeLessThan(0);
  });

  it('lands a translated cell where the live window would put it', () => {
    // Same check the bars get: the anchored position plus the shift must equal the position the
    // live window would have drawn.
    const { tick } = renderTimeline(ARGS);
    const cell = screen.getAllByTestId('ops-density-cell')[0];
    const layer = screen.getByTestId('ops-shift-layer');

    tick(NOW + 60_000);

    // Bucket 3 at a 75 s bucket width starts 225 s into the 1 h window.
    const live = windowFor(NOW + 60_000, 60 * MIN, 600);
    const drawn = parseFloat(cell.style.left) + translateXOf(layer);
    expect(drawn).toBeCloseTo(timeToX(NOW - 60 * MIN + 225_000, live), 4);
  });

  it('draws the no-history band in the anchored layer too', () => {
    // The band is also a statement about history, so it belongs in the same coordinate system.
    // It only appears when there is no density to replace it.
    renderTimeline({ ...ARGS, density: [], densityBucketSeconds: 0 });
    expect(screen.getByTestId('ops-shift-layer')).toContainElement(screen.getByTestId('ops-history-gap'));
  });
});

describe('OpsTimeline — the anchored subtree is built per snapshot, not per tick', () => {
  // Memoizing the geometry is only half of it. If the layer's JSX lived in the render body, a
  // clock tick would rebuild every density element and re-run densityTitle for each one: two
  // toLocaleTimeString calls and up to three i18n lookups per cell, none of which can change
  // between polls. These tests count the formatting calls rather than assert the intent.

  const DENSE = {
    running: [],
    recent: DEFAULT_RECENT,
    windowMs: 60 * MIN,
    recentSinceMs: NOW - 60 * MIN,
    // Seam late enough that all 40 buckets (40 x 75 s = 50 min) sit left of it and render.
    // Anything past the seam is clipped away and would shrink what this measures.
    historyFromMs: NOW - 5 * MIN,
    densityBucketSeconds: 75,
    density: [{
      workflowId: 'w2',
      buckets: Array.from({ length: 40 }, (_, i) => ({
        bucketIndex: i, total: 5 + (i % 7), failed: i % 5 === 0 ? 1 : 0, cancelled: 0,
      })),
    }],
  };
  const QUIET = { ...DENSE, density: [], densityBucketSeconds: 0 };

  /** Clock formats at mount, and clock formats added by one tick. */
  function clockFormats(args: Partial<Parameters<typeof OpsTimeline>[0]>) {
    const spy = vi.spyOn(Date.prototype, 'toLocaleTimeString');
    const { tick } = renderTimeline(args);
    const mount = spy.mock.calls.length;
    tick(NOW + 60_000);
    const perTick = spy.mock.calls.length - mount;
    spy.mockRestore();
    cleanup();
    return { mount, perTick };
  }

  it('adds no per-tick clock formatting, however many density cells are on screen', () => {
    const dense = clockFormats(DENSE);
    const quiet = clockFormats(QUIET);

    // Confirms the fixture really does render density, so the comparison below is not vacuous.
    // 40 buckets with two clock labels each is 80, less the one the no-history band would have
    // formatted, since density suppresses that band. The expected delta is therefore 79.
    expect(dense.mount - quiet.mount).toBeGreaterThanOrEqual(79);

    // Those formats belong to the snapshot, not to the clock. Whatever the axis costs per tick,
    // density must add nothing on top of it.
    expect(dense.perTick).toBe(quiet.perTick);
  });
});

describe('OpsTimeline — keyboard reach without a trap', () => {
  const DENSITY_ARGS = {
    running: [],
    recent: DEFAULT_RECENT,
    windowMs: 60 * MIN,
    recentSinceMs: NOW - 60 * MIN,
    historyFromMs: NOW - 20 * MIN,
    densityBucketSeconds: 75,
    density: [{
      workflowId: 'w2',
      buckets: [
        { bucketIndex: 3, total: 12, failed: 0, cancelled: 0 },
        { bucketIndex: 4, total: 20, failed: 3, cancelled: 1 },
      ],
    }],
  };

  // A busy board carries thousands of bars. One tab stop apiece would make the timeline a keyboard
  // trap, leaving the departure board below it unreachable. The track is a single tab stop and
  // moves aria-activedescendant across the bars instead.

  it('keeps every bar out of the tab order', () => {
    renderTimeline();
    for (const bar of [screen.getByTitle(/Nightly Backup · Running/), screen.getByTitle(/Report Gen · Failed/)]) {
      expect(bar).toHaveAttribute('tabindex', '-1');
    }
  });

  it('makes the track itself the one tab stop and points it at a bar', () => {
    renderTimeline();
    const track = screen.getByTestId('ops-track');
    expect(track).toHaveAttribute('tabindex', '0');
    expect(track).toHaveAttribute('role', 'grid');
    expect(track.getAttribute('aria-activedescendant')).toMatch(/^ops-bar-/);
  });

  it('walks the bars with the arrow keys and opens one with Enter', () => {
    const { onSelect } = renderTimeline();
    const track = screen.getByTestId('ops-track');

    const first = track.getAttribute('aria-activedescendant');
    fireEvent.keyDown(track, { key: 'ArrowRight' });
    expect(track.getAttribute('aria-activedescendant')).not.toBe(first);

    fireEvent.keyDown(track, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledTimes(1);

    // Home returns to the first bar in lane order.
    fireEvent.keyDown(track, { key: 'Home' });
    expect(track.getAttribute('aria-activedescendant')).toBe(first);
  });

  it('moves lane-wise with ArrowDown/ArrowUp instead of crawling through a lane', () => {
    // The two fixture bars sit in different lanes, so a vertical press has to land on the other
    // one. Crawling bar by bar would be unusable on a board where one lane holds hundreds.
    renderTimeline();
    const track = screen.getByTestId('ops-track');
    const first = track.getAttribute('aria-activedescendant');

    fireEvent.keyDown(track, { key: 'ArrowDown' });
    const next = track.getAttribute('aria-activedescendant');
    expect(next).not.toBe(first);
    expect(next).toMatch(/^ops-bar-/);

    fireEvent.keyDown(track, { key: 'ArrowUp' });
    expect(track.getAttribute('aria-activedescendant')).toBe(first);
  });

  it('stays put when the neighbouring lane has no bars to land on', () => {
    // Covers the `first !== -1` guard. Without it a press past the last lane clears the roving
    // pointer and focus lands nowhere. The same branch covers a lane built from density alone.
    renderTimeline();
    const track = screen.getByTestId('ops-track');

    fireEvent.keyDown(track, { key: 'End' });
    const last = track.getAttribute('aria-activedescendant');
    expect(last).toMatch(/^ops-bar-/);
    fireEvent.keyDown(track, { key: 'ArrowDown' });
    expect(track.getAttribute('aria-activedescendant')).toBe(last);

    fireEvent.keyDown(track, { key: 'Home' });
    const first = track.getAttribute('aria-activedescendant');
    fireEvent.keyDown(track, { key: 'ArrowUp' });
    expect(track.getAttribute('aria-activedescendant')).toBe(first);
  });

  it('reaches both ends with End and ArrowLeft', () => {
    renderTimeline();
    const track = screen.getByTestId('ops-track');
    const first = track.getAttribute('aria-activedescendant');

    fireEvent.keyDown(track, { key: 'End' });
    const last = track.getAttribute('aria-activedescendant');
    expect(last).not.toBe(first);

    fireEvent.keyDown(track, { key: 'ArrowLeft' });
    expect(track.getAttribute('aria-activedescendant')).toBe(first);
  });

  it('announces density columns instead of hiding them behind a title attribute', () => {
    // A div with only a `title` does not reach a screen reader, and the column carries the run
    // counts for the density stretch.
    renderTimeline(DENSITY_ARGS);
    const cell = screen.getAllByTestId('ops-density-cell')[0];
    expect(cell).toHaveAttribute('role', 'img');
    expect(cell.getAttribute('aria-label')).toContain('12 runs');
    expect(cell).not.toHaveAttribute('tabindex');
  });

  it('announces a capped lane once instead of repeating the marker on every sub-row', () => {
    renderTimeline({
      recent: [],
      running: Array.from({ length: 13 }, (_, i) => ({
        executionId: `busy-${i}`,
        workflowId: 'w1',
        status: 'Running',
        startedAt: new Date(NOW - 5 * MIN).toISOString(),
        parentExecutionId: null,
        stepsFinished: null,
        lastCompletedStepName: null,
        lastProgressAt: null,
        activeStepCount: null,
      })),
    });

    expect(screen.getAllByTestId('ops-lane-capped')).toHaveLength(1);
  });
});
