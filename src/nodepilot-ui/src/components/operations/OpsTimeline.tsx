import { useLayoutEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity } from '@carbon/icons-react';
import type { OpsDensityLane, OpsNode, OpsRecentExecution, OpsRunningExecution } from '../../types/api';
import type { LocalSettled } from '../../stores/operationsStore';
import { rawStatusLabelKey, STATUS_COLOR_VAR, STATUS_TEXT_CLASS } from '../../lib/statusTokens';
import {
  windowFor, buildTimelineBars, assignLanes, placeBar, axisTicks, timeToX, pairCallConnectors,
  buildDensityCells, isActiveBarStatus, isOverdue, isStalled, tickStepFor, formatDuration,
  type DensityCell, type PlacedBar,
} from '../../lib/opsTimeline';
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
/** Vertical padding of a density slice inside its lane, so lanes stay visually separable. */
const DENSITY_INSET = 5;

/**
 * Vertical stack of a density slice's outcomes: failures at the bottom, cancellations above them,
 * successes filling the rest — each sized by its share of the slice. A single "worst status"
 * colour was the obvious alternative and is a lie in both directions: it paints nineteen good
 * runs red, or hides one bad run among them.
 */
function densityBackground(cell: DensityCell): string {
  const failedPct = (cell.failed / cell.total) * 100;
  const cancelledPct = ((cell.failed + cell.cancelled) / cell.total) * 100;
  if (cancelledPct === 0) return STATUS_COLOR_VAR.success;
  return `linear-gradient(to top,`
    + ` ${STATUS_COLOR_VAR.failed} 0 ${failedPct}%,`
    + ` ${STATUS_COLOR_VAR.cancelled} ${failedPct}% ${cancelledPct}%,`
    + ` ${STATUS_COLOR_VAR.success} ${cancelledPct}% 100%)`;
}

/**
 * Run count → ink. Scaled against the busiest slice in the same snapshot rather than an absolute
 * number, because "busy" only means anything relative to the rest of what is on screen. Floored
 * well above zero so a quiet slice stays visible: the difference that matters most is between
 * "few runs" and "no runs at all".
 */
function densityOpacity(total: number, peak: number): number {
  if (peak <= 0) return 0.85;
  return 0.28 + 0.57 * (total / peak);
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

  // Density arrives keyed by workflow; scope it once and reuse the key set for lane allocation.
  const densityByWorkflow = useMemo(() => {
    const map = new Map<string, OpsDensityLane['buckets']>();
    for (const lane of density) {
      if (scopedWorkflowIds.has(lane.workflowId)) map.set(lane.workflowId, lane.buckets);
    }
    return map;
  }, [density, scopedWorkflowIds]);

  const { lanes, placed } = useMemo(() => {
    const bars = buildTimelineBars(running, recent, locallySettled, w, scopedWorkflowIds);
    return assignLanes(bars, nodesById, new Set(densityByWorkflow.keys()));
  }, [running, recent, locallySettled, w, scopedWorkflowIds, nodesById, densityByWorkflow]);

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

  const placedBars: PlacedBar[] = useMemo(
    () => placed.map((p) => placeBar(p, w)),
    [placed, w],
  );

  // Representative execution (job) id per lane sub-row for the copyable chip next to the
  // workflow name: each overlapping run gets its own row label, so we key by (laneIndex,
  // subRow) and pick the most relevant bar on that row — prefer an active run, else the most
  // recently started one. (A sub-row can hold several sequential, non-overlapping runs; the
  // chip names the live/latest one. Every bar stays clickable for its own drilldown.)
  const rowExecId = useMemo(() => {
    const best = new Map<string, { id: string; active: boolean; startedAtMs: number }>();
    for (const b of placedBars) {
      const key = `${b.laneIndex}:${b.subRow}`;
      const active = isActiveBarStatus(b.status);
      const prev = best.get(key);
      if (!prev || (active && !prev.active) || (active === prev.active && b.startedAtMs > prev.startedAtMs)) {
        best.set(key, { id: b.executionId, active, startedAtMs: b.startedAtMs });
      }
    }
    return best;
  }, [placedBars]);

  // Call connectors: elbow lines from the parent bar down/up to each sub-workflow bar's start.
  const connectors = useMemo(() => pairCallConnectors(placedBars), [placedBars]);

  // Overdue set, computed once and shared by the bars and the strip so the two can never
  // disagree about which runs are stuck.
  const overdueIds = useMemo(
    () => new Set(placedBars.filter((b) => isOverdue(b, nowMs, overdueMs)).map((b) => b.executionId)),
    [placedBars, nowMs, overdueMs],
  );
  const overdueBars = useMemo(
    () => placedBars.filter((b) => overdueIds.has(b.executionId)),
    [placedBars, overdueIds],
  );

  // Stalled reuses the overdue threshold: "no step finished for as long as a whole run is
  // allowed to take" is the same operator judgement, applied to progress instead of age.
  const stalledIds = useMemo(
    () => new Set(placedBars.filter((b) => isStalled(b, nowMs, overdueMs)).map((b) => b.executionId)),
    [placedBars, nowMs, overdueMs],
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
        const nextStart = sorted[i + 1]?.leftPx ?? w.trackWidthPx;
        if (nextStart - end >= OUTSIDE_LABEL_PX) ids.add(b.executionId);
      });
    }
    return ids;
  }, [placedBars, w.trackWidthPx]);

  const ticks = useMemo(() => axisTicks(w, tickStepFor(windowMs)), [w, windowMs]);

  // Density cells per lane, covering the stretch left of the bar/aggregate seam.
  const densityCellsByLane = useMemo(() => {
    const seam = historyFromMs ?? nowMs;
    const map = new Map<string, DensityCell[]>();
    for (const [workflowId, buckets] of densityByWorkflow) {
      const cells = buildDensityCells(buckets, recentSinceMs, densityBucketSeconds, seam, w);
      if (cells.length > 0) map.set(workflowId, cells);
    }
    return map;
  }, [densityByWorkflow, recentSinceMs, densityBucketSeconds, historyFromMs, nowMs, w]);

  // Busiest slice on screen — the reference the per-cell opacity is scaled against, so "darker"
  // reliably means "more runs" within one snapshot.
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
  const historyGapPx = useMemo(() => {
    if (historyFromMs === null || densityCellsByLane.size > 0) return 0;
    const x = timeToX(historyFromMs, w);
    return x > 2 ? Math.min(x, w.trackWidthPx) : 0;
  }, [historyFromMs, densityCellsByLane, w]);
  const nowX = timeToX(nowMs, w);

  const rowCenterY = (bar: PlacedBar) =>
    laneTops.tops[bar.laneIndex] + bar.subRow * OPS_ROW_H + OPS_ROW_H / 2;

  const statusLabel = (status: string) => {
    const key = rawStatusLabelKey(status);
    return key ? t(`executions:status.${key}`) : status;
  };

  const clockLabel = (ms: number) =>
    new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

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
              time: new Date(nextStart.atMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
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
        <div ref={setTrackEl} className="np-ops-track relative min-w-0 flex-1" data-testid="ops-track">
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
            {/* No-history band: the server returned nothing settled before this point. Drawn
                so an empty stretch of track is never misread as "nothing ran here". */}
            {historyGapPx > 0 && (
              <div
                className="np-ops-nohistory"
                style={{ width: historyGapPx }}
                title={t('operations:timeline.historyGap', {
                  time: new Date(historyFromMs!).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
                })}
                data-testid="ops-history-gap"
              />
            )}
            {/* Density: what the window holds where individual bars ran out. One slice per
                (lane, bucket), stacked by outcome so a slice with a single failure among twenty
                successes cannot render as either all-green or all-red. */}
            {lanes.flatMap((lane, i) =>
              (densityCellsByLane.get(lane.workflowId) ?? []).map((cell) => (
                <div
                  key={`dens-${lane.workflowId}#${cell.bucketIndex}`}
                  className="np-ops-density"
                  style={{
                    left: cell.leftPx,
                    width: cell.widthPx,
                    top: laneTops.tops[i] + DENSITY_INSET,
                    height: Math.max(lane.subRowCount * OPS_ROW_H - 2 * DENSITY_INSET, 1),
                    background: densityBackground(cell),
                    opacity: densityOpacity(cell.total, densityPeak),
                  }}
                  title={densityTitle(cell)}
                  data-testid="ops-density-cell"
                />
              )),
            )}
            {/* Gridlines */}
            {ticks.map((tick) => (
              <div key={tick.atMs} className="np-ops-gridline" style={{ left: tick.xPx }} aria-hidden="true" />
            ))}
            {/* NOW line */}
            <div className="np-ops-now" style={{ left: nowX }} aria-hidden="true" />

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

            {placedBars.map((bar) => {
              const node = nodesById.get(bar.workflowId);
              return (
                <OpsTimelineBar
                  key={bar.executionId}
                  bar={bar}
                  topPx={laneTops.tops[bar.laneIndex]}
                  nowMs={nowMs}
                  selected={bar.executionId === selectedExecutionId}
                  label={`${node?.name ?? bar.workflowId} · ${statusLabel(bar.status)}`}
                  overdue={overdueIds.has(bar.executionId)}
                  stalled={stalledIds.has(bar.executionId)}
                  onSelect={onSelect}
                />
              );
            })}

            {/* Duration beside bars too narrow to hold it — keeps runs comparable at 1 h / 4 h. */}
            {placedBars.filter((b) => outsideLabelIds.has(b.executionId)).map((bar) => (
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
            {new Date(tick.atMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
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
