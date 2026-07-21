import type { OpsNode, OpsRecentExecution, OpsRunningExecution } from '../types/api';
import type { LocalSettled } from '../stores/operationsStore';

// Pure geometry + lane allocation for the Mission-Control live timeline. Everything here is
// side-effect-free and driven entirely by the caller's clock (`nowMs`) so it stays trivially
// unit-testable — the rendering layer only maps the returned pixel values onto absolutely
// positioned divs.

/** Visible time span of the timeline. */
export const OPS_WINDOW_MS = 20 * 60_000;
/** Horizontal fraction of the track where the NOW line sits (right gutter = growing-bar room). */
export const OPS_NOW_FRACTION = 0.92;
/** Axis tick spacing. */
export const OPS_TICK_STEP_MS = 5 * 60_000;

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
 * - Bars entirely left of the window and out-of-scope workflows are dropped.
 */
export function buildTimelineBars(
  running: OpsRunningExecution[],
  recent: OpsRecentExecution[],
  locallySettled: Record<string, LocalSettled>,
  w: TimelineWindow,
  scopedWorkflowIds: Set<string>,
): TimelineBarInput[] {
  const bars: TimelineBarInput[] = [];
  const seen = new Set<string>();

  for (const r of recent) {
    if (!scopedWorkflowIds.has(r.workflowId)) continue;
    const completedAtMs = Date.parse(r.completedAt);
    if (completedAtMs < w.startMs) continue;
    seen.add(r.executionId);
    bars.push({
      executionId: r.executionId,
      workflowId: r.workflowId,
      status: r.status,
      startedAtMs: Date.parse(r.startedAt),
      completedAtMs,
      parentExecutionId: r.parentExecutionId,
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
    });
  }

  for (const [executionId, s] of Object.entries(locallySettled)) {
    if (seen.has(executionId) || !scopedWorkflowIds.has(s.workflowId)) continue;
    if (s.startedAtMs === null) continue; // never saw it running — nothing to draw
    if (s.settledAtMs < w.startMs) continue;
    seen.add(executionId);
    bars.push({
      executionId,
      workflowId: s.workflowId,
      status: s.status,
      startedAtMs: s.startedAtMs,
      completedAtMs: s.settledAtMs,
      parentExecutionId: null, // SignalR deltas carry no parent link; the snapshot fills it in
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
): { lanes: TimelineLane[]; placed: Omit<PlacedBar, 'leftPx' | 'widthPx' | 'clippedLeft'>[] } {
  const byWorkflow = new Map<string, TimelineBarInput[]>();
  for (const b of bars) {
    const list = byWorkflow.get(b.workflowId);
    if (list) list.push(b);
    else byWorkflow.set(b.workflowId, [b]);
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
      subRowCount: rowEnds.length,
      hasActive: lane.hasActive,
      depth,
    });
  });

  return { lanes, placed };
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
