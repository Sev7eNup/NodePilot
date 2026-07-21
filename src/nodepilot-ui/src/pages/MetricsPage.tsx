import { CircleDash, Launch, Meter, Pause, Play, Renew, WarningAltFilled } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { Link, NavLink, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { EChart } from '../components/common/EChart';
import { buildGrafanaDashboardUrl, useMetricsDashboard, useObservabilityConfig } from '../api/observability';
import type { MetricsDataSeries, MetricsWidget } from '../types/api';
import { metricsSections } from '../lib/navigation';
const HOURS = [1, 24, 168, 720] as const;
const HOUR_LABEL: Record<number, string> = { 1: '1 h', 24: '24 h', 168: '7 d', 720: '30 d' };
const COLORS = ['#7c3aed', '#06b6d4', '#22c55e', '#f59e0b', '#ef4444', '#3b82f6', '#ec4899', '#84cc16'];

export function MetricsPage() {
  const { t } = useTranslation('metrics');
  const { section = 'mission-control' } = useParams();
  const current = metricsSections.find((entry) => entry.id === section) ?? metricsSections[0];
  const [hours, setHours] = useState<number>(24);
  const [paused, setPaused] = useState(false);
  const { data: config } = useObservabilityConfig();
  const enabled = !!config?.enabled && !!config.prometheusAvailable;
  const query = useMetricsDashboard(current.id, hours, paused, enabled);
  const grafanaUrl = buildGrafanaDashboardUrl(config?.grafanaBaseUrl, current.dashboardId);

  return (
    <section className="space-y-4" aria-label={t('title')}>
      <header className="np-card p-4 sm:p-5">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-center xl:justify-between">
          <div>
            <div className="flex items-center gap-2"><Meter size={20} className="text-primary" /><h2 className="text-xl font-bold text-on-surface">{t('title')}</h2></div>
            <p className="mt-1 text-sm text-on-surface-variant">{t('subtitle')}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex rounded-lg border border-outline-variant/40 p-0.5">{HOURS.map((value) => <button key={value} onClick={() => setHours(value)} className={`rounded-md px-2.5 py-1 text-xs ${hours === value ? 'bg-primary text-on-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}>{HOUR_LABEL[value]}</button>)}</div>
            <button onClick={() => setPaused((value) => !value)} className="inline-flex items-center gap-1.5 rounded-lg border border-outline-variant/40 px-3 py-1.5 text-xs text-on-surface-variant hover:bg-surface-high" title={paused ? t('resume') : t('pause')}>{paused ? <Play size={14} /> : <Pause size={14} />}{paused ? t('paused') : t('live')}</button>
            <button onClick={() => void query.refetch()} className="inline-flex items-center gap-1.5 rounded-lg border border-outline-variant/40 px-3 py-1.5 text-xs text-on-surface-variant hover:bg-surface-high"><Renew size={14} />{t('refresh')}</button>
            {grafanaUrl && <a href={grafanaUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1.5 rounded-lg bg-surface-high px-3 py-1.5 text-xs text-on-surface hover:bg-surface-highest"><Launch size={14} />{t('openGrafana')}</a>}
          </div>
        </div>
        <nav className="mt-5 flex gap-1 overflow-x-auto border-b border-outline-variant/30 pb-1" aria-label={t('sections')}>{metricsSections.map(({ id, labelKey }) => <NavLink key={id} to={`/metrics/${id}`} className={({ isActive }) => `whitespace-nowrap rounded-md px-3 py-1.5 text-sm ${isActive ? 'bg-primary/15 font-medium text-primary' : 'text-on-surface-variant hover:bg-surface-high'}`}>{t(`sectionLabels.${labelKey}`)}</NavLink>)}</nav>
      </header>
      {!enabled ? <SetupState /> : query.isLoading ? <LoadingState /> : query.isError ? <ErrorState text={t('loadFailed')} /> : !query.data?.available ? <SetupState /> : <DashboardContent widgets={query.data.widgets ?? []} />}
    </section>
  );
}

function DashboardContent({ widgets }: { widgets: MetricsWidget[] }) {
  return <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-12">{widgets.map((widget) => <Widget key={widget.id} widget={widget} />)}</div>;
}

function Widget({ widget }: { widget: MetricsWidget }) {
  const span = widget.grid.width <= 3 ? 'xl:col-span-3' : widget.grid.width <= 4 ? 'xl:col-span-4' : widget.grid.width <= 6 ? 'xl:col-span-6' : widget.grid.width <= 8 ? 'xl:col-span-8' : widget.grid.width <= 12 ? 'xl:col-span-12' : 'xl:col-span-12';
  const compact = widget.type === 'stat';
  return (
    <article className={`np-card col-span-1 p-4 sm:col-span-2 ${span}`} title={widget.description ?? undefined}>
      <div className="mb-3 flex items-start justify-between gap-2"><div><h3 className="font-semibold text-on-surface">{widget.title}</h3>{widget.description && !compact && <p className="mt-0.5 line-clamp-2 text-xs text-on-surface-variant">{widget.description}</p>}</div>{widget.error && <WarningAltFilled size={15} className="shrink-0 text-amber-500" aria-label={widget.error} />}</div>
      {widget.type === 'stat' ? <StatWidget widget={widget} /> : widget.type === 'table' ? <TableWidget widget={widget} /> : <ChartWidget widget={widget} />}
    </article>
  );
}

function StatWidget({ widget }: { widget: MetricsWidget }) {
  if (widget.data.length === 0) return <Empty />;
  return <div className="flex flex-wrap gap-x-6 gap-y-2">{widget.data.map((series, index) => <div key={`${series.label}-${index}`}><p className="text-2xl font-semibold tabular-nums text-on-surface">{formatValue(last(series), widget.unit)}</p>{widget.data.length > 1 && <p className="mt-1 text-xs text-on-surface-variant">{series.label}</p>}</div>)}</div>;
}

function ChartWidget({ widget }: { widget: MetricsWidget }) {
  const option = useMemo<EChartsOption>(() => buildMetricsChartOption(widget), [widget]);
  return widget.data.length ? <EChart option={option} style={{ height: Math.max(220, widget.grid.height * 28) }} ariaLabel={widget.title} /> : <Empty />;
}

export function buildMetricsChartOption(widget: MetricsWidget): EChartsOption {
  const base = { tooltip: { trigger: 'axis' as const }, legend: { type: 'scroll' as const, bottom: 0, textStyle: { color: '#94a3b8' } }, grid: { left: 12, right: 16, top: 16, bottom: 42, containLabel: true } };
  if (widget.type === 'piechart') {
    const finite = widget.data.flatMap((series, index) => {
      const value = last(series);
      return value == null ? [] : [{ name: series.label, value, itemStyle: { color: COLORS[index % COLORS.length] } }];
    });
    return { tooltip: { trigger: 'item' }, legend: base.legend, series: [{ type: 'pie', radius: ['38%', '70%'], data: finite }] };
  }
  if (widget.type === 'bargauge') {
    const finite = widget.data.flatMap((series, index) => {
      const value = last(series);
      return value == null ? [] : [{ label: series.label, value, color: COLORS[index % COLORS.length] }];
    });
    return { ...base, xAxis: { type: 'value', axisLabel: { color: '#94a3b8' } }, yAxis: { type: 'category', data: finite.map((item) => item.label), axisLabel: { color: '#94a3b8', width: 180, overflow: 'truncate' } }, series: [{ type: 'bar', data: finite.map((item) => ({ value: item.value, itemStyle: { color: item.color } })) }] };
  }
  if (widget.type === 'heatmap') {
    const timestamps = [...new Set(widget.data.flatMap((series) => series.points.map((point) => point.timestamp)))].sort();
    const buckets = widget.data.map((series) => series.label);
    const values = widget.data.flatMap((series, y) => series.points.flatMap((point) => point.value == null ? [] : [[timestamps.indexOf(point.timestamp), y, point.value]]));
    const max = Math.max(1, ...values.map((value) => Number(value[2])));
    return { tooltip: {}, grid: base.grid, xAxis: { type: 'category', data: timestamps.map((timestamp) => new Date(timestamp * 1000).toLocaleTimeString()), axisLabel: { color: '#94a3b8' } }, yAxis: { type: 'category', data: buckets, axisLabel: { color: '#94a3b8' } }, visualMap: { min: 0, max, calculable: true, orient: 'horizontal', left: 'center', bottom: 0 }, series: [{ type: 'heatmap', data: values }] };
  }
  return { ...base, xAxis: { type: 'time', axisLabel: { color: '#94a3b8' } }, yAxis: { type: 'value', axisLabel: { color: '#94a3b8' }, splitLine: { lineStyle: { color: 'rgba(148,163,184,.16)' } } }, series: widget.data.map((series, index) => ({ name: series.label, type: 'line', smooth: true, showSymbol: false, lineStyle: { width: 2 }, areaStyle: { opacity: widget.data.length === 1 ? .12 : 0 }, itemStyle: { color: COLORS[index % COLORS.length] }, data: series.points.filter((point) => point.value != null).map((point) => [point.timestamp * 1000, point.value]) })) };
}

function TableWidget({ widget }: { widget: MetricsWidget }) {
  if (!widget.data.length) return <Empty />;
  const entityKey = findEntityKey(widget.data);
  if (!entityKey) return <SimpleTable widget={widget} />;
  const columns = [...new Set(widget.data.map((series) => series.label))];
  const rows = [...new Set(widget.data.map((series) => series.labels[entityKey]).filter(Boolean))];
  return <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr className="border-b border-outline-variant/30 text-left text-xs text-on-surface-variant"><th className="px-2 py-2 font-medium">{humanize(entityKey)}</th>{columns.map((column) => <th key={column} className="px-2 py-2 text-right font-medium">{column}</th>)}</tr></thead><tbody>{rows.map((row) => <tr key={row} className="border-b border-outline-variant/15"><td className="max-w-[260px] truncate px-2 py-2 text-on-surface">{row}</td>{columns.map((column) => { const value = widget.data.find((series) => series.label === column && series.labels[entityKey] === row); return <td key={column} className="px-2 py-2 text-right tabular-nums text-on-surface-variant">{value ? formatValue(last(value), unitForColumn(column, widget.unit)) : '—'}</td>; })}</tr>)}</tbody></table></div>;
}

function SimpleTable({ widget }: { widget: MetricsWidget }) { return <div className="space-y-1">{widget.data.map((series, index) => <div key={`${series.label}-${index}`} className="flex justify-between gap-3 border-b border-outline-variant/15 py-1.5 text-sm"><span className="truncate text-on-surface-variant">{series.label}</span><span className="tabular-nums text-on-surface">{formatValue(last(series), widget.unit)}</span></div>)}</div>; }
function findEntityKey(data: MetricsDataSeries[]) { const priority = ['workflow_name', 'http_route', 'activity_type', 'auth_type', 'operation', 'trigger_type', 'model']; return priority.find((key) => data.some((series) => series.labels[key])); }
function unitForColumn(column: string, fallback: string) { if (/success rate/i.test(column)) return 'percentunit'; if (/p\d+/i.test(column)) return 'ms'; return fallback; }
function humanize(value: string) { return value.replaceAll('_', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase()); }
function last(series: MetricsDataSeries) { return series.points.at(-1)?.value ?? null; }
function Empty() { return <p className="py-10 text-center text-sm text-on-surface-variant">—</p>; }
function SetupState() { const { t } = useTranslation('metrics'); return <div className="np-card flex items-start gap-3 p-6"><WarningAltFilled className="mt-0.5 text-amber-500" size={20} /><div><h3 className="font-semibold text-on-surface">{t('notConfigured')}</h3><p className="mt-1 text-sm text-on-surface-variant">{t('notConfiguredDetail')}</p><Link to="/settings" className="mt-3 inline-block text-sm text-primary hover:underline">{t('openSettings')}</Link></div></div>; }
function LoadingState() { return <div className="np-card flex items-center justify-center gap-2 p-16 text-on-surface-variant"><CircleDash className="animate-spin" size={20} />Laden…</div>; }
function ErrorState({ text }: { text: string }) { return <div className="np-card p-6 text-red-500">{text}</div>; }

function formatValue(value: number | null, unit: string) {
  if (value == null || !Number.isFinite(value)) return '—';
  if (unit === 'percentunit' || unit === 'ratio') return `${(value * 100).toFixed(1)} %`;
  if (unit === 'percent') return `${value.toFixed(1)} %`;
  if (unit === 'ms') return value >= 1000 ? `${(value / 1000).toFixed(2)} s` : `${Math.round(value)} ms`;
  if (unit === 's' || unit === 'seconds') return `${value.toFixed(2)} s`;
  if (unit === 'bytes' || unit === 'decbytes') return `${(value / 1024 / 1024).toFixed(1)} MB`;
  if (unit === 'Bps') return `${(value / 1024 / 1024).toFixed(1)} MB/s`;
  if (unit === 'reqps' || unit === 'rps' || unit === 'ops') return `${value.toFixed(2)}/s`;
  return value < 10 ? value.toFixed(2) : new Intl.NumberFormat().format(Math.round(value));
}
