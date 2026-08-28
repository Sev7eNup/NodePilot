import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { WarningAltFilled } from '@carbon/icons-react';
import { formatDuration, type PlacedBar } from '../../lib/opsTimeline';
import { formatTime } from '../../lib/format';
import { STATUS_TEXT_CLASS } from '../../lib/statusTokens';

// Overdue runs, lifted out of the timeline into one line above it.
//
// A run older than the visible window is clamped to the left edge and looks like any other long
// bar, so the runs that matter most are the least visible. Re-sorting lanes instead would reshuffle
// the layout whenever a bar crosses the threshold and would break the deterministic lane order
// assignLanes() guarantees. A separate strip is stable, bounded, and hidden when nothing is stuck.

const CAP = 5;

export function OpsStuckStrip({ bars, nowMs, nameFor, onSelect }: Readonly<{
  /** Already filtered to overdue bars by the caller, using isOverdue as the single source. */
  bars: PlacedBar[];
  nowMs: number;
  nameFor: (workflowId: string) => string;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations']);

  // Oldest first: the longest-running run is the most likely to be genuinely stuck.
  const sorted = useMemo(
    () => [...bars].sort((a, b) => a.startedAtMs - b.startedAtMs),
    [bars],
  );

  if (sorted.length === 0) return null;

  const shown = sorted.slice(0, CAP);
  const hidden = sorted.length - shown.length;

  return (
    <section className="np-ops-stuck" aria-label={t('operations:stuck.title')}>
      <span className={`flex shrink-0 items-center gap-1.5 text-xs font-medium uppercase tracking-wide ${STATUS_TEXT_CLASS.warning}`}>
        <WarningAltFilled size={14} aria-hidden="true" />
        {t('operations:stuck.title')}
      </span>
      {shown.map((bar) => {
        const name = nameFor(bar.workflowId);
        const since = formatDuration(nowMs - bar.startedAtMs);
        return (
          <button
            key={bar.executionId}
            type="button"
            onClick={() => onSelect(bar.executionId)}
            className="np-ops-stuck-item"
            title={`${name} · ${t('operations:stuck.since', { value: since })}`}
          >
            <span className="max-w-[16rem] truncate font-medium text-on-surface">{name}</span>
            <span className={`tabular-nums ${STATUS_TEXT_CLASS.warning}`}>
              {t('operations:stuck.since', { value: since })}
            </span>
            {/* Separates a merely long run from one stuck on a single step. Shown only when the
                server enriched this run; null means unknown, not zero. */}
            {bar.lastProgressAtMs !== null ? (
              <span className="tabular-nums text-on-surface-variant">
                {t('operations:stuck.lastProgress', {
                  value: formatDuration(nowMs - bar.lastProgressAtMs),
                  step: bar.lastCompletedStepName ?? '—',
                })}
              </span>
            ) : (
              <span className="tabular-nums text-on-surface-variant">
                {t('operations:stuck.startedAt', {
                  value: formatTime(bar.startedAtMs, { hour: '2-digit', minute: '2-digit' }),
                })}
              </span>
            )}
          </button>
        );
      })}
      {hidden > 0 && (
        <span className="shrink-0 text-xs text-on-surface-variant">
          {t('operations:stuck.more', { count: hidden })}
        </span>
      )}
    </section>
  );
}
