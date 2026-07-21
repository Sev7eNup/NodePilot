import { memo } from 'react';
import { npStatusFromExecution, STATUS_COLOR_VAR } from '../../lib/statusTokens';
import { formatDuration, isActiveBarStatus, type PlacedBar } from '../../lib/opsTimeline';

// One execution bar on the live timeline. Absolutely positioned by the parent-provided
// geometry; horizontal motion between clock ticks is carried by the CSS linear transition
// on left/width (see .np-ops-bar). Memoized — only the inline geometry changes per tick.

const BAR_H = 22;
const ROW_H = 38;

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

export const OpsTimelineBar = memo(function OpsTimelineBar({ bar, topPx, nowMs, selected, label, onSelect }: Readonly<{
  bar: PlacedBar;
  topPx: number;
  nowMs: number;
  selected: boolean;
  /** Accessible name: workflow + status (built by the parent with i18n). */
  label: string;
  onSelect: (executionId: string) => void;
}>) {
  const active = isActiveBarStatus(bar.status);
  const color = STATUS_COLOR_VAR[npStatusFromExecution(bar.status)];
  const durationMs = (bar.completedAtMs ?? nowMs) - bar.startedAtMs;
  const showLabel = bar.widthPx >= 64;
  const glyph = active ? null : settledGlyph(bar.status);

  return (
    <button
      type="button"
      onClick={() => onSelect(bar.executionId)}
      title={`${label} · ${formatDuration(durationMs)}`}
      aria-pressed={selected}
      className={[
        'np-ops-bar',
        active ? 'np-ops-bar--running' : 'np-ops-bar--settled',
        bar.clippedLeft ? 'np-ops-bar--clipped' : '',
        selected ? 'np-ops-bar--selected' : '',
      ].filter(Boolean).join(' ')}
      style={{
        left: bar.leftPx,
        width: Math.max(bar.widthPx, 6),
        top: topPx + bar.subRow * ROW_H + (ROW_H - BAR_H) / 2,
        height: BAR_H,
        ['--np-ops-bar-color' as string]: color,
      }}
    >
      {showLabel && (
        <span className="np-ops-bar-label tabular-nums">
          {glyph && <span aria-hidden="true">{glyph} </span>}
          {formatDuration(durationMs)}
        </span>
      )}
    </button>
  );
});

export const OPS_ROW_H = ROW_H;
