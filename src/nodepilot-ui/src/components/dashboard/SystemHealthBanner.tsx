import {
  Activity,
  BareMetalServer,
  CalendarSettings,
  Certificate,
  MagicWandFilled,
  Notification,
  SecurityServices,
} from '@carbon/icons-react';
import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../../api/client';
import { alertingApi, type NotificationDelivery } from '../../api/alerting';
import { useRole } from '../../lib/rbac';
import { useMinuteTick } from '../../hooks/useMinuteTick';
import type { HealthHeartbeatInfo } from '../../pages/DashboardPage';

interface MaintenanceWindow {
  id: string;
  isEnabled: boolean;
  recurrence: 'OneTime' | 'Weekly' | 'Cron';
  oneTimeStartUtc: string | null;
  oneTimeEndUtc: string | null;
}

/**
 * Sticky top banner: cluster role + scheduler heartbeat + DB provider + AI-activation
 * flag (all from the dashboard payload, no extra round-trip) plus compact maintenance-
 * window and alert-delivery badges for Admin/Operator. Colour-coded green/amber/red so
 * a glance tells the operator whether anything needs attention. The maintenance "active"
 * check is exact for OneTime windows (start/end contains now); recurring windows surface
 * as "enabled" only — exact recurrence-active computation is a follow-up. The AI flag
 * mirrors the Llm:Enabled master switch (green "AI activated" vs muted "AI disabled"),
 * not whether a working endpoint is configured.
 */
export function SystemHealthBanner({
  clusterRole, databaseProvider, heartbeats, llmEnabled,
}: Readonly<{
  clusterRole: string | null;
  databaseProvider: string;
  heartbeats: HealthHeartbeatInfo[];
  llmEnabled: boolean;
}>) {
  const { t } = useTranslation(['dashboard']);
  const navigate = useNavigate();
  const { canWrite } = useRole();
  const now = useMinuteTick();

  // Scheduler heartbeat — same heuristic the dashboard already uses for its KPI hint.
  const scheduler = useMemo(
    () => heartbeats.find((h) => h.serviceName.toLowerCase().includes('scheduler')
      || h.serviceName.toLowerCase().includes('trigger')),
    [heartbeats],
  );
  const schedulerStale = scheduler?.isStale === true;
  const schedulerNoData = !scheduler;

  // Maintenance windows — Admin/Op only (endpoint is Admin/Op). Polls 60s; not SignalR-driven.
  const { data: windows } = useQuery<MaintenanceWindow[]>({
    queryKey: ['maintenance-windows'],
    queryFn: () => api.get<MaintenanceWindow[]>('/maintenance-windows'),
    refetchInterval: 60_000,
    enabled: canWrite,
  });
  const activeMaintenance = useMemo(() => (windows ?? []).filter((w) =>
    w.isEnabled && w.recurrence === 'OneTime'
    && w.oneTimeStartUtc && w.oneTimeEndUtc
    && new Date(w.oneTimeStartUtc).getTime() <= now
    && new Date(w.oneTimeEndUtc).getTime() >= now).length, [windows, now]);
  const enabledMaintenance = useMemo(() => (windows ?? []).filter((w) => w.isEnabled).length, [windows]);

  // Alert deliveries — Admin/Op only. Failed count of the recent ledger.
  const { data: deliveries } = useQuery<NotificationDelivery[]>({
    queryKey: ['alerting-deliveries', 'dashboard'],
    queryFn: () => alertingApi.deliveries({ limit: 50 }),
    refetchInterval: 60_000,
    enabled: canWrite,
  });
  const failedDeliveries = useMemo(
    () => (deliveries ?? []).filter((d) => d.status === 'Failed').length, [deliveries]);

  // Overall health drives the banner accent: red if anything is wrong, amber if attention,
  // green otherwise.
  const hasRed = schedulerStale || failedDeliveries > 0;
  const hasAmber = !hasRed && (activeMaintenance > 0 || schedulerNoData);
  const healthTone = hasRed ? 'critical' : hasAmber ? 'warning' : 'healthy';
  const accent = hasRed ? 'border-red-500/40 bg-red-500/5'
    : hasAmber ? 'border-amber-500/40 bg-amber-500/5'
    : 'border-emerald-500/30 bg-emerald-500/5';

  return (
    <div
      className={`np-card np-status-card p-3 mb-5 border ${accent} flex flex-wrap items-center gap-x-5 gap-y-2`}
      data-health={healthTone}
    >
      <span className="text-xs uppercase tracking-wider font-semibold text-on-surface-variant shrink-0">
        {t('dashboard:banner.title')}
      </span>
      {/* Scheduler */}
      <span className="flex items-center gap-1.5 text-sm" title={t('dashboard:banner.title')}>
        <Activity size={14} className={schedulerStale ? 'text-red-500' : schedulerNoData ? 'text-amber-500' : 'text-emerald-500'} />
        <span className={schedulerStale ? 'text-red-600' : schedulerNoData ? 'text-amber-600' : 'text-on-surface'}>
          {schedulerStale ? t('dashboard:banner.schedulerStale')
            : schedulerNoData ? t('dashboard:banner.schedulerNoData')
            : t('dashboard:banner.schedulerHealthy')}
        </span>
      </span>
      {/* HA cluster role */}
      <span className="flex items-center gap-1.5 text-sm text-on-surface-variant">
        <BareMetalServer size={14} className="text-outline" />
        <span>{clusterRole === 'leader' ? t('dashboard:banner.clusterLeader')
          : clusterRole === 'standby' ? t('dashboard:banner.clusterStandby')
          : t('dashboard:banner.noCluster')}</span>
      </span>
      {/* DB provider */}
      <span className="flex items-center gap-1.5 text-sm text-on-surface-variant">
        <span className="text-[11px] uppercase tracking-wide text-outline">{t('dashboard:banner.db')}</span>
        <span className="font-medium text-on-surface">{databaseProvider}</span>
      </span>
      {/* AI / LLM activation — reflects the Llm:Enabled master switch */}
      <span
        className="flex items-center gap-1.5 text-sm"
        title={llmEnabled ? t('dashboard:banner.aiActive') : t('dashboard:banner.aiDisabled')}
      >
        <MagicWandFilled size={14} className={llmEnabled ? 'text-emerald-500' : 'text-outline'} />
        <span className={llmEnabled ? 'text-on-surface' : 'text-on-surface-variant'}>
          {llmEnabled ? t('dashboard:banner.aiActive') : t('dashboard:banner.aiDisabled')}
        </span>
      </span>
      {/* Maintenance + Alerts — Admin/Op only */}
      {canWrite && (
        <>
          {/* Maintenance — read-only info (the Quick Actions bar already links to the page). */}
          <span className="flex items-center gap-1.5 text-sm">
            <CalendarSettings size={14} className={activeMaintenance > 0 ? 'text-amber-500' : 'text-outline'} />
            <span className={activeMaintenance > 0 ? 'text-amber-600' : 'text-on-surface-variant'}>
              {activeMaintenance > 0
                ? t('dashboard:banner.activeMaintenance', { count: activeMaintenance })
                : t('dashboard:banner.enabledMaintenance', { count: enabledMaintenance })}
            </span>
          </span>
          <button
            type="button"
            onClick={() => navigate('/alerts')}
            className={`np-btn np-btn-sm ${failedDeliveries > 0 ? 'np-btn-danger' : 'np-btn-success'}`}
            title={t('dashboard:banner.viewAlerts')}
          >
            {failedDeliveries > 0 ? <SecurityServices size={14} /> : <Certificate size={14} />}
            {failedDeliveries > 0
              ? t('dashboard:banner.failedDeliveries', { count: failedDeliveries })
              : <span className="flex items-center gap-1"><Notification size={13} />{t('dashboard:banner.noFailedDeliveries')}</span>}
          </button>
        </>
      )}
    </div>
  );
}
