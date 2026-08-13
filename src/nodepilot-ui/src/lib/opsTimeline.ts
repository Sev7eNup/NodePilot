import type { OpsDensityBucket, OpsNode, OpsRecentExecution, OpsRunningExecution } from '../types/api';
import type { LocalSettled } from '../stores/operationsStore';

// Pure geometry + lane allocation for the Mission-Control live timeline. Everything here is
// side-effect-free and driven entirely by the caller's clock (`nowMs`) so it stays trivially
// unit-testable — the rendering layer only maps the returned pixel values onto absolutely
// positioned divs.

/** Visible time span of the timeline (default window). */
export const OPS_WINDOW_MS = 30 * 60_000;
/** Horizontal fraction of the track where the NOW line sits (right gutter = growing-bar room). */
export const OPS_NOW_FRACTION = 0.92;
/** Axis tick spacing at the default window. */
export const OPS_TICK_STEP_MS = 5 * 60_000;

/**
 * Selectable windows, in minutes — must match the server's clamp list
 * (`OperationsController.AllowedWindowMinutes`). The first entry is the default the page opens on.
 */
export const OPS_WINDOW_MINUTES = [30, 60] as const;
export type OpsWindowMinutes = (typeof OPS_WINDOW_MINUTES)[number];

/**
 * Tick spacing that keeps four to six labels on the axis at either selectable window: six at 30 min,
 * four at 1 h. Keeping the 5-minute step at 1 h would draw twelve.
 *
 * The steps are wall-clock divisors on purpose — `axisTicks` aligns ticks to multiples of the step,
 * so anything that does not divide an hour lands the axis on ragged times.
 *
 * Windows wider than 1 h are no longer offered; the final branch is the floor for anything a caller
 * hands in regardless.
 */
export function tickStepFor(windowMs: number): number {
  if (windowMs <= 30 * 60_000) return 5 * 60_000;
  return 15 * 60_000;
}

export interface TimelineWindow {
  /** Timestamp rendered at x=0. */
  startMs: number;
  /** Timestamp rendered at x=trackWidthPx (past the NOW line, right gutter). */
  endMs: number;
  nowMs: number;
  trackWidthPx: number;
}

export interface TimelineBarInput {
  executionId: string;
  workflowId: string;
  status: string;
  startedAtMs: number;
  /** null = still running (bar grows toward NOW). */
  completedAtMs: number | null;
  /** Parent run for sub-workflow executions (call connector source), if any. */
  parentExecutionId: string | null;
  /**
   * Observed step activity — only ever set for live bars from the snapshot's `running` list.
   * Settled and locally-settled bars carry null: they show a terminal glyph, not activity.
   */
  stepsFinished: number | null;
  lastCompletedStepName: string | null;
  lastProgressAtMs: number | null;
}

export interface PlacedBar extends TimelineBarInput {
  leftPx: number;
  widthPx: number;
  /** Bar started before the window; the left edge is clamped and rendered with a fade. */
  clippedLeft: boolean;
  laneIndex: number;
  /** Stacking row within the lane for temporally overlapping executions. */
  subRow: number;
}

export interface TimelineLane {
  workflowId: string;
  name: string;
  folderPath: string;
  subRowCount: number;
  /** Lane has at least one live (running/pending) bar. */
  hasActive: boolean;
  /** Call-hierarchy depth: 0 = top-level, >0 = sub-workflow lane indented under its caller. */
  depth: number;
}

/** Window such that NOW sits at `nowFraction` of the track and the window spans `windowMs`. */
export function windowFor(
  nowMs: number,
  windowMs: number = OPS_WINDOW_MS,
  trackWidthPx: number = 0,
  nowFraction: number = OPS_NOW_FRACTION,
): TimelineWindow {
  const startMs = nowMs - windowMs;
  // The visible [start..now] span occupies `nowFraction` of the width; extrapolate the end.
  const endMs = startMs + windowMs / nowFraction;
  return { startMs, endMs, nowMs, trackWidthPx };
}

/** Linear time→pixel mapping over the window (unclamped — callers clamp as needed). */
export function timeToX(tMs: number, w: TimelineWindow): number {
  const span = w.endMs - w.startMs;
  if (span <= 0 || w.trackWidthPx <= 0) return 0;
  return ((tMs - w.startMs) / span) * w.trackWidthPx;
}

/**
 * Merges the snapshot's running + recent lists with the store's locally-settled overlay into
 * timeline bar inputs.
 *
 * - Snapshot `recent` is authoritative per executionId (real CompletedAt beats the client-side
 *   settled approximation).
 * - `locallySettled` covers the gap between a SignalR terminal event and the next snapshot:
 *   the run has vanished from `running` but is not yet in `recent`. Entries whose start time
 *   was never observed (startedAtMs null) are skipped — there is nothing to draw.
 * - Out-of-scope workflows are dropped.
 *
 * Deliberately takes NO window: the result depends only on the snapshot, so it survives a clock
 * tick untouched and `assignLanes` on top of it does too. That is the whole point — the previous
 * signature took the window purely to drop bars left of it, which re-parsed every timestamp and
 * re-allocated every bar object once a second on a board that changes once every five. The server
 * already windows `recent`; between two polls a bar can age at most one poll interval past the
 * left edge, and `placeBar` clamps it (`clippedLeft`) rather than drawing it out of bounds.
 */
export function buildTimelineBars(
  running: OpsRunningExecution[],
  recent: OpsRecentExecution[],
  locallySettled: Record<string, LocalSettled>,
  scopedWorkflowIds: Set<string>,
): TimelineBarInput[] {
  const bars: TimelineBarInput[] = [];
  const seen = new Set<string>();

  for (const r of recent) {
    if (!scopedWorkflowIds.has(r.workflowId)) continue;
    const completedAtMs = Date.parse(r.completedAt);
    seen.add(r.executionId);
    bars.push({
      executionId: r.executionId,
      workflowId: r.workflowId,
      status: r.status,
      startedAtMs: Date.parse(r.startedAt),
      completedAtMs,
      parentExecutionId: r.parentExecutionId,
      stepsFinished: null,
      lastCompletedStepName: null,
      lastProgressAtMs: null,
    });
  }

  for (const r of running) {
    if (seen.has(r.executionId) || !scopedWorkflowIds.has(r.workflowId)) continue;
    seen.add(r.executionId);
    bars.push({
      executionId: r.executionId,
      workflowId: r.workflowId,
      status: r.status,
      startedAtMs: Date.parse(r.startedAt),
      completedAtMs: null,
      parentExecutionId: r.parentExecutionId,
      stepsFinished: r.stepsFinished ?? null,
      lastCompletedStepName: r.lastCompletedStepName ?? null,
      // Falsy check, not `=== null`: an absent field would otherwise reach Date.parse(undefined)
      // and render as "NaN:NaN". Unknown must degrade to null, never to a bogus number.
      lastProgressAtMs: r.lastProgressAt ? Date.parse(r.lastProgressAt) : null,
    });
  }

  for (const [executionId, s] of Object.entries(locallySettled)) {
    if (seen.has(executionId) || !scopedWorkflowIds.has(s.workflowId)) continue;
    if (s.startedAtMs === null) continue; // never saw it running — nothing to draw
    seen.add(executionId);
    bars.push({
      executionId,
      workflowId: s.workflowId,
      status: s.status,
      startedAtMs: s.startedAtMs,
      completedAtMs: s.settledAtMs,
      parentExecutionId: null, // SignalR deltas carry no parent link; the snapshot fills it in
      stepsFinished: null,
      lastCompletedStepName: null,
      lastProgressAtMs: null,
    });
  }

  return bars;
}

const ACTIVE_STATUSES = new Set(['Running', 'Pending', 'Paused']);

/**
 * Deterministic lane + sub-row allocation. One lane per workflow; temporally overlapping
 * executions within a lane stack into sub-rows (greedy interval allocation on start order).
 *
 * Lane order is call-hierarchical: sub-workflow lanes (bars whose parentExecutionId points
 * into another visible lane) are indented directly under their caller's lane (depth+1).
 * Top-level lanes sort active-first (latest start desc), then by latest completedAt desc;
 * ties broken by workflowId asc. Children keep the same comparator among siblings.
 */
export function assignLanes(
  bars: TimelineBarInput[],
  nodesById: Map<string, OpsNode>,
  densityWorkflowIds: ReadonlySet<string> = new Set(),
): { lanes: TimelineLane[]; placed: Omit<PlacedBar, 'leftPx' | 'widthPx' | 'clippedLeft'>[] } {
  const byWorkflow = new Map<string, TimelineBarInput[]>();
  for (const b of bars) {
    const list = byWorkflow.get(b.workflowId);
    if (list) list.push(b);
    else byWorkflow.set(b.workflowId, [b]);
  }
  // A workflow can have density but no bar: it ran inside the window, yet every one of its runs
  // fell past the raw cap. Without a lane of its own its density strip would have nowhere to
  // draw and the workflow would read as idle — the exact misreading density exists to prevent.
  for (const workflowId of densityWorkflowIds) {
    if (!byWorkflow.has(workflowId)) byWorkflow.set(workflowId, []);
  }

  const wfByExec = new Map(bars.map((b) => [b.executionId, b.workflowId]));

  interface LaneMeta {
    workflowId: string;
    hasActive: boolean;
    sortKey: number; // active: latest startedAt; settled: latest completedAt
    /** Dominant caller lane (from the most recent bar with a visible parent), if any. */
    parentWf: string | null;
  }
  const metas: LaneMeta[] = [];
  for (const [workflowId, list] of byWorkflow) {
    const active = list.filter((b) => b.completedAtMs === null);
    const hasActive = active.length > 0;
    const sortKey = hasActive
      ? Math.max(...active.map((b) => b.startedAtMs))
      : Math.max(...list.map((b) => b.completedAtMs ?? 0));
    let parentWf: string | null = null;
    let bestStart = Number.NEGATIVE_INFINITY;
    for (const b of list) {
      if (!b.parentExecutionId) continue;
      const pwf = wfByExec.get(b.parentExecutionId);
      if (!pwf || pwf === workflowId) continue;
      if (b.startedAtMs > bestStart) { bestStart = b.startedAtMs; parentWf = pwf; }
    }
    metas.push({ workflowId, hasActive, sortKey, parentWf });
  }
  metas.sort((a, b) => {
    if (a.hasActive !== b.hasActive) return a.hasActive ? -1 : 1;
    if (a.sortKey !== b.sortKey) return b.sortKey - a.sortKey;
    return a.workflowId < b.workflowId ? -1 : 1;
  });

  const metaByWf = new Map(metas.map((m) => [m.workflowId, m]));
  const childrenByWf = new Map<string, LaneMeta[]>();
  for (const m of metas) {
    if (m.parentWf === null || !metaByWf.has(m.parentWf)) continue;
    const siblings = childrenByWf.get(m.parentWf) ?? [];
    siblings.push(m);
    childrenByWf.set(m.parentWf, siblings);
  }

  const emitted = new Set<string>();
  const order: { meta: LaneMeta; depth: number }[] = [];
  const emit = (m: LaneMeta, depth: number) => {
    if (emitted.has(m.workflowId)) return;
    emitted.add(m.workflowId);
    order.push({ meta: m, depth });
    for (const child of childrenByWf.get(m.workflowId) ?? []) emit(child, depth + 1);
  };
  for (const m of metas) {
    if (m.parentWf !== null && metaByWf.has(m.parentWf)) continue; // emitted under its caller
    emit(m, 0);
  }
  // Cycle guard (A calls B while B calls A across runs): emit whatever remains at top level.
  for (const m of metas) emit(m, 0);

  const lanes: TimelineLane[] = [];
  const placed: Omit<PlacedBar, 'leftPx' | 'widthPx' | 'clippedLeft'>[] = [];

  order.forEach(({ meta: lane, depth }, laneIndex) => {
    const list = [...(byWorkflow.get(lane.workflowId) ?? [])];
    list.sort((a, b) => (a.startedAtMs - b.startedAtMs) || (a.executionId < b.executionId ? -1 : 1));

    // Greedy interval stacking: place each bar in the first sub-row whose last bar ended
    // before this one starts. Running bars occupy their row until "forever".
    const rowEnds: number[] = [];
    for (const b of list) {
      const end = b.completedAtMs ?? Number.POSITIVE_INFINITY;
      let subRow = rowEnds.findIndex((rowEnd) => rowEnd <= b.startedAtMs);
      if (subRow === -1) {
        subRow = rowEnds.length;
        rowEnds.push(end);
      } else {
        rowEnds[subRow] = end;
      }
      placed.push({ ...b, laneIndex, subRow });
    }

    const node = nodesById.get(lane.workflowId);
    lanes.push({
      workflowId: lane.workflowId,
      name: node?.name ?? lane.workflowId,
      folderPath: node?.folderPath ?? '/',
      // Floored at 1: a density-only lane has no bars and would otherwise be zero rows tall,
      // leaving its label and its density strip nowhere to render.
      subRowCount: Math.max(rowEnds.length, 1),
      hasActive: lane.hasActive,
      depth,
    });
  });

  return { lanes, placed };
}

/** One rendered density slice: pixel geometry plus the counts behind it. */
export interface DensityCell {
  bucketIndex: number;
  /** Time range this slice covers, already clipped to what is actually drawn. */
  fromMs: number;
  toMs: number;
  leftPx: number;
  widthPx: number;
  total: number;
  failed: number;
  cancelled: number;
}

/**
 * Drawn width of a density column, one pixel narrower than the time range it covers.
 *
 * Abutting cells were half of what made the density stretch read as a solid block: `to(i)` equals
 * `from(i + 1)` exactly, so 48 buckets fused into one uninterrupted rectangle. `fromMs`/`toMs` keep
 * the honest range — the pixel comes off the drawn width only.
 */
export const OPS_DENSITY_CELL_GAP_PX = 1;

/**
 * Tallest density column. Strictly below `OPS_BAR_H` (22) and pinned there by test: a column
 * aggregates many runs and must never be readable as one. Everything else about the density
 * stretch — bottom anchoring, the gap, the achromatic fill — follows from that single rule.
 */
export const OPS_DENSITY_MAX_H = 14;

/**
 * Shortest visible column. Columns scale against the busiest slice on the whole board, so a
 * low-volume lane beside a hot one would otherwise round to zero and read as "nothing ran" — the
 * exact misreading the aggregate exists to prevent.
 */
export const OPS_DENSITY_MIN_H = 3;

/**
 * Run count → column height, in px.
 *
 * Linear against `peak`, the busiest slice ANYWHERE on the board rather than within the lane: the
 * lanes share one time axis and one grid, so "taller means more runs" has to hold across lanes too.
 * Per-lane normalisation would draw a lane at 3 runs per slice exactly like a lane at 300 — a new
 * lie, and a worse one, because it looks quantitative. The price is that quiet lanes bottom out,
 * hence the floor. A sqrt/log compression would rescue them and was rejected: bar length is a
 * linear channel, and bending it to make small numbers look bigger is the same sin one level down.
 *
 * No opacity ramp rides along. Height already carries the count, and the ramp this replaces
 * (`0.28 + 0.57 * total / peak`) modulated by nothing on an evenly loaded system — every slice
 * landed within a few percent of the same ink, which is how the strip became a flat slab.
 */
export function densityColumnHeight(total: number, peak: number): number {
  if (peak <= 0 || total <= 0) return OPS_DENSITY_MIN_H;
  const scaled = Math.round((total / peak) * OPS_DENSITY_MAX_H);
  return Math.min(OPS_DENSITY_MAX_H, Math.max(OPS_DENSITY_MIN_H, scaled));
}

/**
 * Turns one workflow's density buckets into drawable cells.
 *
 * Only the stretch LEFT of `seamMs` is emitted: that is where the snapshot ran out of individual
 * runs, and it is the only place an aggregate adds anything. Right of the seam every run is
 * already a bar, and drawing density under them would count the same runs twice on screen. The
 * server nevertheless buckets the whole window (so the totals stay honest); the clipping is a
 * rendering decision made here.
 */
export function buildDensityCells(
  buckets: readonly OpsDensityBucket[],
  recentSinceMs: number,
  bucketSeconds: number,
  seamMs: number,
  w: TimelineWindow,
): DensityCell[] {
  if (bucketSeconds <= 0 || w.trackWidthPx <= 0) return [];
  const bucketMs = bucketSeconds * 1000;
  const cells: DensityCell[] = [];
  for (const b of buckets) {
    if (b.total <= 0) continue;
    const from = Math.max(recentSinceMs + b.bucketIndex * bucketMs, w.startMs);
    const to = Math.min(recentSinceMs + (b.bucketIndex + 1) * bucketMs, seamMs);
    if (to <= from) continue;
    const leftPx = timeToX(from, w);
    // One pixel comes off the right so neighbouring buckets stay countable instead of fusing into
    // a block. Floored at 1 px after that: a slice that rounds away to nothing would read as a gap
    // in the history, which is the opposite of what it says.
    const widthPx = Math.max(timeToX(to, w) - leftPx - OPS_DENSITY_CELL_GAP_PX, 1);
    cells.push({
      bucketIndex: b.bucketIndex,
      fromMs: from,
      toMs: to,
      leftPx,
      widthPx,
      total: b.total,
      failed: b.failed,
      cancelled: b.cancelled,
    });
  }
  return cells;
}

/** Computes final pixel geometry for a bar within the window. */
export function placeBar(
  bar: Omit<PlacedBar, 'leftPx' | 'widthPx' | 'clippedLeft'>,
  w: TimelineWindow,
): PlacedBar {
  const clippedLeft = bar.startedAtMs < w.startMs;
  const startX = clippedLeft ? 0 : timeToX(bar.startedAtMs, w);
  const endMs = bar.completedAtMs ?? w.nowMs;
  const endX = timeToX(Math.min(endMs, w.endMs), w);
  return { ...bar, leftPx: startX, widthPx: Math.max(endX - startX, 0), clippedLeft };
}

/** Whether a bar's raw status counts as live/in-flight. */
export function isActiveBarStatus(status: string): boolean {
  return ACTIVE_STATUSES.has(status);
}

/**
 * A run that has been going longer than the operator-configured long-running threshold
 * (`meta.overdueSeconds`, from `Alerting:LongRunningSeconds`).
 *
 * Deliberately `Running` only, not "any unfinished bar" — the alerting collector that owns the
 * threshold looks at Running alone. `Pending` (queued, not started) and `Paused` (sitting on a
 * breakpoint) are different conditions and would be dishonest to flag as "stuck" using a
 * threshold that was never meant for them.
 */
export function isOverdue(
  bar: Pick<TimelineBarInput, 'status' | 'startedAtMs' | 'completedAtMs'>,
  nowMs: number,
  overdueMs: number,
): boolean {
  if (bar.status !== 'Running' || bar.completedAtMs !== null) return false;
  return nowMs - bar.startedAtMs >= overdueMs;
}

/**
 * Pairs sub-workflow bars with their parent bar for the call connectors (trace-waterfall
 * lines from parent lane to child bar start). Children whose parent is not visible in the
 * current window/scope are skipped.
 */
export function pairCallConnectors(bars: PlacedBar[]): { parent: PlacedBar; child: PlacedBar }[] {
  const byExec = new Map(bars.map((b) => [b.executionId, b]));
  const pairs: { parent: PlacedBar; child: PlacedBar }[] = [];
  for (const child of bars) {
    if (!child.parentExecutionId) continue;
    const parent = byExec.get(child.parentExecutionId);
    if (parent) pairs.push({ parent, child });
  }
  return pairs;
}

/** Compact elapsed/duration label: "0:42", "4:12", "1:02:33". */
export function formatDuration(ms: number): string {
  const totalSec = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

/** Axis tick positions aligned to wall-clock multiples of `stepMs`, within [start..now]. */
export function axisTicks(
  w: TimelineWindow,
  stepMs: number = OPS_TICK_STEP_MS,
): { xPx: number; atMs: number }[] {
  const ticks: { xPx: number; atMs: number }[] = [];
  const first = Math.ceil(w.startMs / stepMs) * stepMs;
  for (let t = first; t <= w.nowMs; t += stepMs) {
    ticks.push({ xPx: timeToX(t, w), atMs: t });
  }
  return ticks;
}

/**
 * A live run whose most recent step finished longer ago than `stalledMs` — i.e. it is sitting on
 * one step, not making its way through many. This, not a percentage, is the honest answer to
 * "is it working or is it hung?": under Engine:DeferRunningStateWrite an in-flight step has no
 * row at all, so the only trustworthy signal is when something last FINISHED.
 *
 * Returns false when the activity data is absent (run not enriched, or nothing finished yet) —
 * unknown must never render as stalled.
 */
export function isStalled(
  bar: Pick<TimelineBarInput, 'status' | 'completedAtMs' | 'lastProgressAtMs'>,
  nowMs: number,
  stalledMs: number,
): boolean {
  if (bar.status !== 'Running' || bar.completedAtMs !== null) return false;
  if (bar.lastProgressAtMs === null) return false;
  return nowMs - bar.lastProgressAtMs >= stalledMs;
}
