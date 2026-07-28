import { memo } from 'react';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../../lib/statusTokens';
import { formatDuration, isActiveBarStatus, type PlacedBar } from '../../lib/opsTimeline';

// One execution bar on the live timeline. Absolutely positioned by the parent-provided
// geometry; horizontal motion between clock ticks is carried by the CSS linear transition
// on left/width (see .np-ops-bar). Memoized — only the inline geometry changes per tick.

const BAR_H = 22;
const ROW_H = 38;

/**
 * Floor on the rendered bar width so a very short run stays visible and clickable.
 *
 * Kept small on purpose: the floor destroys proportionality, and it does so worst exactly where
 * the window is widest. At 20 min a 2-minute run is ~55 px; at 4 h it is ~2 px, so a generous
 * floor made every bar in the 1 h / 4 h views the same length and hid which run took longer.
 * Below the floor, `OpsTimeline` writes the duration next to the bar instead — see
 * OPS_INSIDE_LABEL_PX.
 */
export const OPS_MIN_BAR_PX = 4;

/** Narrower than this and the duration no longer fits INSIDE the bar. */
export const OPS_INSIDE_LABEL_PX = 64;

/** Terminal glyph shown inside settled bars that are wide enough. */
function settledGlyph(status: string): string | null {
  switch (npStatusFromExecution(status)) {
    case 'success': return '✓';
    case 'failed': return '✕';
    case 'cancelled':
    case 'skipped': return '–';
    default: return null;
  }
}

export const OpsTimelineBar = memo(function OpsTimelineBar({ bar, topPx, nowMs, selected, label, overdue, stalled, onSelect }: Readonly<{
  bar: PlacedBar;
  topPx: number;
  nowMs: number;
  selected: boolean;
  /** Accessible name: workflow + status (built by the parent with i18n). */
  label: string;
  /** Running past the long-running threshold — see isOverdue(). */
  overdue: boolean;
  /** No step has finished for a while — sitting on one step rather than working through many. */
  stalled: boolean;
  onSelect: (executionId: string) => void;
}>) {
  const active = isActiveBarStatus(bar.status);
  const color = STATUS_COLOR_VAR[npStatusFromExecution(bar.status)];
  const durationMs = (bar.completedAtMs ?? nowMs) - bar.startedAtMs;
  const showLabel = bar.widthPx >= OPS_INSIDE_LABEL_PX;
  const glyph = active ? null : settledGlyph(bar.status);
  // A bar that started before the window is clamped to x=0, so a 3-hour hang looks exactly
  // like a 21-minute one. The only other cue is a mask-image fade, which is easy to miss —
  // state the actual start time instead.
  const startedBefore = bar.clippedLeft
    ? new Date(bar.startedAtMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : null;

  return (
    <button
      type="button"
      onClick={() => onSelect(bar.executionId)}
      title={[
        `${label} · ${formatDuration(durationMs)}`,
        // Steps FINISHED, never "x of y": under DeferRunningStateWrite no honest total exists.
        bar.stepsFinished !== null ? `${bar.stepsFinished} steps done` : null,
        bar.lastCompletedStepName,
      ].filter(Boolean).join(' · ')}
      aria-pressed={selected}
      className={[
        'np-ops-bar',
        active ? 'np-ops-bar--running' : 'np-ops-bar--settled',
        overdue ? 'np-ops-bar--overdue' : '',
        stalled ? 'np-ops-bar--stalled' : '',
        bar.clippedLeft ? 'np-ops-bar--clipped' : '',
        selected ? 'np-ops-bar--selected' : '',
      ].filter(Boolean).join(' ')}
      style={{
        left: bar.leftPx,
        width: Math.max(bar.widthPx, OPS_MIN_BAR_PX),
        top: topPx + bar.subRow * ROW_H + (ROW_H - BAR_H) / 2,
        height: BAR_H,
        ['--np-ops-bar-color' as string]: color,
      }}
    >
      {showLabel && (
        <span className="np-ops-bar-label tabular-nums">
          {glyph && <span aria-hidden="true">{glyph} </span>}
          {startedBefore && <span className="np-ops-bar-since">{`‹ ${startedBefore} `}</span>}
          {formatDuration(durationMs)}
        </span>
      )}
    </button>
  );
});

export const OPS_ROW_H = ROW_H;
