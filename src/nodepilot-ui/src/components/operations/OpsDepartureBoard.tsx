import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Plane } from '@carbon/icons-react';
import type { OpsArmedTrigger } from '../../api/operations';
import { formatDuration } from '../../lib/opsTimeline';

// Next-fires "departure board": upcoming scheduled starts, soonest first, split-flap style.
// Rows are keyed by workflow+nextFire so a changed fire time remounts the row and replays
// the flip-in animation (CSS only — no per-character flap machinery).

const BOARD_CAP = 8;

export function OpsDepartureBoard({ triggers, nowMs }: Readonly<{
  triggers: OpsArmedTrigger[];
  nowMs: number;
}>) {
  const { t } = useTranslation(['operations']);

  const rows = useMemo(() => {
    const sorted = [...triggers].sort((a, b) => {
      if (a.nextFireUtc === null && b.nextFireUtc === null) return a.workflowName.localeCompare(b.workflowName);
      if (a.nextFireUtc === null) return 1;
      if (b.nextFireUtc === null) return -1;
      return Date.parse(a.nextFireUtc) - Date.parse(b.nextFireUtc);
    });
    return sorted.slice(0, BOARD_CAP);
  }, [triggers]);

  return (
    <section className="np-ops-board rounded-2xl border border-outline-variant bg-surface px-4 py-3" aria-label={t('operations:board.title')}>
      <h2 className="mb-2 flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-on-surface-variant">
        <Plane size={14} aria-hidden="true" />
        {t('operations:board.title')}
      </h2>
      {rows.length === 0 ? (
        <p className="text-sm text-outline">{t('operations:board.empty')}</p>
      ) : (
        <table className="w-full font-mono text-sm">
          <thead>
            <tr className="text-left text-[10px] uppercase tracking-widest text-outline">
              <th scope="col" className="pb-1 pr-4 font-medium">{t('operations:board.time')}</th>
              <th scope="col" className="pb-1 pr-4 font-medium">{t('operations:board.workflow')}</th>
              <th scope="col" className="hidden pb-1 pr-4 font-medium sm:table-cell">{t('operations:board.trigger')}</th>
              <th scope="col" className="pb-1 text-right font-medium">{t('operations:board.tMinus')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((trigger) => {
              const fireMs = trigger.nextFireUtc ? Date.parse(trigger.nextFireUtc) : null;
              const overdue = fireMs !== null && fireMs < nowMs;
              return (
                <tr key={`${trigger.workflowId}-${trigger.nextFireUtc ?? 'none'}`} className="np-ops-flap text-on-surface">
                  <td className="py-0.5 pr-4 tabular-nums text-primary">
                    {fireMs !== null
                      ? new Date(fireMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                      : '—'}
                  </td>
                  <td className="max-w-0 truncate py-0.5 pr-4 font-sans" title={trigger.workflowName}>
                    {trigger.workflowName}
                  </td>
                  <td className="hidden py-0.5 pr-4 text-on-surface-variant sm:table-cell">
                    {trigger.triggerTypes.join(', ')}
                  </td>
                  <td className="py-0.5 text-right tabular-nums text-on-surface-variant">
                    {fireMs === null
                      ? '—'
                      : overdue
                        ? t('operations:board.overdue')
                        : t('operations:board.in', { value: formatDuration(fireMs - nowMs) })}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
