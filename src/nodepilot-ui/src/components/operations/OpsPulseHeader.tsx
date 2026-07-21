import { useTranslation } from 'react-i18next';
import { FlashFilled, Time, Hourglass } from '@carbon/icons-react';
import type { PulseReason, PulseState } from '../../lib/opsPulse';

// Header banner of the Mission-Control view: overall system state (nominal/degraded/incident)
// with a heartbeat dot, the contributing reasons, the live in-flight counts and a live wall
// clock. The state word is an aria-live region so screen readers hear state transitions.

const STATE_TEXT_CLASS: Record<PulseState, string> = {
  nominal: 'text-success',
  degraded: 'text-warning',
  incident: 'text-error',
};

export function OpsPulseHeader({ state, reasons, runningCount, pendingCount, longRunningCount, nowMs }: Readonly<{
  state: PulseState;
  reasons: PulseReason[];
  runningCount: number;
  pendingCount: number;
  longRunningCount: number;
  nowMs: number;
}>) {
  const { t } = useTranslation(['operations']);

  return (
    <div className={`np-ops-pulse np-ops-pulse--${state} flex flex-wrap items-center gap-x-6 gap-y-2 rounded-2xl border border-outline-variant px-4 py-3`}>
      <div className="flex min-w-0 items-center gap-3">
        <span className={`np-ops-pulse-dot np-ops-pulse-dot--${state}`} aria-hidden="true" />
        <span
          role="status"
          aria-live="polite"
          className={`font-headline text-lg font-semibold uppercase tracking-wider ${STATE_TEXT_CLASS[state]}`}
        >
          {t(`operations:pulse.${state}`)}
        </span>
        {reasons.length > 0 && (
          <span className="min-w-0 truncate text-sm text-on-surface-variant" title={reasons.map((r) => t(`operations:pulse.reason.${r}`)).join(' · ')}>
            — {reasons.map((r) => t(`operations:pulse.reason.${r}`)).join(' · ')}
          </span>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-x-5 gap-y-1 text-sm text-on-surface-variant">
        <span className="flex items-center gap-1.5">
          <FlashFilled size={15} className="text-running" aria-hidden="true" />
          <span className="tabular-nums font-medium text-on-surface">{runningCount}</span>
          {t('operations:pulse.active')}
        </span>
        <span className="flex items-center gap-1.5">
          <Time size={15} className="text-outline" aria-hidden="true" />
          <span className="tabular-nums font-medium text-on-surface">{pendingCount}</span>
          {t('operations:pulse.pending')}
        </span>
        {longRunningCount > 0 && (
          <span className="flex items-center gap-1.5 text-warning">
            <Hourglass size={15} aria-hidden="true" />
            <span className="tabular-nums font-medium">{longRunningCount}</span>
            {t('operations:pulse.longRunning')}
          </span>
        )}
      </div>

      <time
        className="ml-auto font-mono text-lg tabular-nums text-on-surface"
        dateTime={new Date(nowMs).toISOString()}
      >
        {new Date(nowMs).toLocaleTimeString()}
      </time>
    </div>
  );
}
