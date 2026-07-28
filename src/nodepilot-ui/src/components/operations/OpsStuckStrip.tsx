import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { WarningAltFilled } from '@carbon/icons-react';
import { formatDuration, type PlacedBar } from '../../lib/opsTimeline';
import { STATUS_TEXT_CLASS } from '../../lib/statusTokens';

// Overdue runs, lifted out of the timeline into one line above it.
//
// Why a strip and not lane re-ordering: a run older than the 20-minute window is clamped to the
// left edge and looks like any other long bar, so the runs that matter most are the least
// visible. Re-sorting lanes would fix that by churning the whole layout every time a bar crosses
// the threshold — and would invalidate the deterministic lane order assignLanes() guarantees.
// A separate strip is stable, bounded, and disappears entirely when nothing is stuck.

const CAP = 5;

export function OpsStuckStrip({ bars, nowMs, nameFor, onSelect }: Readonly<{
  /** Already filtered to overdue bars by the caller (single source of truth: isOverdue). */
  bars: PlacedBar[];
  nowMs: number;
  nameFor: (workflowId: string) => string;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations']);

  // Oldest first: the longest-running one is the most likely to be genuinely stuck.
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
            {/* The distinguishing detail: "long" vs. "stuck on ONE step since 11 min". Only
                shown when the server actually enriched this run (null = unknown, not zero). */}
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
                  value: new Date(bar.startedAtMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
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
