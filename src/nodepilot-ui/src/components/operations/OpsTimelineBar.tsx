import { memo } from 'react';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../../lib/statusTokens';
import { formatDuration, isActiveBarStatus, type PlacedBar } from '../../lib/opsTimeline';
import { formatTime } from '../../lib/format';

// One execution bar on the live timeline. Absolutely positioned by the parent-provided geometry.
//
// Which coordinate system that geometry is in depends on the bar: a SETTLED bar is placed once per
// snapshot and lives inside `.np-ops-shift`, the layer that slides as a whole between polls, so its
// props are constant across clock ticks and `memo` keeps it out of the render entirely. A RUNNING
// bar is placed against the live window every tick — it grows toward NOW, which a layer translation
// cannot express — and carries the CSS transition that smooths that growth (see .np-ops-bar--running).

const BAR_H = 22;
const ROW_H = 38;

/**
 * Floor on the rendered bar width so a very short run stays visible and clickable.
 *
 * Kept small on purpose: the floor destroys proportionality, and it does so worst exactly where
 * the window is widest. Even at the selectable 1 h window, short runs compress to only a few
 * pixels, so a generous floor makes them the same length and hides which run took longer.
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

export const OpsTimelineBar = memo(function OpsTimelineBar({ bar, topPx, durationMs, selected, label, overdue, stalled, onSelect }: Readonly<{
  bar: PlacedBar;
  topPx: number;
  /**
   * How long the run has taken, in ms — precomputed by the parent rather than derived here from a
   * clock. Deliberate: this used to take `nowMs`, which a settled bar never reads but which changed
   * every second, so `memo` saw a new prop and re-rendered thousands of frozen bars once a second
   * for nothing. A settled bar's duration is constant, so the memo now actually holds.
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
  // A bar that started before the window is clamped to x=0, so a 3-hour hang looks exactly
  // like a 21-minute one. The only other cue is a mask-image fade, which is easy to miss —
  // state the actual start time instead.
  const startedBefore = bar.clippedLeft
    ? formatTime(bar.startedAtMs, { hour: '2-digit', minute: '2-digit' })
    : null;

  return (
    <button
      type="button"
      // Not a tab stop of its own. A busy board carries thousands of these, and one tab stop apiece
      // turns the timeline into a keyboard trap: nothing below it is reachable without thousands of
      // presses. The track itself is the single tab stop and moves an aria-activedescendant across
      // these ids instead — see OpsTimeline's key handling.
      id={`ops-bar-${bar.executionId}`}
      tabIndex={-1}
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

/**
 * Rendered height of a run bar. Exported so the density histogram can be pinned strictly below it
 * — see OPS_DENSITY_MAX_H. An aggregate column that reaches bar height reads as a single run, which
 * is exactly the defect the histogram replaced.
 */
export const OPS_BAR_H = BAR_H;
