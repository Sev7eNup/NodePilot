import {
  Activity,
  BareMetalServer,
  Branch,
  ChartLine,
  ChartLineData,
  ChartPie,
  CheckmarkFilled,
  CircleDash,
  DocumentUnknown,
  ErrorFilled,
  FlashFilled,
  Growth,
  Locked,
  Meter,
  Play,
  Screen,
  SecurityServices,
  Subtract,
  TaskComplete,
  Time,
  WarningAltFilled,
} from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { api } from '../api/client';
import { EChart } from '../components/common/EChart';
import { useObservabilityConfig, useTelemetrySummary } from '../api/observability';
import { useDashboardFeed } from '../hooks/useDashboardFeed';
import { useMinuteTick } from '../hooks/useMinuteTick';
import { SystemHealthBanner } from '../components/dashboard/SystemHealthBanner';
import { DashboardQuickActions } from '../components/dashboard/DashboardQuickActions';
import { StatusBadge } from '../components/common/StatusBadge';
import { formatDate, formatDuration, formatNumber, formatRelative, formatRelativeFuture } from '../lib/format';
import { TRIGGER_BADGE_META as TRIGGER_META } from '../lib/triggerBadgeMeta';

interface DashboardStats {
  workflowsTotal: number;
  workflowsEnabled: number;
  machinesTotal: number;
  machinesReachable: number;
  executionsTotal: number;
  last24h: ExecutionCounts;
  last24hBuckets: HourBucket[];
  topWorkflows: TopWorkflow[];
  running: RunningExecutionInfo[];
  recent: RecentExecutionInfo[];
  armedTriggers?: ArmedTriggerInfo[];
  pendingCount: number;
  runningCount: number;
  longRunningCount: number;
  failingWorkflows: FailingWorkflow[];
  editLocks: EditLockInfo[];
  healthHeartbeats: HealthHeartbeatInfo[];
  databaseProvider: string;
  clusterRole: string | null;
  recentAudit: DashboardAuditEvent[] | null;
  llmEnabled: boolean;
}
interface ArmedTriggerInfo {
  workflowId: string;
  workflowName: string;
  triggerTypes: string[];
  nextFireUtc: string | null;
  nextFireKind: 'cron' | 'event-driven' | 'polling' | null;
  pollIntervalSeconds: number | null;
}
interface ExecutionCounts {
  total: number; succeeded: number; failed: number; running: number; cancelled: number;
}
interface HourBucket {
  hourStart: string; succeeded: number; failed: number; cancelled: number;
}
interface TopWorkflow {
  id: string; name: string; runCount: number; successCount: number; failCount: number;
  avgDurationMs: number | null; p95DurationMs: number | null;
}
interface FailingWorkflow {
  id: string; name: string; failCount: number; runCount: number; lastFailureAt: string | null;
  prevFailCount?: number; prevRunCount?: number;
}
interface EditLockInfo {
  workflowId: string; workflowName: string; lockOwnerUserName: string; lockedAt: string;
}
export interface HealthHeartbeatInfo {
  serviceName: string; lastHeartbeatAt: string; expectedIntervalSeconds: number; status: string | null; isStale: boolean;
}
interface DashboardAuditEvent {
  timestamp: string; actorUserName: string | null; action: string; resourceType: string | null; resourceId: string | null;
}
interface RunningExecutionInfo {
  id: string; workflowId: string; workflowName: string; status: string; startedAt: string; triggeredBy: string | null;
}
interface RecentExecutionInfo {
  id: string; workflowId: string; workflowName: string; status: string;
  startedAt: string; completedAt: string | null; durationMs: number | null; triggeredBy: string | null;
}

const LONG_RUNNING_THRESHOLD_MS = 30 * 60 * 1000;
const STALE_LOCK_THRESHOLD_MS = 24 * 3600 * 1000;

// Selectable dashboard window (hours). WINDOW_KEY maps each value to its i18n label slug.
const WINDOW_OPTIONS = [1, 24, 168, 720] as const;
const WINDOW_KEY: Record<number, string> = { 1: '1h', 24: '24h', 168: '7d', 720: '30d' };
// Map an execution status string to the executions:status.* i18n key (mirrors StatusBadge).
const STATUS_KEY: Record<string, string> = {
  Succeeded: 'succeeded', Failed: 'failed', Running: 'running',
  Pending: 'pending', Cancelled: 'cancelled', Skipped: 'skipped',
};
// Donut segment key -> execution status string (for the Recent-table filter).
// 'statusOther' folds Pending/Paused/Skipped together, so it cannot filter cleanly.
const RUN_STATUS_FILTER_FOR_KEY: Record<string, string | null> = {
  succeeded: 'Succeeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
  running: 'Running',
  statusOther: null,
};

// Semantic health colours used by both the hero gauge and the KPI accents.
const HEALTH = { ok: '#22c55e', warn: '#f59e0b', bad: '#ef4444', muted: '#9ca3af' };
function healthColor(rate: number | null): string {
  if (rate == null) return HEALTH.muted;
  if (rate >= 95) return HEALTH.ok;
  if (rate >= 80) return HEALTH.warn;
  return HEALTH.bad;
}

/** Reads the live, np-shell-scoped design tokens so ECharts matches the theme
 *  (and re-reads on light/dark toggle). Returns empty strings under jsdom — every
 *  chart builder falls back to literals, so charts never depend on this resolving. */
interface ThemeTokens {
  onSurfaceVariant: string; outlineVariant: string; surfaceHigh: string; onSurface: string;
  // Accent gradient stops (primary-container → primary) so accent-coloured charts
  // recolour with the active skin instead of hardcoding the dark-orange ramp.
  primaryContainer: string; primary: string;
}
function useThemeTokens(): { probeRef: React.RefObject<HTMLDivElement | null>; tokens: ThemeTokens } {
  const probeRef = useRef<HTMLDivElement>(null);
  const [tokens, setTokens] = useState<ThemeTokens>({
    onSurfaceVariant: '', outlineVariant: '', surfaceHigh: '', onSurface: '',
    primaryContainer: '', primary: '',
  });
  useEffect(() => {
    const read = () => {
      const el = probeRef.current;
      if (!el) return;
      const cs = getComputedStyle(el);
      const g = (v: string) => cs.getPropertyValue(v).trim();
      setTokens({
        onSurfaceVariant: g('--color-on-surface-variant'),
        outlineVariant: g('--color-outline-variant'),
        surfaceHigh: g('--color-surface-high'),
        onSurface: g('--color-on-surface'),
        primaryContainer: g('--color-primary-container'),
        primary: g('--color-primary'),
      });
    };
    read();
    const mo = new MutationObserver(read);
    // Watch `data-skin` too: switching e.g. dark → dark-lila keeps the same `class`
    // (both are `dark`/`np-accent-remap`) and only flips `data-skin`, so a class-only
    // filter would miss it and the charts would keep the old skin until a reload.
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'data-skin'] });
    return () => mo.disconnect();
  }, []);
  return { probeRef, tokens };
}

export function DashboardPage() {
  const { t } = useTranslation(['dashboard', 'executions', 'common']);
  const navigate = useNavigate();
  const { probeRef, tokens } = useThemeTokens();
  // Caller-selected time window for the hero/area/donut/success-trend charts. Top/failing
  // stay on a fixed 7 d window by backend design (recent-failure hotspots), so they don't
  // empty out at windowHours=1.
  const [windowHours, setWindowHours] = useState(24);
  const windowLabel = t(`dashboard:window.${WINDOW_KEY[windowHours] ?? '24h'}`);
  // Live status transitions via the SignalR ops feed (JoinOperationsFeed). It debounced-
  // invalidates ['dashboard-stats'] as soon as an execution starts/finishes — Running,
  // Queue and Recent KPIs update in near-real-time instead of waiting up to 30s. The
  // 120s polling below is now just a safety net for reconnects/missed events.
  useDashboardFeed();
  const { data: stats, isLoading } = useQuery({
    queryKey: ['dashboard-stats', windowHours],
    queryFn: () => api.get<DashboardStats>(`/stats/dashboard?windowHours=${windowHours}`),
    refetchInterval: 120_000,
  });
  // Donut-segment click filters the Recent Executions table client-side (the backend only
  // returns the latest 10, so this is a "of the last 10" filter — full filtering stays /executions).
  const [statusFilter, setStatusFilter] = useState<string | null>(null);

  if (isLoading || !stats) {
    return (
      <div ref={probeRef} className="max-w-[1600px] mx-auto">
        <p className="text-outline">{t('common:loadingDots')}</p>
      </div>
    );
  }

  const successRate = stats.last24h.total > 0
    ? Math.round((stats.last24h.succeeded / Math.max(1, stats.last24h.succeeded + stats.last24h.failed)) * 100)
    : null;

  const recentFiltered = statusFilter
    ? stats.recent.filter((e) => e.status === statusFilter)
    : stats.recent;

  return (
    <div ref={probeRef} className="max-w-[1600px] mx-auto">
      <SystemHealthBanner
        clusterRole={stats.clusterRole}
        databaseProvider={stats.databaseProvider}
        heartbeats={stats.healthHeartbeats}
        llmEnabled={stats.llmEnabled}
      />
      <DashboardQuickActions longRunningCount={stats.longRunningCount} />
      <div className="flex flex-wrap items-center justify-between gap-3 mb-5">
        <div className="np-segmented">
          {WINDOW_OPTIONS.map((h) => (
            <button
              key={h}
              type="button"
              onClick={() => { setWindowHours(h); setStatusFilter(null); }}
              className={`np-segment ${h === windowHours ? 'is-active' : ''}`}
            >
              {t(`dashboard:window.${WINDOW_KEY[h]}`)}
            </button>
          ))}
        </div>
        <p className="text-xs text-outline">{t('dashboard:autoRefresh')}</p>
      </div>
      {/* HERO — radial gauge (left) + KPI cluster (centre) + live runs (right). */}
      <div className="grid grid-cols-1 xl:grid-cols-4 gap-5 mb-5">
        <div className="np-fade-up" style={{ animationDelay: '0ms' }}>
          <HeroGauge
            successRate={successRate}
            succeeded={stats.last24h.succeeded}
            failed={stats.last24h.failed}
            running={stats.last24h.running}
            windowLabel={windowLabel}
            tokens={tokens}
          />
        </div>
        <div className="xl:col-span-2 np-fade-up" style={{ animationDelay: '60ms' }}>
          <KpiGrid stats={stats} windowLabel={windowLabel} />
        </div>
        <div className="np-fade-up flex flex-col" style={{ animationDelay: '90ms' }}>
          <div className="np-card p-5 flex flex-col overflow-hidden flex-1">
            <h3 className="font-semibold text-on-surface mb-3 flex items-center gap-2 shrink-0">
              <CircleDash size={16} className="text-blue-500 animate-spin" />
              {t('dashboard:currentlyRunning')}
            </h3>
            {/* Keep the long list out of grid intrinsic sizing; it scrolls inside the row height. */}
            <div className="relative flex-1 min-h-[220px] xl:min-h-0">
              <div className="absolute inset-0 overflow-y-auto">
                <RunningList items={stats.running} onOpen={(id) => navigate(`/executions?id=${id}`)} />
              </div>
            </div>
          </div>
        </div>
      </div>
      {/* Window trend — gradient area chart frames the rest of the dashboard. */}
      <div className="np-card p-5 mb-5 np-fade-up" style={{ animationDelay: '120ms' }}>
        <div className="flex items-center justify-between mb-2">
          <h3 className="font-semibold text-on-surface flex items-center gap-2">
            <Activity size={16} className="text-primary" />
            {t('dashboard:executionsWindow', { window: windowLabel })}
          </h3>
          <div className="flex items-center gap-3 text-xs text-on-surface-variant">
            <span className="flex items-center gap-1"><span className="w-2 h-2 bg-green-500 rounded-sm" /> {t('dashboard:succeeded')}</span>
            <span className="flex items-center gap-1"><span className="w-2 h-2 bg-red-500 rounded-sm" /> {t('dashboard:failed')}</span>
            <span className="flex items-center gap-1"><span className="w-2 h-2 bg-outline-variant rounded-sm" /> {t('dashboard:cancelled')}</span>
          </div>
        </div>
        <HourlyAreaChart buckets={stats.last24hBuckets} windowHours={windowHours} tokens={tokens} />
      </div>
      {/* Three compact insight charts — status, quality trend, performance. */}
      <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-5 mb-5">
        <div className="np-fade-up" style={{ animationDelay: '160ms' }}>
          <Panel title={t('dashboard:runStatusWindow', { window: windowLabel })} icon={ChartPie} iconClass="text-emerald-500" className="h-full">
            <RunStatusDonut counts={stats.last24h} tokens={tokens} onSelect={(status) => setStatusFilter(status)} />
          </Panel>
        </div>
        <div className="np-fade-up" style={{ animationDelay: '200ms' }}>
          <Panel title={t('dashboard:successTrendWindow', { window: windowLabel })} icon={ChartLine} iconClass="text-primary" className="h-full">
            <SuccessRateTrend buckets={stats.last24hBuckets} windowHours={windowHours} tokens={tokens} />
          </Panel>
        </div>
        <div className="np-fade-up" style={{ animationDelay: '240ms' }}>
          <Panel title={t('dashboard:p95TopWorkflows')} icon={Time} iconClass="text-amber-500" className="h-full">
            <P95WorkflowBars items={stats.topWorkflows} tokens={tokens} />
          </Panel>
        </div>
      </div>
      <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-5 mb-5">
        <Panel title={t('dashboard:armedTriggers')} icon={FlashFilled} iconClass="text-amber-500" divided>
          <ArmedTriggersList items={stats.armedTriggers ?? []} onOpen={(id) => navigate(`/workflows/${id}`)} />
        </Panel>

        <Panel title={t('dashboard:topWorkflows')} icon={Growth} iconClass="text-emerald-500" divided>
          <TopWorkflowsList items={stats.topWorkflows} onOpen={(id) => navigate(`/workflows/${id}`)} />
        </Panel>

        <Panel title={t('dashboard:failingWorkflows')} icon={DocumentUnknown} iconClass="text-red-500" divided>
          <FailingWorkflowsList items={stats.failingWorkflows} onOpen={(id) => navigate(`/workflows/${id}`)} />
        </Panel>
      </div>
      <div className="mb-5">
      <Panel title={t('dashboard:recentExecutions')} icon={Time} iconClass="text-on-surface-variant">
        {statusFilter && (
          <div className="mb-3 flex items-center gap-2 text-xs">
            <span className="inline-flex items-center gap-1 px-2 py-1 rounded-full bg-surface-container text-on-surface-variant">
              {t('dashboard:filterByStatus', { status: t(`executions:status.${STATUS_KEY[statusFilter] ?? ''}`) ?? statusFilter })}
              <button type="button" className="np-btn np-btn-icon-sm np-btn-ghost" onClick={() => setStatusFilter(null)}>
                <ErrorFilled size={13} />
              </button>
            </span>
          </div>
        )}
        {recentFiltered.length === 0 ? (
          <EmptyState text={t('dashboard:noExecutionsYet')} />
        ) : (
          <div className="overflow-x-auto -mx-4">
            <table className="w-full text-sm">
              <thead className="text-xs uppercase tracking-wide text-outline">
                <tr>
                  <th className="text-left px-4 py-2 font-medium">{t('dashboard:tableHeaders.workflow')}</th>
                  <th className="text-left px-4 py-2 font-medium">{t('dashboard:tableHeaders.status')}</th>
                  <th className="text-left px-4 py-2 font-medium">{t('dashboard:tableHeaders.trigger')}</th>
                  <th className="text-left px-4 py-2 font-medium">{t('dashboard:tableHeaders.started')}</th>
                  <th className="text-right px-4 py-2 font-medium">{t('dashboard:tableHeaders.duration')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-outline-variant/30">
                {recentFiltered.map((e) => (
                  <tr
                    key={e.id}
                    className="cursor-pointer hover:bg-surface-low"
                    onClick={() => navigate(`/executions?id=${e.id}`)}
                  >
                    <td className="px-4 py-2 font-medium text-on-surface">{e.workflowName}</td>
                    <td className="px-4 py-2"><StatusBadge status={e.status} /></td>
                    <td className="px-4 py-2 text-on-surface-variant text-xs">{e.triggeredBy ?? t('common:dash')}</td>
                    <td className="px-4 py-2 text-on-surface-variant text-xs" title={formatDate(e.startedAt)}>
                      {formatRelative(e.startedAt)}
                    </td>
                    <td className="px-4 py-2 text-on-surface-variant text-xs text-right tabular-nums">
                      {formatDuration(e.durationMs)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      </div>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-5 mb-5">
        {stats.recentAudit != null && (
          <Panel title={t('dashboard:recentAudit')} icon={SecurityServices} iconClass="text-purple-500">
            <RecentAuditList items={stats.recentAudit} />
          </Panel>
        )}

        <Panel title={t('dashboard:editLocks')} icon={Locked} iconClass="text-indigo-500">
          <EditLocksList items={stats.editLocks} onOpen={(id) => navigate(`/workflows/${id}`)} />
        </Panel>
      </div>
      <TelemetrySection />
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Hero gauge
// ─────────────────────────────────────────────────────────────────────────────

function HeroGauge({
  successRate, succeeded, failed, running, windowLabel, tokens,
}: Readonly<{
  successRate: number | null; succeeded: number; failed: number; running: number;
  windowLabel: string; tokens: ThemeTokens;
}>) {
  const { t } = useTranslation(['dashboard']);
  const color = healthColor(successRate);
  const track = tokens.outlineVariant || 'rgba(255,255,255,0.10)';
  const option = useMemo<EChartsOption>(() => ({
    series: [{
      type: 'gauge',
      startAngle: 220,
      endAngle: -40,
      min: 0,
      max: 100,
      radius: '94%',
      center: ['50%', '56%'],
      pointer: { show: false },
      anchor: { show: false },
      title: { show: false },
      detail: { show: false },
      axisTick: { show: false },
      splitLine: { show: false },
      axisLabel: { show: false },
      axisLine: { lineStyle: { width: 16, color: [[1, track]] } },
      progress: {
        show: true,
        width: 16,
        roundCap: true,
        itemStyle: { color, shadowBlur: 22, shadowColor: color },
      },
      data: [{ value: successRate ?? 0 }],
    }],
  }), [successRate, color, track]);

  const runningSuffix = running > 0 ? ` · ${running}↻` : '';
  return (
    <div className="np-card np-card-hero p-4 sm:p-6 h-full flex flex-col">
      <div className="flex items-center gap-1.5 text-[10px] uppercase tracking-wider font-semibold text-on-surface-variant">
        <CheckmarkFilled size={12} className="shrink-0" style={{ color }} />
        <span className="truncate">{t('dashboard:successRateWindow', { window: windowLabel })}</span>
      </div>
      <div className="relative flex-1 min-h-[150px] sm:min-h-[210px]">
        <EChart option={option} className="absolute inset-0" ariaLabel={t('dashboard:successRateWindow', { window: windowLabel })} />
        <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none">
          <span
            className="text-4xl sm:text-5xl font-bold tabular-nums leading-none"
            style={{ color, textShadow: `0 0 28px ${color}55` }}
          >
            {successRate == null ? '–' : `${successRate}%`}
          </span>
          <span className="text-xs text-on-surface-variant mt-2 tabular-nums">
            {succeeded}✓ · {failed}✗{runningSuffix}
          </span>
        </div>
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// KPI cluster
// ─────────────────────────────────────────────────────────────────────────────

function KpiGrid({ stats, windowLabel }: Readonly<{ stats: DashboardStats; windowLabel: string }>) {
  const { t } = useTranslation(['dashboard', 'common']);
  const machinesAllOnline = stats.machinesTotal === 0 || stats.machinesReachable === stats.machinesTotal;
  const scheduler = stats.healthHeartbeats.find((h) => h.serviceName.toLowerCase().includes('scheduler')
    || h.serviceName.toLowerCase().includes('trigger'));

  return (
    <div className="grid grid-cols-2 sm:grid-cols-3 gap-4 h-full content-stretch">
      <KpiCard
        icon={Branch} iconColor="text-primary"
        label={t('dashboard:workflows')} value={formatNumber(stats.workflowsTotal)}
        hint={t('dashboard:workflowsEnabled', { count: stats.workflowsEnabled })}
      />
      <KpiCard
        icon={Screen} iconColor="text-on-surface-variant"
        label={t('dashboard:machines')} value={formatNumber(stats.machinesTotal)}
        hint={t('dashboard:machinesReachable', { count: stats.machinesReachable })}
        hintColor={machinesAllOnline ? undefined : 'text-amber-600'}
      />
      <KpiCard
        icon={Play} iconColor="text-on-surface-variant"
        label={t('dashboard:runsWindow', { window: windowLabel })} value={formatNumber(stats.last24h.total)}
        hint={t('dashboard:runsAllTime', { count: stats.executionsTotal })}
      />
      <KpiCard
        icon={TaskComplete} iconColor="text-on-surface-variant"
        label={t('dashboard:queueDepth')}
        value={t('dashboard:queueShort', { pending: stats.pendingCount, running: stats.runningCount })}
        hint={stats.longRunningCount > 0 ? t('dashboard:longRunning', { count: stats.longRunningCount }) : undefined}
        hintColor={stats.longRunningCount > 0 ? 'text-red-600' : undefined}
        hintIcon={stats.longRunningCount > 0 ? WarningAltFilled : undefined}
      />
      <KpiCard
        icon={Activity}
        iconColor={scheduler ? (scheduler.isStale ? 'text-red-600' : 'text-emerald-500') : 'text-outline'}
        label={t('dashboard:scheduler')}
        value={scheduler ? (scheduler.isStale ? t('dashboard:schedulerStale') : t('dashboard:schedulerOk')) : t('dashboard:schedulerNoData')}
        valueColor={scheduler?.isStale ? 'text-red-600' : undefined}
        hint={scheduler ? formatRelative(scheduler.lastHeartbeatAt) : undefined}
      />
      {stats.clusterRole && (
        <KpiCard
          icon={BareMetalServer} iconColor="text-primary"
          label={t('dashboard:clusterRole')}
          value={stats.clusterRole === 'leader' ? t('dashboard:clusterLeader') : t('dashboard:clusterStandby')}
        />
      )}
    </div>
  );
}

function KpiCard({
  icon: Icon, iconColor, label, value, valueColor, hint, hintColor, hintIcon: HintIcon,
}: Readonly<{
  icon: React.ElementType;
  iconColor: string;
  label: string;
  value: string;
  valueColor?: string;
  hint?: string;
  hintColor?: string;
  hintIcon?: React.ElementType;
}>) {
  return (
    <div className="np-card p-4 min-w-0 flex flex-col justify-center">
      <div className="flex items-center gap-1.5 text-[10px] uppercase tracking-wider font-semibold text-on-surface-variant">
        <Icon size={12} className={`${iconColor} shrink-0`} />
        <span className="truncate">{label}</span>
      </div>
      <div className="mt-2.5">
        <p
          className={`text-[1.7rem] font-bold tabular-nums leading-none truncate ${valueColor ?? 'text-on-surface'}`}
          title={value}
        >
          {value}
        </p>
      </div>
      {hint && (
        <p className={`text-xs mt-2 flex items-center gap-1 ${hintColor ?? 'text-outline'}`}>
          {HintIcon && <HintIcon size={10} className="shrink-0" />}
          <span className="truncate">{hint}</span>
        </p>
      )}
    </div>
  );
}

function RunningList({ items, onOpen }: Readonly<{ items: RunningExecutionInfo[]; onOpen: (id: string) => void }>) {
  const { t } = useTranslation(['dashboard']);
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:nothingRunning')} />;
  }
  return (
    <ul className="divide-y divide-outline-variant/30">
      {items.map((r) => (
        <li
          key={r.id}
          className="np-row py-2 flex items-center justify-between cursor-pointer -mx-2 px-2"
          onClick={() => onOpen(r.id)}
          onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen(r.id)}
          role="button"
          tabIndex={0}
        >
          <div className="flex items-center gap-2 min-w-0">
            <CircleDash size={14} className="text-blue-500 animate-spin shrink-0" />
            <div className="min-w-0">
              <p className="font-medium text-on-surface truncate">{r.workflowName}</p>
              <p className="text-xs text-outline">
                {t('dashboard:startedBy', { trigger: r.triggeredBy ?? t('dashboard:manual'), time: formatRelative(r.startedAt) })}
              </p>
            </div>
          </div>
          <LiveDuration startedAt={r.startedAt} />
        </li>
      ))}
    </ul>
  );
}

function TopWorkflowsList({ items, onOpen }: Readonly<{ items: TopWorkflow[]; onOpen: (id: string) => void }>) {
  const { t } = useTranslation(['dashboard']);
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:noExecutions7d')} />;
  }
  return (
    <ul className="space-y-0.5">
      {items.map((w, i) => {
        const terminal = w.successCount + w.failCount;
        const pct = terminal > 0 ? (w.successCount / terminal) * 100 : 0;
        const barClass = terminal === 0 ? '' : pct >= 95 ? 'np-bar-ok' : pct >= 80 ? 'np-bar-warn' : 'np-bar-bad';
        const pctColor = terminal === 0 ? 'text-outline'
          : pct >= 95 ? 'text-emerald-600 dark:text-emerald-400'
          : pct >= 80 ? 'text-amber-600 dark:text-amber-400'
          : 'text-red-600 dark:text-red-400';
        return (
          <li
            key={w.id}
            className="np-row cursor-pointer -mx-2 px-2 py-1.5 rounded-md flex flex-col gap-0.5"
            onClick={() => onOpen(w.id)}
            onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen(w.id)}
            role="button"
            tabIndex={0}
          >
            <div className="flex items-center gap-2">
              <span className="text-[10px] font-mono text-outline/60 tabular-nums w-4 shrink-0">#{i + 1}</span>
              <span className="text-xs font-medium text-on-surface truncate min-w-0">{w.name}</span>
              <div className="flex-1 h-1 bg-surface-container rounded-full overflow-hidden shrink-0" style={{ minWidth: '3rem' }}>
                <div className={`np-bar h-full ${barClass || 'bg-surface-highest'}`} style={{ width: `${terminal === 0 ? 100 : pct}%` }} />
              </div>
              <span
                className={`text-xs font-semibold tabular-nums shrink-0 w-9 text-right ${pctColor}`}
                title={terminal > 0 ? `${w.successCount}/${terminal}` : undefined}
              >
                {terminal === 0 ? '—' : `${Math.round(pct)}%`}
              </span>
            </div>
            <div className="flex items-center gap-1 pl-6 text-[10px] text-outline/70 tabular-nums">
              <span>{w.runCount.toLocaleString()} runs</span>
              {w.avgDurationMs != null && <span>· {t('dashboard:avgDuration', { duration: formatDuration(w.avgDurationMs) })}</span>}
              {w.p95DurationMs != null && <span>· {t('dashboard:p95', { duration: formatDuration(w.p95DurationMs) })}</span>}
            </div>
          </li>
        );
      })}
    </ul>
  );
}

function FailingWorkflowsList({ items, onOpen }: Readonly<{ items: FailingWorkflow[]; onOpen: (id: string) => void }>) {
  const { t } = useTranslation(['dashboard']);
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:noFailingWorkflows')} />;
  }
  return (
    <ul className="space-y-0.5">
      {items.map((w) => {
        const pct = w.runCount > 0 ? (w.failCount / w.runCount) * 100 : 100;
        const currentRate = w.runCount > 0 ? w.failCount / w.runCount : 1;
        const prevRate = (w.prevRunCount ?? 0) > 0 ? (w.prevFailCount ?? 0) / w.prevRunCount! : null;
        const trend = prevRate == null ? null
          : currentRate > prevRate + 0.05 ? 'up'
          : currentRate < prevRate - 0.05 ? 'down'
          : 'flat';
        return (
          <li
            key={w.id}
            className="np-row cursor-pointer -mx-2 px-2 py-1.5 rounded-md flex flex-col gap-0.5"
            onClick={() => onOpen(w.id)}
            onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen(w.id)}
            role="button"
            tabIndex={0}
          >
            <div className="flex items-center gap-2">
              <span className="text-xs font-medium text-on-surface truncate min-w-0">{w.name}</span>
              <div className="flex-1 h-1 bg-surface-container rounded-full overflow-hidden shrink-0" style={{ minWidth: '3rem' }}>
                <div className="np-bar np-bar-bad h-full" style={{ width: `${pct}%` }} />
              </div>
              <span
                className="text-xs font-semibold text-red-600 dark:text-red-400 tabular-nums shrink-0 w-9 text-right"
                title={`${w.failCount}/${w.runCount}`}
              >
                {Math.round(pct)}%
              </span>
              {trend === 'up' && (
                <span title={t('dashboard:trendWorse')} className="shrink-0 inline-flex">
                  <Growth size={13} className="text-red-500" />
                </span>
              )}
              {trend === 'down' && (
                <span title={t('dashboard:trendBetter')} className="shrink-0 inline-flex">
                  <ChartLineData size={13} className="text-emerald-500" />
                </span>
              )}
              {trend === 'flat' && (
                <span title={t('dashboard:trendStable')} className="shrink-0 inline-flex">
                  <Subtract size={13} className="text-outline/50" />
                </span>
              )}
              <span className="bg-red-500/15 text-red-600 dark:text-red-400 px-1.5 py-px rounded-full text-[10px] font-semibold shrink-0 tabular-nums">
                {w.failCount}✗
              </span>
            </div>
            <p className="text-[10px] text-outline/70 tabular-nums">
              {t('dashboard:ofRuns', { count: w.runCount })}
              {w.lastFailureAt && ` · ${t('dashboard:lastFailure', { time: formatRelative(w.lastFailureAt) })}`}
            </p>
          </li>
        );
      })}
    </ul>
  );
}

function ArmedTriggersList({ items, onOpen }: Readonly<{ items: ArmedTriggerInfo[]; onOpen: (id: string) => void }>) {
  const { t } = useTranslation(['dashboard']);
  // Single ticker for all rows — minute granularity is enough for "in 5m" labels.
  const now = useMinuteTick();
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:noArmedTriggers')} />;
  }
  return (
    <ul className="space-y-0.5 max-h-64 overflow-y-auto pr-1">
      {items.map((w) => (
        <li
          key={w.workflowId}
          className="np-row py-1.5 flex items-center justify-between gap-2 cursor-pointer -mx-2 px-2 rounded-md"
          onClick={() => onOpen(w.workflowId)}
          onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen(w.workflowId)}
          role="button"
          tabIndex={0}
        >
          <div className="flex items-center gap-1.5 min-w-0 flex-1">
            <span className="text-xs font-medium text-on-surface truncate">{w.workflowName}</span>
            <div className="flex flex-wrap gap-0.5 shrink-0">
              {w.triggerTypes.map((tt) => {
                const meta = TRIGGER_META[tt] ?? { label: tt, icon: FlashFilled, className: 'bg-surface-container text-on-surface' };
                const Icon = meta.icon;
                return (
                  <span
                    key={tt}
                    className={`inline-flex items-center gap-0.5 px-1 py-px rounded text-[10px] font-medium ${meta.className}`}
                    title={tt}
                  >
                    <Icon size={10} />
                    {meta.label}
                  </span>
                );
              })}
            </div>
          </div>
          <NextFireBadge nextFireUtc={w.nextFireUtc} nextFireKind={w.nextFireKind} pollIntervalSeconds={w.pollIntervalSeconds} now={now} />
        </li>
      ))}
    </ul>
  );
}

function NextFireBadge({
  nextFireUtc, nextFireKind, pollIntervalSeconds, now,
}: Readonly<{
  nextFireUtc: string | null;
  nextFireKind: string | null;
  pollIntervalSeconds: number | null;
  now: number;
}>) {
  const { t } = useTranslation(['dashboard']);
  if (nextFireKind === 'cron' && nextFireUtc) {
    return (
      <span className="inline-flex items-center gap-1 bg-purple-500/10 text-purple-600 dark:text-purple-400 px-2 py-0.5 rounded-full text-xs font-medium tabular-nums shrink-0">
        <Time size={11} />
        {formatRelativeFuture(nextFireUtc, now)}
      </span>
    );
  }
  if (nextFireKind === 'polling' && pollIntervalSeconds != null) {
    return (
      <span className="inline-flex items-center gap-1 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 px-2 py-0.5 rounded-full text-xs font-medium shrink-0">
        {t('dashboard:pollInterval', { seconds: pollIntervalSeconds })}
      </span>
    );
  }
  return (
    <span className="text-[11px] text-outline shrink-0">{t('dashboard:eventDriven')}</span>
  );
}

function EditLocksList({ items, onOpen }: Readonly<{ items: EditLockInfo[]; onOpen: (id: string) => void }>) {
  const { t } = useTranslation(['dashboard']);
  const now = useMinuteTick();
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:noEditLocks')} />;
  }
  return (
    <ul className="divide-y divide-outline-variant/30 max-h-80 overflow-y-auto">
      {items.map((l) => {
        const age = now - new Date(l.lockedAt).getTime();
        const isStale = age > STALE_LOCK_THRESHOLD_MS;
        return (
          <li
            key={l.workflowId}
            className="np-row py-2 flex items-center justify-between gap-2 cursor-pointer -mx-2 px-2"
            onClick={() => onOpen(l.workflowId)}
            onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen(l.workflowId)}
            role="button"
            tabIndex={0}
          >
            <div className="flex items-center gap-2 min-w-0">
              <Locked size={12} className={`shrink-0 ${isStale ? 'text-amber-600' : 'text-indigo-500'}`} />
              <div className="min-w-0">
                <p className="font-medium text-on-surface truncate">{l.workflowName}</p>
                <p className={`text-xs ${isStale ? 'text-amber-600' : 'text-outline'}`}>
                  {t('dashboard:editLockBy', { user: l.lockOwnerUserName, time: formatRelative(l.lockedAt) })}
                </p>
              </div>
            </div>
            {isStale && <WarningAltFilled size={14} className="text-amber-600 shrink-0" />}
          </li>
        );
      })}
    </ul>
  );
}

function RecentAuditList({ items }: Readonly<{ items: DashboardAuditEvent[] }>) {
  const { t } = useTranslation(['dashboard']);
  if (items.length === 0) {
    return <EmptyState text={t('dashboard:noRecentAudit')} />;
  }
  return (
    <ul className="divide-y divide-outline-variant/30">
      {items.map((a, i) => (
        <li key={i} className="py-2 flex items-center gap-3 text-xs">
          <span className="text-outline tabular-nums shrink-0 w-24" title={formatDate(a.timestamp)}>
            {formatRelative(a.timestamp)}
          </span>
          <span className="text-on-surface-variant shrink-0 w-24 truncate">
            {a.actorUserName ?? t('dashboard:auditSystemActor')}
          </span>
          <span className="font-medium text-on-surface truncate">{a.action}</span>
          {a.resourceType && (
            <span className="text-outline truncate hidden md:inline">{a.resourceType}</span>
          )}
        </li>
      ))}
    </ul>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// 24h gradient area chart
// ─────────────────────────────────────────────────────────────────────────────

const AREA_SERIES = [
  { key: 'succeeded' as const, line: '#22c55e', top: 'rgba(34,197,94,0.82)', mid: 'rgba(34,197,94,0.38)', bot: 'rgba(34,197,94,0.02)', shadow: '#22c55e', labelKey: 'dashboard:succeeded' },
  { key: 'failed' as const,    line: '#ef4444', top: 'rgba(239,68,68,0.55)', mid: 'rgba(239,68,68,0.22)', bot: 'rgba(239,68,68,0.02)', shadow: null, labelKey: 'dashboard:failed' },
  { key: 'cancelled' as const, line: '#9ca3af', top: 'rgba(156,163,175,0.35)', mid: 'rgba(156,163,175,0.12)', bot: 'rgba(156,163,175,0.02)', shadow: null, labelKey: 'dashboard:cancelled' },
];

function HourlyAreaChart({ buckets, windowHours, tokens }: Readonly<{ buckets: HourBucket[]; windowHours: number; tokens: ThemeTokens }>) {
  const { t } = useTranslation(['dashboard']);
  const axisColor = tokens.onSurfaceVariant || '#9ca3af';
  const tipBg = tokens.surfaceHigh || '#1f1b16';
  const tipText = tokens.onSurface || '#ece7e1';

  const option = useMemo<EChartsOption>(() => {
    // Hourly windows label "HH:00"; multi-day windows label the date "MM-DD". The backend
    // always emits ≤24 buckets, so every 3rd label keeps the axis legible in both modes.
    const multiDay = windowHours > 24;
    const formatLabel = (iso: string) => {
      const d = new Date(iso);
      if (multiDay) {
        const pad = (n: number) => String(n).padStart(2, '0');
        return `${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
      }
      return `${String(d.getHours()).padStart(2, '0')}:00`;
    };
    const labels = buckets.map((b) => formatLabel(b.hourStart));
    return {
      grid: { left: 2, right: 4, top: 8, bottom: 18, containLabel: false },
      tooltip: {
        trigger: 'axis',
        backgroundColor: tipBg,
        borderWidth: 0,
        padding: [6, 10],
        textStyle: { color: tipText, fontSize: 11 },
        axisPointer: { type: 'line', lineStyle: { color: axisColor, opacity: 0.4 } },
      },
      xAxis: {
        type: 'category',
        boundaryGap: false,
        data: labels,
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: {
          color: axisColor,
          fontSize: 10,
          interval: (i: number) => i % 3 === 0,
          hideOverlap: true,
        },
      },
      yAxis: { type: 'value', show: false, min: 0 },
      series: AREA_SERIES.map((s) => ({
        name: t(s.labelKey),
        type: 'line',
        stack: 'total',
        smooth: 0.35,
        symbol: 'none',
        lineStyle: {
          width: 2.5,
          color: s.line,
          ...(s.shadow ? { shadowBlur: 14, shadowColor: s.shadow } : {}),
        },
        emphasis: { focus: 'series' },
        areaStyle: {
          color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [
            { offset: 0, color: s.top }, { offset: 0.45, color: s.mid }, { offset: 1, color: s.bot },
          ] },
        },
        data: buckets.map((b) => b[s.key]),
      })),
    };
  }, [buckets, windowHours, axisColor, tipBg, tipText, t]);

  if (buckets.length === 0) {
    return <div className="h-28 flex items-center justify-center"><EmptyState text={t('dashboard:noExecutionsYet')} /></div>;
  }
  return <EChart option={option} className="h-36 w-full" ariaLabel={t('dashboard:executionsWindow', { window: t(`dashboard:window.${WINDOW_KEY[windowHours] ?? '24h'}`) })} />;
}

// ─────────────────────────────────────────────────────────────────────────────
// Run-status donut — composition of the last 24h. `total` also counts
// Pending/Paused/Skipped (not broken out by the DTO), so the leftover is folded
// into an "Other" slice → the slices always sum to the centre total.
// ─────────────────────────────────────────────────────────────────────────────

function RunStatusDonut({ counts, tokens, onSelect }: Readonly<{ counts: ExecutionCounts; tokens: ThemeTokens; onSelect: (status: string | null) => void }>) {
  const { t } = useTranslation(['dashboard']);
  const tipBg = tokens.surfaceHigh || '#1f1b16';
  const tipText = tokens.onSurface || '#ece7e1';
  const border = tokens.surfaceHigh || '#1f1b16';

  const segments = useMemo(() => {
    const other = Math.max(0, counts.total - counts.succeeded - counts.failed - counts.cancelled - counts.running);
    return [
      { key: 'succeeded', value: counts.succeeded, color: HEALTH.ok },
      { key: 'failed', value: counts.failed, color: HEALTH.bad },
      { key: 'cancelled', value: counts.cancelled, color: HEALTH.muted },
      { key: 'running', value: counts.running, color: '#3b82f6' },
      { key: 'statusOther', value: other, color: '#64748b' },
    ].filter((s) => s.value > 0);
  }, [counts]);

  const option = useMemo<EChartsOption>(() => ({
    tooltip: {
      trigger: 'item',
      backgroundColor: tipBg,
      borderWidth: 0,
      padding: [6, 10],
      textStyle: { color: tipText, fontSize: 11 },
    },
    series: [{
      type: 'pie',
      radius: ['60%', '86%'],
      center: ['50%', '50%'],
      avoidLabelOverlap: false,
      label: { show: false },
      labelLine: { show: false },
      emphasis: { scale: true, scaleSize: 4 },
      itemStyle: { borderColor: border, borderWidth: 2, borderRadius: 4 },
      data: segments.map((s) => ({
        name: t(`dashboard:${s.key}`), value: s.value, itemStyle: { color: s.color },
        statusKey: s.key,
      })),
    }],
  }), [segments, tipBg, tipText, border, t]);

  // Stable handler identity: map the clicked segment's statusKey back to a status string.
  const handleClick = useCallback((params: unknown) => {
    const p = params as { data?: { statusKey?: string } };
    const key = p?.data?.statusKey;
    if (!key) return;
    onSelect(RUN_STATUS_FILTER_FOR_KEY[key] ?? null);
  }, [onSelect]);

  if (counts.total === 0) {
    return <div className="h-40 flex items-center justify-center"><EmptyState text={t('dashboard:noExecutionsYet')} /></div>;
  }
  return (
    <div>
      <div className="relative h-40">
        <EChart option={option} className="absolute inset-0 cursor-pointer" ariaLabel={t('dashboard:runStatus24h')} onClick={handleClick} />
        <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none">
          <span className="text-2xl font-bold tabular-nums leading-none text-on-surface">{formatNumber(counts.total)}</span>
          <span className="text-[11px] text-on-surface-variant mt-1">{t('dashboard:runsLabel')}</span>
        </div>
      </div>
      <div className="flex flex-wrap items-center justify-center gap-x-3 gap-y-1 mt-3 text-xs text-on-surface-variant">
        {segments.map((s) => (
          <span key={s.key} className="flex items-center gap-1 tabular-nums">
            <span className="w-2 h-2 rounded-sm" style={{ backgroundColor: s.color }} />
            {t(`dashboard:${s.key}`)} {formatNumber(s.value)}
          </span>
        ))}
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Success-rate trend — succeeded / (succeeded+failed) per hour. Gaps are honest
// (connectNulls:false); the tooltip carries the n so a 0/100% single-run hour
// reads sensibly rather than alarmingly.
// ─────────────────────────────────────────────────────────────────────────────

function SuccessRateTrend({ buckets, windowHours, tokens }: Readonly<{ buckets: HourBucket[]; windowHours: number; tokens: ThemeTokens }>) {
  const { t } = useTranslation(['dashboard']);
  const axisColor = tokens.onSurfaceVariant || '#9ca3af';
  const tipBg = tokens.surfaceHigh || '#1f1b16';
  const tipText = tokens.onSurface || '#ece7e1';
  const multiDay = windowHours > 24;

  const points = useMemo(() => buckets.map((b) => {
    const d = new Date(b.hourStart);
    const label = multiDay
      ? `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
      : `${String(d.getHours()).padStart(2, '0')}:00`;
    const terminal = b.succeeded + b.failed;
    return {
      label,
      succeeded: b.succeeded,
      terminal,
      rate: terminal > 0 ? Math.round((b.succeeded / terminal) * 1000) / 10 : null,
    };
  }), [buckets, multiDay]);

  const option = useMemo<EChartsOption>(() => ({
    grid: { left: 2, right: 6, top: 10, bottom: 18, containLabel: false },
    tooltip: {
      trigger: 'axis',
      backgroundColor: tipBg,
      borderWidth: 0,
      padding: [6, 10],
      textStyle: { color: tipText, fontSize: 11 },
      axisPointer: { type: 'line', lineStyle: { color: axisColor, opacity: 0.4 } },
      formatter: (params: unknown) => {
        const arr = params as Array<{ dataIndex: number }>;
        const p = points[arr[0]?.dataIndex];
        if (!p) return '';
        const rate = p.rate == null ? '–' : `${p.rate}%`;
        const ctx = p.terminal > 0 ? `<br/>${t('dashboard:successOfRuns', { succeeded: p.succeeded, terminal: p.terminal })}` : '';
        return `${p.label} · ${rate}${ctx}`;
      },
    },
    xAxis: {
      type: 'category',
      boundaryGap: false,
      data: points.map((p) => p.label),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: axisColor, fontSize: 10, interval: (i: number) => i % 3 === 0, hideOverlap: true },
    },
    yAxis: { type: 'value', show: false, min: 0, max: 100 },
    series: [{
      type: 'line',
      smooth: 0.35,
      connectNulls: false,
      showSymbol: false,
      lineStyle: { width: 2, color: HEALTH.ok },
      areaStyle: {
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [
          { offset: 0, color: 'rgba(34,197,94,0.30)' }, { offset: 1, color: 'rgba(34,197,94,0.02)' },
        ] },
      },
      markLine: {
        silent: true,
        symbol: 'none',
        data: [{ yAxis: 95 }],
        lineStyle: { type: 'dashed', color: axisColor, opacity: 0.6 },
        label: { formatter: t('dashboard:successTarget', { pct: 95 }), color: axisColor, fontSize: 10, position: 'insideEndTop' },
      },
      data: points.map((p) => p.rate),
    }],
  }), [points, axisColor, tipBg, tipText, t]);

  if (points.length === 0 || points.every((p) => p.rate == null)) {
    return <div className="h-40 flex items-center justify-center"><EmptyState text={t('dashboard:noExecutionsYet')} /></div>;
  }
  return <EChart option={option} className="h-40 w-full" ariaLabel={t('dashboard:successTrend24h')} />;
}

// ─────────────────────────────────────────────────────────────────────────────
// p95 duration of the top (busiest) workflows — ranked horizontal bars. Sorting
// the busiest-5 set by p95 is presentation order only; it is not a global
// "slowest" ranking (the backend selects by run count).
// ─────────────────────────────────────────────────────────────────────────────

function P95WorkflowBars({ items, tokens }: Readonly<{ items: TopWorkflow[]; tokens: ThemeTokens }>) {
  const { t } = useTranslation(['dashboard']);
  const axisColor = tokens.onSurfaceVariant || '#9ca3af';
  const tipBg = tokens.surfaceHigh || '#1f1b16';
  const tipText = tokens.onSurface || '#ece7e1';
  // Accent gradient stops follow the active skin (orange / lilac / blue); fall back to
  // the dark-orange literals so the bars still render under jsdom where tokens are empty.
  const barFrom = tokens.primaryContainer || '#c5620b';
  const barTo = tokens.primary || '#fc8861';

  const top = useMemo(() => items
    .filter((w) => w.p95DurationMs != null)
    .sort((a, b) => (b.p95DurationMs ?? 0) - (a.p95DurationMs ?? 0))
    .slice(0, 5), [items]);

  const option = useMemo<EChartsOption>(() => ({
    grid: { left: 4, right: 48, top: 6, bottom: 6, containLabel: true },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      backgroundColor: tipBg,
      borderWidth: 0,
      padding: [6, 10],
      textStyle: { color: tipText, fontSize: 11 },
      formatter: (params: unknown) => {
        const arr = params as Array<{ name: string; value: number }>;
        const p = arr[0];
        return p ? `${p.name}<br/>p95 ${formatDuration(p.value)}` : '';
      },
    },
    xAxis: { type: 'value', show: false },
    yAxis: {
      type: 'category',
      inverse: true,
      data: top.map((w) => w.name),
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: {
        color: axisColor,
        fontSize: 11,
        formatter: (v: string) => (v.length > 16 ? `${v.slice(0, 15)}…` : v),
      },
    },
    series: [{
      type: 'bar',
      barWidth: '55%',
      itemStyle: {
        borderRadius: [0, 4, 4, 0],
        color: { type: 'linear', x: 0, y: 0, x2: 1, y2: 0, colorStops: [
          { offset: 0, color: barFrom }, { offset: 1, color: barTo },
        ] },
      },
      label: {
        show: true,
        position: 'right',
        color: axisColor,
        fontSize: 10,
        formatter: (p: unknown) => formatDuration((p as { value: number }).value),
      },
      data: top.map((w) => w.p95DurationMs),
    }],
  }), [top, axisColor, tipBg, tipText, barFrom, barTo]);

  if (top.length === 0) {
    return <div className="h-40 flex items-center justify-center"><EmptyState text={t('dashboard:noDurationData')} /></div>;
  }
  return <EChart option={option} className="h-40 w-full" ariaLabel={t('dashboard:p95TopWorkflows')} />;
}

function LiveDuration({ startedAt }: Readonly<{ startedAt: string }>) {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = globalThis.setInterval(() => setNow(Date.now()), 1000);
    return () => globalThis.clearInterval(timer);
  }, []);

  const ms = now - new Date(startedAt).getTime();
  const isLong = ms > LONG_RUNNING_THRESHOLD_MS;
  return (
    <span className={`text-xs font-medium tabular-nums shrink-0 ml-2 flex items-center gap-1 ${isLong ? 'text-red-600' : 'text-blue-600'}`}>
      {isLong && <WarningAltFilled size={12} />}
      {formatDuration(ms)}
    </span>
  );
}

function Panel({
  title, icon: Icon, iconClass, divided = false, className, children,
}: Readonly<{ title: string; icon: React.ElementType; iconClass?: string; divided?: boolean; className?: string; children: React.ReactNode }>) {
  return (
    <div className={`np-card p-5 ${className ?? ''}`}>
      <h3 className={`font-semibold text-on-surface flex items-center gap-2 ${divided ? 'pb-3 mb-0 border-b border-outline-variant/25' : 'mb-3'}`}>
        <Icon size={16} className={iconClass ?? 'text-outline'} />
        {title}
      </h3>
      {divided ? <div className="mt-3">{children}</div> : children}
    </div>
  );
}

function EmptyState({ text }: Readonly<{ text: string }>) {
  return <p className="text-outline text-sm py-4 text-center">{text}</p>;
}

function TelemetrySection() {
  const { t } = useTranslation(['dashboard']);
  const { data: config } = useObservabilityConfig();
  const available = config?.enabled && config?.prometheusAvailable;
  const { data: summary, isError, error } = useTelemetrySummary(!!available);

  if (!config?.enabled) return null;
  if (!config.prometheusAvailable) return null;
  if (!summary?.available) return null;

  return (
    <div className="np-card p-5 mb-5">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-semibold text-on-surface flex items-center gap-2">
          <Meter size={16} className="text-indigo-500" />
          {t('dashboard:telemetry')}
        </h3>
        <span className="text-xs text-outline">{t('dashboard:telemetryRefresh')}</span>
      </div>
      {isError && (
        <div className="flex items-center gap-2 text-xs text-red-600 mb-3">
          <WarningAltFilled size={14} />
          {(error as Error)?.message ?? t('dashboard:telemetryFailed')}
        </div>
      )}
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-3">
        {summary.panels.map((p) => (
          <TelemetryCard key={p.key} title={p.title} unit={p.unit} value={p.value} error={p.error} />
        ))}
      </div>
    </div>
  );
}

function TelemetryCard({ title, unit, value, error }: Readonly<{
  title: string;
  unit: string;
  value: number | null;
  error: string | null;
}>) {
  const display = formatTelemetryValue(value, unit);
  return (
    <div className="border border-surface-variant/30 rounded-xl p-3">
      <p className="text-[11px] uppercase tracking-wide text-outline font-medium">{title}</p>
      {error ? (
        <p className="text-xs text-red-500 mt-1 truncate" title={error}>{error}</p>
      ) : (
        <p className="text-xl font-semibold text-on-surface mt-1 tabular-nums">{display}</p>
      )}
    </div>
  );
}

function formatTelemetryValue(value: number | null, unit: string): string {
  if (value == null || !isFinite(value)) return '–';
  switch (unit) {
    case 'ratio':
      return `${(value * 100).toFixed(1)} %`;
    case 'ms':
      return value < 1000 ? `${Math.round(value)} ms` : `${(value / 1000).toFixed(2)} s`;
    case 'per_min':
    case 'rps':
    case 'count':
      return value < 10 ? value.toFixed(2) : formatNumber(Math.round(value));
    default:
      return formatNumber(value);
  }
}
