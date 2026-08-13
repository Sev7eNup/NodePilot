import { useLayoutEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity } from '@carbon/icons-react';
import type { OpsDensityLane, OpsNode, OpsRecentExecution, OpsRunningExecution } from '../../types/api';
import type { LocalSettled } from '../../stores/operationsStore';
import { rawStatusLabelKey, STATUS_COLOR_VAR, STATUS_TEXT_CLASS } from '../../lib/statusTokens';
import {
  windowFor, buildTimelineBars, assignLanes, placeBar, axisTicks, timeToX, pairCallConnectors,
  buildDensityCells, densityColumnHeight, isActiveBarStatus, isOverdue, isStalled, tickStepFor,
  formatDuration, type DensityCell, type PlacedBar,
} from '../../lib/opsTimeline';
import { formatTime } from '../../lib/format';
import { OpsTimelineBar, OPS_ROW_H, OPS_MIN_BAR_PX, OPS_INSIDE_LABEL_PX } from './OpsTimelineBar';
import { OpsStuckStrip } from './OpsStuckStrip';
import { EmptyState } from '../common/EmptyState';
import { CopyableId } from '../common/CopyableId';

// The Mission-Control centerpiece: a real-time horizontal timeline. Running executions grow
// toward the NOW line; settled ones freeze and drift left out of the window. One lane per
// workflow, overlapping runs stack into sub-rows — and each sub-row gets its own labeled
// entry (full workflow name + that run's job id), so concurrent runs of the same workflow
// show as separate rows instead of a single name with a ×N badge. All geometry comes from
// lib/opsTimeline (pure, unit-tested); this component only measures the track and maps
// geometry onto divs.

const LANE_GAP = 8;
const LABEL_COL_PX = 380;
/** Room the out-of-bar duration text needs before it is worth drawing. */
const OUTSIDE_LABEL_PX = 46;
/** Distance from the bottom of a lane's first sub-row to the density baseline. */
const DENSITY_BASELINE_INSET = 6;

/** Height of the incident rug hanging under that baseline. */
const DENSITY_RUG_H = 3;

/**
 * Ink for the incident rug under a density slice, or `null` when the slice holds nothing to look
 * at. Failures win over cancellations: a cancelled run is an operator decision, a failed one is
 * the thing this whole view exists to surface. Both keep their exact counts in the tooltip — the
 * rug only answers "is there something here".
 *
 * This replaces a proportional outcome stack, which cannot survive the scale. Sixty-five failures
 * among 2981 runs is 2.2 %, i.e. a third of a pixel on a 14 px column: the stack either hid the
 * failure outright or, once floored to stay visible, claimed a share it did not have. Splitting
 * "how much ran" (the column, above the line) from "what went wrong" (the rug, below it) is the
 * only encoding that stays true at every ratio.
 */
function densityRugColor(cell: DensityCell): string | null {
  if (cell.failed > 0) return STATUS_COLOR_VAR.failed;
  if (cell.cancelled > 0) return STATUS_COLOR_VAR.cancelled;
  return null;
}

export function OpsTimeline({ nowMs, running, recent, density, locallySettled, scopedWorkflowIds, nodesById, selectedExecutionId, nextStart, overdueMs, windowMs, historyFromMs, recentSinceMs, densityBucketSeconds, densityCapped, onSelect }: Readonly<{
  nowMs: number;
  running: OpsRunningExecution[];
  recent: OpsRecentExecution[];
  /**
   * Bucketed run counts for the stretch the bars could not reach. Empty whenever `recent` already
   * covers the window, which is the normal case — density is what a busy system degrades to, not
   * a second rendering mode the view flips between.
   */
  density: OpsDensityLane[];
  locallySettled: Record<string, LocalSettled>;
  scopedWorkflowIds: Set<string>;
  nodesById: Map<string, OpsNode>;
  selectedExecutionId: string | null;
  /** Nearest upcoming trigger fire (for the idle hero), if any. */
  nextStart: { name: string; atMs: number } | null;
  /** Long-running threshold from the snapshot meta (Alerting:LongRunningSeconds). */
  overdueMs: number;
  /** Visible span of the track. */
  windowMs: number;
  /**
   * Oldest settled run the server actually returned — the seam between bars and density. When
   * nothing is left of it, everything left of it is "no history returned", not "nothing ran".
   */
  historyFromMs: number | null;
  /** Left edge the snapshot was built for; density bucket 0 starts here. */
  recentSinceMs: number;
  /** Width of one density bucket; 0 when the snapshot carries no density. */
  densityBucketSeconds: number;
  /** Density was computed off the newest N runs only — the counts are a floor, not a total. */
  densityCapped: boolean;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations', 'executions']);
  // The track element mounts/unmounts as the view flips between the idle hero and the lane
  // view, so the ResizeObserver must re-attach per element — a ref callback (state) instead
  // of a plain ref keys the effect on the CURRENT element; a one-shot [] effect would keep
  // observing a destroyed node and freeze the width at 0.
  const [trackEl, setTrackEl] = useState<HTMLDivElement | null>(null);
  const [trackWidth, setTrackWidth] = useState(0);

  useLayoutEffect(() => {
    if (!trackEl) return;
    const observer = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0;
      setTrackWidth(w);
    });
    observer.observe(trackEl);
    setTrackWidth(trackEl.getBoundingClientRect().width);
    return () => observer.disconnect();
  }, [trackEl]);

  const w = useMemo(() => windowFor(nowMs, windowMs, trackWidth), [nowMs, windowMs, trackWidth]);

  // Local-clock timestamp of the snapshot currently on screen. Deliberately NOT the server's
  // `recentSinceUtc`: the anchored layer below would inherit any browser/server clock skew as a raw
  // pixel offset. The translation would still land every bar in the right place, but it would first
  // put it thousands of pixels outside the layer, and a browser neither renders nor hit-tests that
  // reliably — a snapshot stamped at the epoch pushed the bars far enough out that they could not
  // even be clicked. Captured from `nowMs` when the data changes instead, which bounds the offset
  // by the poll interval no matter what clock the server keeps.
  //
  // Set during render: React's documented way to adjust state when a prop changes. It re-runs this
  // component before committing, and it costs nothing extra here because a new `recent` already
  // re-renders every bar. `recent` keeps its identity across polls that changed nothing (React
  // Query's structural sharing), so a quiet board does not re-anchor at all.
  const [snapshot, setSnapshot] = useState({ key: recent, atMs: nowMs });
  if (snapshot.key !== recent) setSnapshot({ key: recent, atMs: nowMs });

  // Anchor window: the coordinate system the settled layer is drawn in. Moves once per snapshot,
  // never on a clock tick — that is what lets thousands of settled bars keep their memoized
  // geometry, and their DOM, between ticks.
  const wAnchor = useMemo(
    () => windowFor(snapshot.atMs, windowMs, trackWidth),
    [snapshot.atMs, windowMs, trackWidth],
  );

  // How far the anchored layer has drifted since the snapshot, in px — never more than one poll
  // interval's worth. Both windows share a span, so the difference between them is a pure
  // translation: one number for the whole layer, and the only thing a clock tick has to change.
  // Applied as a transform (compositor work) instead of rewriting `left` on every bar (layout
  // work, once per second, thousands of times).
  const shiftPx = useMemo(() => {
    const span = w.endMs - w.startMs;
    if (span <= 0 || trackWidth <= 0) return 0;
    return ((wAnchor.startMs - w.startMs) / span) * trackWidth;
  }, [wAnchor.startMs, w.startMs, w.endMs, trackWidth]);

  // Density arrives keyed by workflow; scope it once and reuse the key set for lane allocation.
  const densityByWorkflow = useMemo(() => {
    const map = new Map<string, OpsDensityLane['buckets']>();
    for (const lane of density) {
      if (scopedWorkflowIds.has(lane.workflowId)) map.set(lane.workflowId, lane.buckets);
    }
    return map;
  }, [density, scopedWorkflowIds]);

  // Bar building and lane allocation depend on the SNAPSHOT only — no window, no clock. Before
  // this they hung off `w` and therefore re-ran every second: ~8000 Date.parse calls on ISO
  // strings, a fresh object per bar, then a full group-sort-and-stack pass over all of them. None
  // of that can change between two polls, so none of it belongs on the tick.
  const { lanes, placed } = useMemo(() => {
    const bars = buildTimelineBars(running, recent, locallySettled, scopedWorkflowIds);
    return assignLanes(bars, nodesById, new Set(densityByWorkflow.keys()));
  }, [running, recent, locallySettled, scopedWorkflowIds, nodesById, densityByWorkflow]);

  // Vertical offsets: lanes stack; each lane is subRowCount rows tall.
  const laneTops = useMemo(() => {
    const tops: number[] = [];
    let y = 0;
    for (const lane of lanes) {
      tops.push(y);
      y += lane.subRowCount * OPS_ROW_H + LANE_GAP;
    }
    return { tops, totalHeight: Math.max(y - LANE_GAP, 0) };
  }, [lanes]);

  // Anchor-space geometry for everything. Recomputed per snapshot, not per tick.
  const placedBars: PlacedBar[] = useMemo(
    () => placed.map((p) => placeBar(p, wAnchor)),
    [placed, wAnchor],
  );

  // Live-space geometry for the only bars whose SHAPE changes between ticks: a running bar grows
  // toward the NOW line, so it cannot ride the anchored layer — a translation would slide its
  // right edge away from NOW instead of extending it. There is a handful of these next to
  // thousands of settled ones, so recomputing them every tick costs nothing.
  const activeBars: PlacedBar[] = useMemo(
    () => placed.filter((p) => isActiveBarStatus(p.status)).map((p) => placeBar(p, w)),
    [placed, w],
  );

  const settledBars = useMemo(
    () => placedBars.filter((b) => !isActiveBarStatus(b.status)),
    [placedBars],
  );

  // Representative execution (job) id per lane sub-row for the copyable chip next to the
  // workflow name: each overlapping run gets its own row label, so we key by (laneIndex,
  // subRow) and pick the most relevant bar on that row — prefer an active run, else the most
  // recently started one. (A sub-row can hold several sequential, non-overlapping runs; the
  // chip names the live/latest one. Every bar stays clickable for its own drilldown.)
  const rowExecId = useMemo(() => {
    const best = new Map<string, { id: string; active: boolean; startedAtMs: number }>();
    for (const b of placed) {
      const key = `${b.laneIndex}:${b.subRow}`;
      const active = isActiveBarStatus(b.status);
      const prev = best.get(key);
      if (!prev || (active && !prev.active) || (active === prev.active && b.startedAtMs > prev.startedAtMs)) {
        best.set(key, { id: b.executionId, active, startedAtMs: b.startedAtMs });
      }
    }
    return best;
  }, [placed]);

  // Call connectors: elbow lines from the parent bar down/up to each sub-workflow bar's start.
  // Anchor space, and drawn inside the anchored layer: an elbow attaches to bar STARTS, which do
  // not move relative to each other, so the layer's translation carries them exactly right — a
  // connector into a running bar lands on the same pixel as the live-placed bar it points at.
  const connectors = useMemo(() => pairCallConnectors(placedBars), [placedBars]);

  // Overdue set, computed once and shared by the bars and the strip so the two can never
  // disagree about which runs are stuck.
  const overdueIds = useMemo(
    () => new Set(activeBars.filter((b) => isOverdue(b, nowMs, overdueMs)).map((b) => b.executionId)),
    [activeBars, nowMs, overdueMs],
  );
  const overdueBars = useMemo(
    () => activeBars.filter((b) => overdueIds.has(b.executionId)),
    [activeBars, overdueIds],
  );

  // Stalled reuses the overdue threshold: "no step finished for as long as a whole run is
  // allowed to take" is the same operator judgement, applied to progress instead of age.
  const stalledIds = useMemo(
    () => new Set(activeBars.filter((b) => isStalled(b, nowMs, overdueMs)).map((b) => b.executionId)),
    [activeBars, nowMs, overdueMs],
  );

  // Duration written NEXT TO bars too narrow to hold it inside. Without this, wide windows
  // compress every run toward the minimum bar width and "which one took longer?" becomes
  // unanswerable — the bar length alone cannot carry it at 4 h.
  //
  // Only when the gap to the next bar on the SAME sub-row can hold the text, so labels never
  // overlap a following run. Bars on other rows are irrelevant: each sub-row is its own line.
  const outsideLabelIds = useMemo(() => {
    const rows = new Map<string, PlacedBar[]>();
    for (const b of placedBars) {
      const key = `${b.laneIndex}:${b.subRow}`;
      const list = rows.get(key);
      if (list) list.push(b);
      else rows.set(key, [b]);
    }
    const ids = new Set<string>();
    for (const list of rows.values()) {
      const sorted = [...list].sort((a, b) => a.leftPx - b.leftPx);
      sorted.forEach((b, i) => {
        if (b.widthPx >= OPS_INSIDE_LABEL_PX) return; // already labelled inside
        const end = b.leftPx + Math.max(b.widthPx, OPS_MIN_BAR_PX);
        const nextStart = sorted[i + 1]?.leftPx ?? trackWidth;
        if (nextStart - end >= OUTSIDE_LABEL_PX) ids.add(b.executionId);
      });
    }
    return ids;
  }, [placedBars, trackWidth]);

  const ticks = useMemo(() => axisTicks(w, tickStepFor(windowMs)), [w, windowMs]);

  // Density cells per lane, covering the stretch left of the bar/aggregate seam.
  //
  // Anchor space, like the settled bars, and for the same reason: density IS frozen history, so
  // none of this can change between two polls. It used to hang off `w` and therefore off the clock,
  // which at the 4 h window meant recomputing and re-rendering ~1150 cells plus their rugs and
  // baselines every single second while the bars beside them sat perfectly still.
  //
  // The seam falls back to the anchor's own NOW rather than the live clock. That branch is in fact
  // unreachable whenever there is density to draw — the server only sends `density[]` when the bar
  // cap bit, which presupposes settled runs came back, which makes `oldestReturnedCompletedAt`
  // non-null — but reading the clock here would put the whole memo back on the tick for nothing.
  const densityCellsByLane = useMemo(() => {
    const seam = historyFromMs ?? wAnchor.nowMs;
    const map = new Map<string, DensityCell[]>();
    for (const [workflowId, buckets] of densityByWorkflow) {
      const cells = buildDensityCells(buckets, recentSinceMs, densityBucketSeconds, seam, wAnchor);
      if (cells.length > 0) map.set(workflowId, cells);
    }
    return map;
  }, [densityByWorkflow, recentSinceMs, densityBucketSeconds, historyFromMs, wAnchor]);

  // One baseline per density lane, spanning exactly the stretch its columns cover. Dashed, so the
  // aggregate reads as a chart axis rather than as another track element — and because it stops at
  // the seam, it doubles as the marker for where individual runs take over.
  const densityAxes = useMemo(() => {
    const axes = new Map<string, { leftPx: number; widthPx: number }>();
    for (const [workflowId, cells] of densityCellsByLane) {
      let left = Number.POSITIVE_INFINITY;
      let right = 0;
      for (const c of cells) {
        if (c.leftPx < left) left = c.leftPx;
        if (c.leftPx + c.widthPx > right) right = c.leftPx + c.widthPx;
      }
      axes.set(workflowId, { leftPx: left, widthPx: Math.max(right - left, 1) });
    }
    return axes;
  }, [densityCellsByLane]);

  // Busiest slice on the board — the reference every column height is scaled against, so "taller"
  // reliably means "more runs" both within a lane and across lanes.
  const densityPeak = useMemo(() => {
    let peak = 0;
    for (const cells of densityCellsByLane.values()) {
      for (const c of cells) if (c.total > peak) peak = c.total;
    }
    return peak;
  }, [densityCellsByLane]);

  // Window-wide totals for the notice line. Summed over every bucket, INCLUDING the ones right of
  // the seam that are not drawn — the server buckets the whole window precisely so this number is
  // the honest answer to "how much ran here?", not just "how much was aggregated".
  const densitySummary = useMemo(() => {
    let runs = 0;
    let failed = 0;
    for (const buckets of densityByWorkflow.values()) {
      for (const b of buckets) { runs += b.total; failed += b.failed; }
    }
    return { runs, failed };
  }, [densityByWorkflow]);

  // Width of the "no history returned" band at the left edge, in px. Anchored on the oldest
  // row the server actually sent — NOT on the requested window edge: when the cap bit, the
  // requested edge would mark the one stretch truncation did not lose.
  //
  // Suppressed once density is present: the band claims "nothing came back for this stretch",
  // and the density strip is exactly the refutation of that claim.
  //
  // Anchor space too, and drawn in the anchored layer: like the bars and the density it covers, it
  // is a statement about history and does not change between polls.
  const historyGapPx = useMemo(() => {
    if (historyFromMs === null || densityCellsByLane.size > 0) return 0;
    const x = timeToX(historyFromMs, wAnchor);
    return x > 2 ? Math.min(x, wAnchor.trackWidthPx) : 0;
  }, [historyFromMs, densityCellsByLane, wAnchor]);
  const nowX = timeToX(nowMs, w);

  /**
   * Accessible names, one entry per DISTINCT (workflow, status) pair rather than per bar.
   *
   * The caller needs a label for every bar, and building it inline meant an i18next lookup and a
   * template string per bar: at a full bar cap, 4000 of each per snapshot for at most lanes x
   * status distinct results. Resolved up front here instead, so `t()` runs once per status.
   *
   * Built eagerly rather than filled on demand. A lazily populated cache would be mutated after
   * render, which the React Compiler rejects outright -- and rightly, since it makes what a render
   * produces depend on which renders came before it.
   */
  const labelByWorkflowStatus = useMemo(() => {
    const map = new Map<string, string>();
    for (const b of placed) {
      const key = `${b.workflowId} ${b.status}`;
      if (map.has(key)) continue;
      const statusKey = rawStatusLabelKey(b.status);
      const name = nodesById.get(b.workflowId)?.name ?? b.workflowId;
      map.set(key, `${name} · ${statusKey ? t(`executions:status.${statusKey}`) : b.status}`);
    }
    return map;
  }, [placed, nodesById, t]);

  /**
   * Keyboard order over every bar on the board: lanes top to bottom, bars left to right inside a
   * lane. Derived from anchor-space geometry, so it survives a clock tick like everything else here.
   */
  const navBars = useMemo(() => {
    const seen = new Set(placedBars.map((b) => b.executionId));
    const all = [...placedBars, ...activeBars.filter((a) => !seen.has(a.executionId))];
    return all.sort((a, b) => (a.laneIndex - b.laneIndex) || (a.leftPx - b.leftPx));
  }, [placedBars, activeBars]);

  /**
   * Where the track's roving focus points.
   *
   * The track is ONE tab stop and moves `aria-activedescendant` across the bars instead of making
   * every bar tabbable. Thousands of tab stops is a keyboard trap — nothing below the timeline would
   * be reachable — and a per-bar `tabIndex` would be a prop, so every arrow key would rebuild the
   * whole memoized anchored subtree. Keeping the pointer out here costs one DOM focus() call.
   */
  const [activeBarId, setActiveBarId] = useState<string | null>(null);
  const focusedBar = navBars.find((b) => b.executionId === activeBarId) ?? navBars[0];

  const focusBarAt = (index: number) => {
    const target = navBars[index];
    if (!target) return;
    setActiveBarId(target.executionId);
    trackEl?.querySelector<HTMLElement>(`#ops-bar-${CSS.escape(target.executionId)}`)?.focus();
  };

  const onTrackKeyDown = (e: React.KeyboardEvent) => {
    if (navBars.length === 0) return;
    const at = Math.max(0, navBars.findIndex((b) => b.executionId === focusedBar?.executionId));
    switch (e.key) {
      case 'ArrowRight': focusBarAt(Math.min(navBars.length - 1, at + 1)); break;
      case 'ArrowLeft': focusBarAt(Math.max(0, at - 1)); break;
      case 'ArrowDown':
      case 'ArrowUp': {
        // Lane-wise: jump to the first bar of the neighbouring lane rather than crawling through
        // every bar of this one.
        const lane = navBars[at].laneIndex + (e.key === 'ArrowDown' ? 1 : -1);
        const first = navBars.findIndex((b) => b.laneIndex === lane);
        if (first !== -1) focusBarAt(first);
        break;
      }
      case 'Home': focusBarAt(0); break;
      case 'End': focusBarAt(navBars.length - 1); break;
      case 'Enter':
      case ' ':
        if (focusedBar) onSelect(focusedBar.executionId);
        break;
      default: return;
    }
    e.preventDefault();
  };

  /**
   * Everything the anchored layer draws, as ONE memoized subtree.
   *
   * Memoizing the geometry was only half the job: the JSX itself lived in the render body, so a
   * clock tick still rebuilt ~1150 density elements and thousands of bar elements a second and
   * re-ran `densityTitle` for every cell — two `toLocaleTimeString` calls and up to three i18n
   * lookups apiece, none of which can change between polls. The helpers below therefore live
   * INSIDE the memo: as plain per-render closures they would be new on every render and this memo
   * would never hold. Nothing here may depend on `nowMs`, `w` or `shiftPx` — that is the invariant
   * the whole two-layer split rests on, and a test pins it.
   */
  const anchoredLayer = useMemo(() => {
    const clockLabel = (ms: number) => formatTime(ms, { hour: '2-digit', minute: '2-digit' });

    const rowCenterY = (bar: PlacedBar) =>
      laneTops.tops[bar.laneIndex] + bar.subRow * OPS_ROW_H + OPS_ROW_H / 2;

    // Failed/cancelled are appended only when non-zero: a slice that reads "· 0 failed" trains the
    // eye to skip the very part of the label that matters when it is not zero.
    const densityTitle = (cell: DensityCell) => [
      t('operations:timeline.densityCell', {
        from: clockLabel(cell.fromMs),
        to: clockLabel(cell.toMs),
        runs: cell.total,
      }),
      cell.failed > 0 ? t('operations:timeline.densityCellFailed', { count: cell.failed }) : null,
      cell.cancelled > 0 ? t('operations:timeline.densityCellCancelled', { count: cell.cancelled }) : null,
    ].filter(Boolean).join(' · ');

    return (
      <>
        {/* No-history band: the server returned nothing settled before this point. Drawn
            so an empty stretch of track is never misread as "nothing ran here". */}
        {historyGapPx > 0 && (
          <div
            className="np-ops-nohistory"
            style={{ width: historyGapPx }}
            title={t('operations:timeline.historyGap', {
              time: formatTime(historyFromMs!, { hour: '2-digit', minute: '2-digit' }),
            })}
            data-testid="ops-history-gap"
          />
        )}
        {/* Density: what the window holds where individual bars ran out. A bottom-anchored
            column chart, not a block — column height is the run count, the dashed rule beneath
            it is the baseline, and a slice where something went wrong carries a rug BELOW that
            baseline. Two marks rather than one stacked bar because no proportional split
            survives this scale: 65 failures among 2981 runs is 2.2 %, a third of a pixel on a
            14 px column. */}
        {lanes.flatMap((lane, i) => {
          const cells = densityCellsByLane.get(lane.workflowId);
          const axis = densityAxes.get(lane.workflowId);
          if (!cells || !axis) return [];
          // Anchored on the FIRST sub-row's bottom, not the lane's: a constant band height
          // keeps columns comparable across lanes, and a lane only grows extra sub-rows from
          // overlapping bars, which live right of the seam where density never draws.
          const baselineY = laneTops.tops[i] + OPS_ROW_H - DENSITY_BASELINE_INSET;
          return [
            <div
              key={`dens-axis-${lane.workflowId}`}
              className="np-ops-density-axis"
              style={{ left: axis.leftPx, width: axis.widthPx, top: baselineY }}
              aria-hidden="true"
              data-testid="ops-density-axis"
            />,
            ...cells.flatMap((cell) => {
              const h = densityColumnHeight(cell.total, densityPeak);
              const rug = densityRugColor(cell);
              const title = densityTitle(cell);
              const marks = [
                <div
                  key={`dens-${lane.workflowId}#${cell.bucketIndex}`}
                  className="np-ops-density"
                  style={{ left: cell.leftPx, width: cell.widthPx, top: baselineY - h, height: h }}
                  title={title}
                  // A div with a `title` is invisible to a screen reader, and this column carries
                  // real information: how much ran in that slice. It is announced, but stays out of
                  // the tab order — there is nothing to activate, only something to read.
                  role="img"
                  aria-label={title}
                  data-testid="ops-density-cell"
                />,
              ];
              if (rug !== null) {
                marks.push(
                  <div
                    key={`dens-rug-${lane.workflowId}#${cell.bucketIndex}`}
                    className="np-ops-density-rug"
                    style={{
                      left: cell.leftPx,
                      width: cell.widthPx,
                      top: baselineY + 1,
                      height: DENSITY_RUG_H,
                      background: rug,
                    }}
                    title={title}
                    data-testid="ops-density-rug"
                  />,
                );
              }
              return marks;
            }),
          ];
        })}
        {/* Call connectors: parent run → sub-workflow run (trace-waterfall elbows) */}
        {connectors.length > 0 && (
          <svg
            className="pointer-events-none absolute inset-0 h-full w-full"
            aria-hidden="true"
            data-testid="ops-connectors"
          >
            {connectors.map(({ parent, child }) => {
              const x = Math.max(child.leftPx, 1);
              const y1 = rowCenterY(parent);
              const y2 = rowCenterY(child);
              return (
                <g key={`${parent.executionId}-${child.executionId}`} className="np-ops-connector">
                  <path d={`M ${x} ${y1} L ${x} ${y2} l 6 0`} />
                  <circle cx={x} cy={y1} r={2.5} />
                </g>
              );
            })}
          </svg>
        )}

        {settledBars.map((bar) => (
          <OpsTimelineBar
            key={bar.executionId}
            bar={bar}
            topPx={laneTops.tops[bar.laneIndex]}
            durationMs={bar.completedAtMs! - bar.startedAtMs}
            selected={bar.executionId === selectedExecutionId}
            label={labelByWorkflowStatus.get(`${bar.workflowId} ${bar.status}`) ?? bar.workflowId}
            overdue={false}
            stalled={false}
            onSelect={onSelect}
          />
        ))}

        {settledBars.filter((b) => outsideLabelIds.has(b.executionId)).map((bar) => (
          <span
            key={`dur-${bar.executionId}`}
            className="np-ops-bar-outside tabular-nums"
            style={{
              left: bar.leftPx + Math.max(bar.widthPx, OPS_MIN_BAR_PX) + 4,
              top: laneTops.tops[bar.laneIndex] + bar.subRow * OPS_ROW_H,
              height: OPS_ROW_H,
            }}
            aria-hidden="true"
          >
            {formatDuration(bar.completedAtMs! - bar.startedAtMs)}
          </span>
        ))}
      </>
    );
  }, [
    historyGapPx, historyFromMs, lanes, densityCellsByLane, densityAxes, densityPeak, laneTops,
    connectors, settledBars, outsideLabelIds, labelByWorkflowStatus, selectedExecutionId, onSelect, t,
  ]);

  if (lanes.length === 0) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-2 px-6 text-center">
        <EmptyState
          icon={<Activity size={22} />}
          title={t('operations:timeline.idle')}
          hint={t('operations:timeline.idleHint')}
        />
        {nextStart && (
          <p className="text-sm text-on-surface-variant">
            {t('operations:timeline.nextStart', {
              time: formatTime(nextStart.atMs, { hour: '2-digit', minute: '2-digit' }),
              name: nextStart.name,
            })}
          </p>
        )}
      </div>
    );
  }

  return (
    <div className="np-ops-timeline flex h-full min-h-0 flex-col gap-2" data-testid="ops-timeline">
      <OpsStuckStrip
        bars={overdueBars}
        nowMs={nowMs}
        nameFor={(id) => nodesById.get(id)?.name ?? id}
        onSelect={onSelect}
      />
      {densityCellsByLane.size > 0 && (
        <p className={`shrink-0 text-xs ${STATUS_TEXT_CLASS.warning}`} data-testid="ops-density-notice">
          {t(densityCapped ? 'operations:timeline.densityCapped' : 'operations:timeline.density', {
            runs: densitySummary.runs,
            failed: densitySummary.failed,
          })}
        </p>
      )}
      <div className="flex min-h-0 flex-1 overflow-y-auto">
        {/* Lane labels */}
        <div className="shrink-0 border-r border-outline-variant/60" style={{ width: LABEL_COL_PX }}>
          <div className="relative" style={{ height: laneTops.totalHeight }}>
            {lanes.map((lane, i) => (
              <div
                key={`bg-${lane.workflowId}`}
                className={`np-ops-lane-bg${i % 2 === 1 ? ' np-ops-lane-bg--alt' : ''}`}
                style={{ top: laneTops.tops[i], height: lane.subRowCount * OPS_ROW_H }}
                aria-hidden="true"
              />
            ))}
            {lanes.flatMap((lane, i) =>
              Array.from({ length: lane.subRowCount }, (_, r) => r).map((r) => {
                const idKey = `${i}:${r}`;
                return (
                  <div
                    key={`${lane.workflowId}#${r}`}
                    className="absolute right-0 flex flex-col justify-start gap-0 overflow-hidden pr-3 pt-[4px]"
                    style={{
                      top: laneTops.tops[i] + r * OPS_ROW_H,
                      height: OPS_ROW_H,
                      left: Math.min(lane.depth, 3) * 14,
                    }}
                  >
                    <div
                      className={`flex items-center gap-1 text-[13px] font-label font-medium leading-[16px] ${lane.hasActive ? 'text-on-surface' : 'text-on-surface-variant'}`}
                      title={lane.name}
                    >
                      {lane.depth > 0 && <span className="shrink-0 text-outline" aria-hidden="true">↳</span>}
                      <span className="whitespace-nowrap">{lane.name}</span>
                      {rowExecId.has(idKey) && <CopyableId id={rowExecId.get(idKey)!.id} />}
                    </div>
                    {r === 0 && lane.folderPath !== '/' && (
                      <div className="truncate text-[11px] leading-[14px] text-on-surface-variant" title={lane.folderPath}>{lane.folderPath}</div>
                    )}
                  </div>
                );
              }),
            )}
          </div>
        </div>

        {/* Track */}
        <div
          ref={setTrackEl}
          className="np-ops-track relative min-w-0 flex-1"
          data-testid="ops-track"
          role="grid"
          tabIndex={0}
          aria-label={t('operations:timeline.trackLabel')}
          aria-activedescendant={focusedBar ? `ops-bar-${focusedBar.executionId}` : undefined}
          onKeyDown={onTrackKeyDown}
        >
          <div className="relative" style={{ height: laneTops.totalHeight }}>
            {/* Zebra lane backgrounds — group parallel sub-rows visually under their lane */}
            {lanes.map((lane, i) => (
              <div
                key={`bg-${lane.workflowId}`}
                className={`np-ops-lane-bg${i % 2 === 1 ? ' np-ops-lane-bg--alt' : ''}`}
                style={{ top: laneTops.tops[i], height: lane.subRowCount * OPS_ROW_H }}
                aria-hidden="true"
              />
            ))}
            {/* Gridlines */}
            {ticks.map((tick) => (
              <div key={tick.atMs} className="np-ops-gridline" style={{ left: tick.xPx }} aria-hidden="true" />
            ))}
            {/* NOW line */}
            <div className="np-ops-now" style={{ left: nowX }} aria-hidden="true" />

            {/* Anchored layer: everything whose geometry was frozen at the snapshot. Between polls
                the ONLY thing that changes is the inner layer's translation — the subtree itself is
                memoized (see anchoredLayer), so React does not even rebuild the elements.
                Two elements, not one: the outer div clips and stays put, the inner one moves. Sharing
                both roles would drag the clip rectangle along with the transform and shave a few
                pixels off the right edge. */}
            <div className="np-ops-clip">
              <div
                className="np-ops-shift"
                style={{ transform: `translateX(${shiftPx}px)` }}
                data-testid="ops-shift-layer"
              >
                {anchoredLayer}
              </div>
            </div>

            {/* Live layer: running bars grow toward NOW every tick, so they are placed against the
                live window and stay outside the anchored layer. A handful, not thousands. */}
            {activeBars.map((bar) => (
              <OpsTimelineBar
                key={bar.executionId}
                bar={bar}
                topPx={laneTops.tops[bar.laneIndex]}
                durationMs={nowMs - bar.startedAtMs}
                selected={bar.executionId === selectedExecutionId}
                label={labelByWorkflowStatus.get(`${bar.workflowId} ${bar.status}`) ?? bar.workflowId}
                overdue={overdueIds.has(bar.executionId)}
                stalled={stalledIds.has(bar.executionId)}
                onSelect={onSelect}
              />
            ))}

            {/* Duration beside bars too narrow to hold it — keeps runs comparable at 1 h / 4 h. */}
            {activeBars.filter((b) => outsideLabelIds.has(b.executionId)).map((bar) => (
              <span
                key={`dur-${bar.executionId}`}
                className="np-ops-bar-outside tabular-nums"
                style={{
                  left: bar.leftPx + Math.max(bar.widthPx, OPS_MIN_BAR_PX) + 4,
                  top: laneTops.tops[bar.laneIndex] + bar.subRow * OPS_ROW_H,
                  height: OPS_ROW_H,
                }}
                aria-hidden="true"
              >
                {formatDuration((bar.completedAtMs ?? nowMs) - bar.startedAtMs)}
              </span>
            ))}
          </div>
        </div>
      </div>

      {/* Time axis */}
      <div className="relative mt-1 h-5 shrink-0 border-t border-outline-variant/60" style={{ marginLeft: LABEL_COL_PX }}>
        {ticks.map((tick) => (
          <span
            key={tick.atMs}
            className="absolute -translate-x-1/2 text-[10px] tabular-nums text-outline"
            style={{ left: tick.xPx }}
          >
            {formatTime(tick.atMs, { hour: '2-digit', minute: '2-digit' })}
          </span>
        ))}
        <span
          className="absolute -translate-x-1/2 text-[10px] font-semibold uppercase text-primary"
          style={{ left: nowX }}
        >
          {t('operations:timeline.now')}
        </span>
      </div>
    </div>
  );
}
