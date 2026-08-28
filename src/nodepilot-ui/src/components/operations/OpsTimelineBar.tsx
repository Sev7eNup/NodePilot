import { memo } from 'react';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../../lib/statusTokens';
import { formatDuration, isActiveBarStatus, type PlacedBar } from '../../lib/opsTimeline';
import { formatTime } from '../../lib/format';

// One execution bar on the live timeline. Absolutely positioned by the parent-provided geometry.
//
// A settled bar is placed once per snapshot inside `.np-ops-shift`, the layer that slides as a
// whole between polls, so its props stay constant across clock ticks and `memo` keeps it out of
// the render. A running bar is placed against the live window every tick because it grows toward
// now, and carries the CSS transition that smooths that growth (see .np-ops-bar--running).

const BAR_H = 22;
const ROW_H = 38;

/**
 * Floor on the rendered bar width so a very short run stays visible and clickable.
 *
 * Kept small because the floor destroys proportionality: on a wide window a generous floor would
 * render every short run at the same length and hide which one took longer. Below the floor,
 * `OpsTimeline` writes the duration next to the bar instead — see OPS_INSIDE_LABEL_PX.
 */
export const OPS_MIN_BAR_PX = 4;

/** Below this width the duration label no longer fits inside the bar. */
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

export const OpsTimelineBar = memo(function OpsTimelineBar({ bar, topPx, durationMs, selected, label, overdue, stalled, onSelect }: Readonly<{
  bar: PlacedBar;
  topPx: number;
  /**
   * How long the run has taken, in ms. Precomputed by the parent instead of derived from a clock
   * here, so a settled bar keeps constant props and `memo` holds across clock ticks.
   */
  durationMs: number;
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
  const showLabel = bar.widthPx >= OPS_INSIDE_LABEL_PX;
  const glyph = active ? null : settledGlyph(bar.status);
  // A bar that started before the window is clamped to x=0, so a long run looks like a short one.
  // The mask-image fade is the only other cue and is easy to miss, so state the actual start time.
  const startedBefore = bar.clippedLeft
    ? formatTime(bar.startedAtMs, { hour: '2-digit', minute: '2-digit' })
    : null;

  return (
    <button
      type="button"
      // Not a tab stop of its own: a busy board carries thousands of bars, and one tab stop apiece
      // would leave everything below the timeline unreachable by keyboard. The track is the single
      // tab stop and moves an aria-activedescendant across these ids; see OpsTimeline key input.
      id={`ops-bar-${bar.executionId}`}
      tabIndex={-1}
      onClick={() => onSelect(bar.executionId)}
      title={[
        `${label} · ${formatDuration(durationMs)}`,
        // Finished steps only, never "x of y": DeferRunningStateWrite leaves no reliable total.
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

/**
 * Rendered height of a run bar. Exported so the density histogram can be pinned strictly below it
 * — see OPS_DENSITY_MAX_H. An aggregate column that reaches bar height reads as a single run.
 */
export const OPS_BAR_H = BAR_H;
