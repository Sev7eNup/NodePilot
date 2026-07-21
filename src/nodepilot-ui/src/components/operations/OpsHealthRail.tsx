import { useTranslation } from 'react-i18next';
import { Screen, Favorite, Trophy } from '@carbon/icons-react';
import type { OpsHeartbeat } from '../../api/operations';
import type { PulseReason } from '../../lib/opsPulse';

// Infrastructure health rail: machine reachability, background-service heartbeats and the
// HA cluster role. Items contributing to a degraded/incident pulse get the "hot" highlight.

export function OpsHealthRail({ machinesTotal, machinesReachable, heartbeats, clusterRole, reasons }: Readonly<{
  machinesTotal: number;
  machinesReachable: number;
  heartbeats: OpsHeartbeat[];
  clusterRole: string | null;
  reasons: PulseReason[];
}>) {
  const { t } = useTranslation(['operations']);
  const machinesHot = reasons.includes('machinesUnreachable') || reasons.includes('allMachinesUnreachable');
  const heartbeatsHot = reasons.includes('staleHeartbeat') || reasons.includes('allHeartbeatsStale');

  return (
    <section className="space-y-3" aria-label={t('operations:health.title')}>
      <h2 className="text-xs font-medium uppercase tracking-wide text-on-surface-variant">
        {t('operations:health.title')}
      </h2>

      <div className={`rounded-xl border px-3 py-2 ${machinesHot ? 'np-ops-health--hot border-warning' : 'border-outline-variant'}`}>
        <div className="flex items-center justify-between gap-2 text-sm">
          <span className="flex items-center gap-1.5 text-on-surface-variant">
            <Screen size={14} aria-hidden="true" />
            {t('operations:health.machines')}
          </span>
          <span className={`tabular-nums font-medium ${machinesHot ? 'text-warning' : 'text-on-surface'}`}>
            {machinesReachable}/{machinesTotal}
          </span>
        </div>
      </div>

      {heartbeats.length > 0 && (
        <div className={`rounded-xl border px-3 py-2 ${heartbeatsHot ? 'np-ops-health--hot border-warning' : 'border-outline-variant'}`}>
          <div className="mb-1 flex items-center gap-1.5 text-sm text-on-surface-variant">
            <Favorite size={14} aria-hidden="true" />
            {t('operations:health.heartbeats')}
          </div>
          <ul className="space-y-0.5">
            {heartbeats.map((hb) => (
              <li key={hb.serviceName} className="flex items-center justify-between gap-2 text-xs">
                <span className="truncate text-on-surface" title={hb.serviceName}>{hb.serviceName}</span>
                {hb.isStale ? (
                  <span className="shrink-0 rounded-full bg-warning-container px-1.5 py-0.5 text-[10px] font-medium text-on-warning-container">
                    {t('operations:health.stale')}
                  </span>
                ) : (
                  <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-success" aria-hidden="true" />
                )}
              </li>
            ))}
          </ul>
        </div>
      )}

      {clusterRole && (
        <div className="rounded-xl border border-outline-variant px-3 py-2">
          <div className="flex items-center justify-between gap-2 text-sm">
            <span className="flex items-center gap-1.5 text-on-surface-variant">
              <Trophy size={14} aria-hidden="true" />
              {t('operations:health.role')}
            </span>
            <span className="font-medium text-on-surface">
              {clusterRole === 'leader' ? t('operations:health.leader')
                : clusterRole === 'standby' ? t('operations:health.standby')
                : clusterRole}
            </span>
          </div>
        </div>
      )}
    </section>
  );
}
