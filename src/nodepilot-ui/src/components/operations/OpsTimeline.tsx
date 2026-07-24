import { useLayoutEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Activity } from '@carbon/icons-react';
import type { OpsNode, OpsRecentExecution, OpsRunningExecution } from '../../types/api';
import type { LocalSettled } from '../../stores/operationsStore';
import { rawStatusLabelKey } from '../../lib/statusTokens';
import {
  windowFor, buildTimelineBars, assignLanes, placeBar, axisTicks, timeToX, pairCallConnectors,
  isActiveBarStatus, type PlacedBar,
} from '../../lib/opsTimeline';
import { OpsTimelineBar, OPS_ROW_H } from './OpsTimelineBar';
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

export function OpsTimeline({ nowMs, running, recent, locallySettled, scopedWorkflowIds, nodesById, selectedExecutionId, nextStart, onSelect }: Readonly<{
  nowMs: number;
  running: OpsRunningExecution[];
  recent: OpsRecentExecution[];
  locallySettled: Record<string, LocalSettled>;
  scopedWorkflowIds: Set<string>;
  nodesById: Map<string, OpsNode>;
  selectedExecutionId: string | null;
  /** Nearest upcoming trigger fire (for the idle hero), if any. */
  nextStart: { name: string; atMs: number } | null;
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

  const w = useMemo(() => windowFor(nowMs, undefined, trackWidth), [nowMs, trackWidth]);

  const { lanes, placed } = useMemo(() => {
    const bars = buildTimelineBars(running, recent, locallySettled, w, scopedWorkflowIds);
    return assignLanes(bars, nodesById);
  }, [running, recent, locallySettled, w, scopedWorkflowIds, nodesById]);

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

  const ticks = useMemo(() => axisTicks(w), [w]);
  const nowX = timeToX(nowMs, w);

  const rowCenterY = (bar: PlacedBar) =>
    laneTops.tops[bar.laneIndex] + bar.subRow * OPS_ROW_H + OPS_ROW_H / 2;

  const statusLabel = (status: string) => {
    const key = rawStatusLabelKey(status);
    return key ? t(`executions:status.${key}`) : status;
  };

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
    <div className="np-ops-timeline flex h-full min-h-0 flex-col" data-testid="ops-timeline">
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
                    className="absolute right-0 flex flex-col justify-start gap-0.5 overflow-hidden pr-3 pt-[6px]"
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
                      <div className="truncate text-[11px] text-outline" title={lane.folderPath}>{lane.folderPath}</div>
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
                  onSelect={onSelect}
                />
              );
            })}
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
