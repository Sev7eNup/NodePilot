/**
 * Shared Gantt rendering used by the History tab, which replays StepExecution rows from the
 * database, and by the Live tab, which shows in-flight StepUpdate rows from SignalR.
 *
 * Callers normalize their domain rows into `GanttRow[]`. Passing `nowMs` extends the rendered
 * span to the current time and stretches running rows up to that point.
 *
 * History adds the optional scrub slider; live mode skips it.
 */

import { useTranslation } from 'react-i18next';

export interface GanttRow {
  id: string;
  name: string;
  status: string;
  startMs: number | null;
  endMs: number | null;
}

interface ScrubConfig {
  value: number | null;
  onChange: (t: number | null) => void;
}

interface Props {
  rows: GanttRow[];
  /** Current timestamp. When set, the right edge of the chart extends to it and any row
   *  with `status === 'Running'` and `endMs == null` is rendered up to it. */
  nowMs?: number;
  /** Called when a row is clicked. Callers usually select that step. */
  onSelectRow?: (id: string) => void;
  /** Scrub cursor and range slider for replay. Used by the History tab only. */
  scrub?: ScrubConfig;
  /** Minimum width in pixels. Keeps short runs from collapsing into a single column. */
  minWidthPx?: number;
}

export function GanttChart({ rows, nowMs, onSelectRow, scrub, minWidthPx = 600 }: Readonly<Props>) {
  const { t } = useTranslation('designer');
  // The effective end resolves a still-running row to nowMs so live bars grow with the ticker.
  // Skipped rows and rows without a start time stay null and render as a dash.
  const effectiveRows = rows.map((r) => ({
    ...r,
    effEndMs: r.endMs ?? (r.status === 'Running' && nowMs != null ? nowMs : null),
  }));

  const starts = effectiveRows.filter((r) => r.startMs != null).map((r) => r.startMs!);
  const ends = effectiveRows.filter((r) => r.effEndMs != null).map((r) => r.effEndMs!);

  if (starts.length === 0) {
    return (
      <div className="flex items-center justify-center py-8 text-on-surface-variant font-label text-xs">
        {t('gantt.waitingFirstStep')}
      </div>
    );
  }

  const ganttStart = Math.min(...starts);
  const ganttEnd = Math.max(
    ends.length > 0 ? Math.max(...ends) : ganttStart + 1,
    nowMs ?? 0,
  );
  const totalSpanMs = ganttEnd > ganttStart ? ganttEnd - ganttStart : 1;
  const ticks = [0, 1, 2, 3, 4].map((i) => ({
    pct: (i / 4) * 100,
    label: formatClock(ganttStart + (i / 4) * totalSpanMs),
  }));

  return (
    <div
      className="overflow-x-auto rounded-md border border-outline-variant/20 bg-surface-lowest"
      data-testid="gantt-chart"
    >
      {/* Time axis */}
      <div
        className="relative h-5 border-b border-outline-variant/20 bg-surface-low"
        style={{ minWidth: minWidthPx }}
      >
        <div className="absolute inset-0 px-36">
          {ticks.map((t) => (
            <span
              key={t.pct}
              className="absolute -translate-x-1/2 text-[9px] font-mono text-outline top-0.5"
              style={{ left: `${t.pct}%` }}
            >
              {t.label}
            </span>
          ))}
        </div>
      </div>

      {/* Step rows */}
      {effectiveRows.map((r) => {
        const leftPct = r.startMs != null ? ((r.startMs - ganttStart) / totalSpanMs) * 100 : null;
        const widthPct = r.startMs != null && r.effEndMs != null
          ? Math.max(0.5, ((r.effEndMs - r.startMs) / totalSpanMs) * 100)
          : null;
        const durationMs = r.startMs != null && r.effEndMs != null ? r.effEndMs - r.startMs : null;
        const isRunning = r.status === 'Running';
        const rowContent = (
          <>
            <div className="w-96 shrink-0 flex items-center gap-1.5 px-2">
              <StatusDot status={r.status} />
              <span className="font-label text-[11px] text-on-surface-variant truncate">{r.name}</span>
            </div>
            <div className="flex-1 relative h-4 bg-surface-low rounded-sm mx-2">
              {leftPct != null && widthPct != null ? (
                <div
                  className={`absolute top-0 bottom-0 rounded-sm ${isRunning ? 'animate-pulse' : ''}`}
                  style={{
                    left: `${leftPct}%`,
                    width: `${widthPct}%`,
                    backgroundColor: barColorFor(r.status),
                    opacity: 0.85,
                  }}
                />
              ) : (
                <span className="absolute inset-0 flex items-center px-2 text-[10px] text-outline">—</span>
              )}
              {scrub?.value != null && (
                <div
                  className="absolute top-0 bottom-0 w-px bg-primary/70 pointer-events-none z-10"
                  style={{ left: `${((scrub.value - ganttStart) / totalSpanMs) * 100}%` }}
                />
              )}
            </div>
            <div className="w-16 shrink-0 text-right pr-2 font-mono text-[10px] text-on-surface-variant tabular-nums">
              {durationMs != null ? formatMs(durationMs) : '—'}
            </div>
          </>
        );
        const commonClasses = 'w-full flex items-center border-b border-outline-variant/10 h-6 text-left';
        const title = titleFor(r.name, r.status, durationMs);
        return onSelectRow ? (
          <button
            key={r.id}
            type="button"
            onClick={() => onSelectRow(r.id)}
            className={`${commonClasses} hover:bg-surface-low/60 transition-colors`}
            style={{ minWidth: minWidthPx }}
            title={title}
          >
            {rowContent}
          </button>
        ) : (
          <div
            key={r.id}
            className={commonClasses}
            style={{ minWidth: minWidthPx }}
            title={title}
          >
            {rowContent}
          </div>
        );
      })}

      {/* Scrubber: vertical cursor and range slider for replay */}
      {scrub && ganttEnd > ganttStart && (
        <div
          className="relative px-2 py-2 border-t border-outline-variant/20 bg-surface-low/60"
          style={{ minWidth: minWidthPx }}
        >
          <div className="flex items-center gap-3 pl-36 pr-16">
            <span className="text-[9px] font-mono text-outline shrink-0">{formatClock(ganttStart)}</span>
            <div className="relative flex-1">
              <input
                type="range"
                min={ganttStart}
                max={ganttEnd}
                step={Math.max(1, Math.round(totalSpanMs / 500))}
                value={scrub.value ?? ganttEnd}
                onChange={(e) => scrub.onChange(Number(e.target.value))}
                onMouseUp={(e) => {
                  // Read from the input directly to avoid a stale closure when React has
                  // not flushed onChange before mouseup fires.
                  const t = Number((e.target as HTMLInputElement).value);
                  if (t >= ganttEnd) scrub.onChange(null);
                }}
                className="w-full accent-primary cursor-pointer"
                title={t('gantt.scrub')}
              />
              {scrub.value != null && (
                <span
                  className="absolute -top-5 bg-primary text-on-primary text-[9px] font-mono px-1.5 py-0.5 rounded pointer-events-none -translate-x-1/2"
                  style={{ left: `${((scrub.value - ganttStart) / totalSpanMs) * 100}%` }}
                >
                  {formatClock(scrub.value)}
                </span>
              )}
            </div>
            <span className="text-[9px] font-mono text-outline shrink-0">{formatClock(ganttEnd)}</span>
            {scrub.value != null && (
              <button
                type="button"
                onClick={() => scrub.onChange(null)}
                className="text-[9px] font-label text-primary hover:underline shrink-0"
                title={t('gantt.clearScrub')}
              >
                {t('gantt.reset')}
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function StatusDot({ status }: Readonly<{ status: string }>) {
  const color =
    status === 'Succeeded' ? 'bg-green-500' :
    status === 'Failed' ? 'bg-red-500' :
    status === 'Running' ? 'bg-blue-500 animate-pulse' :
    status === 'Skipped' ? 'bg-outline-variant' :
    status === 'Paused' ? 'bg-orange-500 animate-pulse' :
    'bg-outline-variant/60';
  return <span className={`w-2 h-2 rounded-full shrink-0 ${color}`} />;
}

function barColorFor(status: string): string {
  if (status === 'Succeeded') return 'var(--color-success)';
  if (status === 'Failed') return 'var(--color-error)';
  if (status === 'Running') return 'var(--color-running)';
  if (status === 'Paused') return '#fb923c';
  if (status === 'Skipped') return '#d1d5db';
  return 'var(--color-skipped)';
}

function titleFor(name: string, status: string, durationMs: number | null): string {
  const head = `${name} · ${status}`;
  return durationMs != null ? `${head} · ${formatMs(durationMs)}` : head;
}

function formatClock(ms: number): string {
  const d = new Date(ms);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  return `${hh}:${mm}:${ss}`;
}

function formatMs(ms: number): string {
  if (ms < 0) ms = 0;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m ${s}s`;
}
