import { useTranslation } from 'react-i18next';
import { ConnectionSignal } from '@carbon/icons-react';
import type { OpsNode } from '../../types/api';
import type { TickerEvent } from '../../stores/operationsStore';
import { npStatusFromExecution, rawStatusLabelKey, STATUS_DOT_CLASS, STATUS_TEXT_CLASS } from '../../lib/statusTokens';

// Live event ticker (right rail): the raw ExecutionStatusChanged stream, newest on top.
// aria-live is deliberately off — during a busy spell this feed is far too chatty for
// screen-reader announcements; the pulse header carries the polite state summary instead.

function relativeLabel(atMs: number, nowMs: number, t: (key: string, opts?: Record<string, unknown>) => string): string {
  const ageMin = Math.floor((nowMs - atMs) / 60_000);
  if (ageMin < 1) return t('operations:ticker.justNow');
  return t('operations:ticker.minutesAgo', { count: ageMin });
}

export function OpsEventTicker({ events, nodesById, nowMs, onSelect }: Readonly<{
  events: TickerEvent[];
  nodesById: Map<string, OpsNode>;
  nowMs: number;
  onSelect: (executionId: string) => void;
}>) {
  const { t } = useTranslation(['operations', 'executions']);

  return (
    <section className="flex min-h-0 flex-col" aria-label={t('operations:ticker.title')}>
      <h2 className="mb-2 flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-on-surface-variant">
        <ConnectionSignal size={14} aria-hidden="true" />
        {t('operations:ticker.title')}
      </h2>
      {events.length === 0 ? (
        <p className="text-sm text-outline">{t('operations:ticker.empty')}</p>
      ) : (
        <ul role="log" aria-live="off" className="min-h-0 flex-1 space-y-1 overflow-y-auto pr-1">
          {events.map((evt) => {
            const status = npStatusFromExecution(evt.status);
            const labelKey = rawStatusLabelKey(evt.status);
            const node = nodesById.get(evt.workflowId);
            return (
              <li key={`${evt.executionId}-${evt.status}`} className="np-ops-ticker-item">
                <button
                  type="button"
                  onClick={() => onSelect(evt.executionId)}
                  className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left hover:bg-surface-high"
                >
                  <span className={`h-2 w-2 shrink-0 rounded-full ${STATUS_DOT_CLASS[status]}`} aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate text-sm text-on-surface" title={node?.name ?? evt.executionId}>
                    {node?.name ?? evt.executionId.slice(0, 8)}
                  </span>
                  <span className={`shrink-0 text-xs font-medium ${STATUS_TEXT_CLASS[status]}`}>
                    {labelKey ? t(`executions:status.${labelKey}`) : evt.status}
                  </span>
                  <span className="shrink-0 text-[11px] tabular-nums text-outline">
                    {relativeLabel(evt.atMs, nowMs, t)}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
