import { CircleDash, Launch, Meter, Pause, Play, Renew, WarningAltFilled } from '@carbon/icons-react';
import { memo, useMemo, useState, type CSSProperties } from 'react';
import { Link, NavLink, useParams } from 'react-router';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { EChart } from '../components/common/LazyEChart';
import { buildGrafanaDashboardUrl, useMetricsDashboard, useObservabilityConfig } from '../api/observability';
import type { MetricsDataSeries, MetricsWidget } from '../types/api';
import { metricsSections } from '../lib/navigation';
import { DEFAULT_CHART_TOKENS, useChartTokens, type ChartTokens } from '../lib/chartTheme';
import { formatTime } from '../lib/format';
const HOURS = [1, 24, 168, 720] as const;
const HOUR_LABEL: Record<number, string> = { 1: '1 h', 24: '24 h', 168: '7 d', 720: '30 d' };

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

type Block = { kind: 'stats'; widgets: MetricsWidget[] } | { kind: 'widget'; widget: MetricsWidget };

/** Consecutive stat panels become one KPI strip; every other panel stays its own block. */
function groupBlocks(widgets: MetricsWidget[]): Block[] {
  const blocks: Block[] = [];
  for (const widget of widgets) {
    const tail = blocks.at(-1);
    if (widget.type !== 'stat') blocks.push({ kind: 'widget', widget });
    else if (tail?.kind === 'stats') tail.widgets.push(widget);
    else blocks.push({ kind: 'stats', widgets: [widget] });
  }
  return blocks;
}

// Grafana lays panels out on a 24-column grid; the page uses 12, so each panel takes half its width.
const XL_SPAN: Record<number, string> = { 1: 'xl:col-span-1', 2: 'xl:col-span-2', 3: 'xl:col-span-3', 4: 'xl:col-span-4', 5: 'xl:col-span-5', 6: 'xl:col-span-6', 7: 'xl:col-span-7', 8: 'xl:col-span-8', 9: 'xl:col-span-9', 10: 'xl:col-span-10', 11: 'xl:col-span-11', 12: 'xl:col-span-12' };

function DashboardContent({ widgets }: { widgets: MetricsWidget[] }) {
  // One probe and one observer for the whole grid rather than one per widget.
  const { probeRef, tokens } = useChartTokens();
  return (
    <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-12">
      <div ref={probeRef} aria-hidden="true" className="hidden" />
      {groupBlocks(widgets).map((block) => block.kind === 'stats'
        ? <StatStrip key={`stats-${block.widgets[0].id}`} widgets={block.widgets} />
        : <Widget key={block.widget.id} widget={block.widget} tokens={tokens} />)}
    </div>
  );
}

// A chart with no finite value would draw empty axes, so it counts as having no data.
function hasData(widget: MetricsWidget): boolean {
  return widget.type === 'table' ? widget.data.length > 0 : widget.data.some((series) => series.points.some((point) => point.value != null));
}

function Widget({ widget, tokens }: { widget: MetricsWidget; tokens: ChartTokens }) {
  const { t } = useTranslation('metrics');
  const span = XL_SPAN[Math.min(12, Math.max(1, Math.ceil(widget.grid.width / 2)))];
  const half = widget.grid.width < 16 ? 'md:col-span-1' : 'md:col-span-2';
  const truncated = widget.type === 'timeseries' && widget.data.length > MAX_SERIES;
  return (
    <article className={`np-card flex min-w-0 flex-col p-4 ${half} ${span}`}>
      <div className="mb-2 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-semibold text-on-surface">{widget.title}</h3>
          {widget.description && <p className="mt-0.5 truncate text-xs text-on-surface-variant" title={widget.description}>{widget.description}</p>}
        </div>
        {truncated && <span className="shrink-0 rounded bg-surface-high px-1.5 py-0.5 text-[11px] text-on-surface-variant">{t('topSeries', { shown: MAX_SERIES, total: widget.data.length })}</span>}
        {widget.error && <WarningAltFilled size={15} className="shrink-0 text-amber-500" aria-label={widget.error} />}
      </div>
      {!hasData(widget) ? <Empty /> : widget.type === 'table' ? <TableWidget widget={widget} /> : <ChartWidget widget={widget} tokens={tokens} />}
    </article>
  );
}

function StatStrip({ widgets }: { widgets: MetricsWidget[] }) {
  // At most six tiles per row, split evenly so a strip of eight reads as 4 + 4.
  const columns = Math.ceil(widgets.length / Math.ceil(widgets.length / 6));
  return (
    <div className="np-card p-3 md:col-span-2 xl:col-span-12">
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 xl:grid-cols-[repeat(var(--stat-columns),minmax(0,1fr))]" style={{ '--stat-columns': columns } as CSSProperties}>
        {widgets.map((widget) => <StatTile key={widget.id} widget={widget} />)}
      </div>
    </div>
  );
}

function StatTile({ widget }: { widget: MetricsWidget }) {
  return (
    <div className="min-w-0 rounded-lg bg-surface-high/40 px-4 py-3" title={widget.description ?? undefined}>
      <p className="flex items-center gap-1.5 text-xs font-medium text-on-surface-variant">
        <span className="truncate">{widget.title}</span>
        {widget.error && <WarningAltFilled size={13} className="shrink-0 text-amber-500" aria-label={widget.error} />}
      </p>
      {widget.data.length === 0
        ? <p className="mt-1 text-2xl font-semibold text-on-surface-variant">—</p>
        : <div className="mt-1 flex flex-wrap gap-x-5 gap-y-1">{widget.data.map((series, index) => <div key={`${series.label}-${index}`}><p className="text-2xl font-semibold tabular-nums text-on-surface">{formatValue(last(series), widget.unit)}</p>{widget.data.length > 1 && <p className="text-xs text-on-surface-variant">{series.label}</p>}</div>)}</div>}
    </div>
  );
}

function chartHeight(widget: MetricsWidget): number {
  if (widget.type === 'bargauge') return Math.max(160, widget.data.length * 22 + 16);
  return widget.type === 'heatmap' ? 220 : 200;
}

function ChartWidget({ widget, tokens }: { widget: MetricsWidget; tokens: ChartTokens }) {
  const option = useMemo<EChartsOption>(() => buildMetricsChartOption(widget, tokens), [widget, tokens]);
  // A heatmap draws one cell per bucket and timestamp. The shipped dashboards sample at 1m, so a
  // 24 h window is tens of thousands of cells — as SVG that is tens of thousands of DOM elements.
  // Every other chart type stays on SVG, which keeps it crisp at any zoom.
  const renderer = widget.type === 'heatmap' ? 'canvas' : 'svg';
  const chart = <EChart option={option} renderer={renderer} style={{ height: chartHeight(widget) }} ariaLabel={widget.title} />;
  // A long ranking scrolls inside its card instead of stretching the whole row.
  return widget.type === 'bargauge' ? <div className="max-h-64 overflow-y-auto">{chart}</div> : chart;
}

const MAX_SERIES = 8;

/** The palette has eight slots, so a line chart keeps the eight series with the highest peak. */
function rankedSeries(data: MetricsDataSeries[]): MetricsDataSeries[] {
  if (data.length <= MAX_SERIES) return data;
  const peak = (series: MetricsDataSeries) => series.points.reduce((max, point) => Math.max(max, point.value ?? 0), 0);
  return [...data].sort((a, b) => peak(b) - peak(a)).slice(0, MAX_SERIES);
}

// Heatmap rows are histogram buckets (`le`); order them by bound, `+Inf` last, when every label is one.
function orderedBuckets(data: MetricsDataSeries[]): MetricsDataSeries[] {
  const bound = (label: string) => /^\+?inf$/i.test(label) ? Infinity : Number(label);
  if (data.some((series) => Number.isNaN(bound(series.label)))) return data;
  return [...data].sort((a, b) => (bound(a.label) < bound(b.label) ? -1 : bound(a.label) > bound(b.label) ? 1 : 0));
}

/**
 * Builds the ECharts option for one metrics widget.
 *
 * `tokens` carries the live design tokens plus the categorical series palette. It has a
 * default so the builder stays a pure function that can be called without a probe.
 * Every chart keeps its text legend, which identifies the series for readers who cannot
 * tell the hues apart and gives the lower-contrast light-mode colors a second cue.
 */
export function buildMetricsChartOption(widget: MetricsWidget, tokens: ChartTokens = DEFAULT_CHART_TOKENS): EChartsOption {
  const { series: palette, axis, grid: gridLine, surfaceHigh, onSurface } = tokens;
  const seriesColor = (index: number) => palette[index % palette.length];
  const axisLabel = { color: axis, fontSize: 11 };
  const formatUnit = (value: unknown) => formatValue(Number(value), widget.unit);
  const tooltipStyle = {
    backgroundColor: surfaceHigh,
    borderColor: gridLine,
    textStyle: { color: onSurface, fontSize: 12 },
    ...(tokens.reducedDecoration ? { extraCssText: 'box-shadow:none' } : {}),
  };
  // A single series is named by the card title, so its legend would only cost height.
  const lines = rankedSeries(widget.data);
  const showLegend = lines.length > 1;
  const base = {
    tooltip: { trigger: 'axis' as const, valueFormatter: formatUnit, ...tooltipStyle },
    legend: { show: showLegend, type: 'scroll' as const, bottom: 0, icon: 'circle', itemWidth: 8, itemHeight: 8, itemGap: 14, textStyle: { color: axis, fontSize: 11 } },
    grid: { left: 4, right: 8, top: 8, bottom: showLegend ? 30 : 4, containLabel: true },
  };
  if (widget.type === 'piechart') {
    const ranked = widget.data.flatMap((series) => {
      const value = last(series);
      return value == null ? [] : [{ name: series.label, value }];
    }).sort((a, b) => b.value - a.value);
    // Past the palette size the smallest slices collapse into one neutral "+N" slice.
    const head = ranked.length > MAX_SERIES ? ranked.slice(0, MAX_SERIES - 1) : ranked;
    const rest = ranked.slice(head.length);
    const finite = [
      ...head.map((item, index) => ({ ...item, itemStyle: { color: seriesColor(index) } })),
      ...(rest.length ? [{ name: `+${rest.length}`, value: rest.reduce((sum, item) => sum + item.value, 0), itemStyle: { color: axis } }] : []),
    ];
    return {
      tooltip: { trigger: 'item', valueFormatter: formatUnit, ...tooltipStyle },
      legend: { ...base.legend, show: true },
      series: [{ type: 'pie', radius: ['48%', '72%'], center: ['50%', '45%'], label: { show: false }, itemStyle: { borderColor: surfaceHigh, borderWidth: 2, borderRadius: 4 }, data: finite }],
    };
  }
  if (widget.type === 'bargauge') {
    const finite = widget.data.flatMap((series) => {
      const value = last(series);
      return value == null ? [] : [{ label: series.label, value }];
    }).sort((a, b) => b.value - a.value);
    // One measure compared across entities: a single accent hue, with the value printed at the bar end.
    return {
      tooltip: { trigger: 'item', valueFormatter: formatUnit, ...tooltipStyle },
      grid: { left: 4, right: 56, top: 4, bottom: 4, containLabel: true },
      xAxis: { type: 'value', show: false },
      yAxis: { type: 'category', inverse: true, data: finite.map((item) => item.label), axisTick: { show: false }, axisLine: { show: false }, axisLabel: { ...axisLabel, width: 130, overflow: 'truncate' } },
      series: [{ type: 'bar', barMaxWidth: 12, itemStyle: { color: tokens.primary, borderRadius: [0, 4, 4, 0] }, label: { show: true, position: 'right', color: axis, fontSize: 11, formatter: (params: { value?: unknown }) => formatUnit(params.value) }, data: finite.map((item) => item.value) }],
    };
  }
  if (widget.type === 'heatmap') {
    const timestamps = [...new Set(widget.data.flatMap((series) => series.points.map((point) => point.timestamp)))].sort((a, b) => a - b);
    const rows = orderedBuckets(widget.data);
    const buckets = rows.map((series) => series.label);
    // Indexed once; indexOf per point scanned the whole axis for every cell.
    const xByTimestamp = new Map(timestamps.map((timestamp, index) => [timestamp, index]));
    const values = rows.flatMap((series, y) => series.points.flatMap((point) => point.value == null ? [] : [[xByTimestamp.get(point.timestamp)!, y, point.value]]));
    const max = Math.max(1, ...values.map((value) => Number(value[2])));
    // A heatmap encodes magnitude, so it uses a single-hue sequential ramp keyed to the
    // skin accent rather than the categorical palette.
    return { tooltip: tooltipStyle, grid: { ...base.grid, bottom: 40 }, xAxis: { type: 'category', data: timestamps.map((timestamp) => formatTime(timestamp * 1000, { hour: '2-digit', minute: '2-digit' })), axisLabel: { ...axisLabel, hideOverlap: true } }, yAxis: { type: 'category', data: buckets, axisLabel }, visualMap: { min: 0, max, calculable: false, orient: 'horizontal', left: 'center', bottom: 0, itemWidth: 10, itemHeight: 120, textStyle: { color: axis, fontSize: 11 }, inRange: { color: [surfaceHigh, tokens.primaryContainer, tokens.primary] } }, series: [{ type: 'heatmap', data: values }] };
  }
  return {
    ...base,
    xAxis: { type: 'time', axisLabel, axisTick: { show: false }, axisLine: { lineStyle: { color: gridLine } } },
    yAxis: { type: 'value', axisLabel: { ...axisLabel, formatter: formatUnit }, splitNumber: 3, splitLine: { lineStyle: { color: gridLine, type: 'dashed' } } },
    series: lines.map((series, index) => ({ name: series.label, type: 'line', smooth: true, showSymbol: false, lineStyle: { width: 1.5 }, areaStyle: { opacity: lines.length === 1 ? .12 : 0 }, itemStyle: { color: seriesColor(index) }, data: series.points.filter((point) => point.value != null).map((point) => [point.timestamp * 1000, point.value]) })),
  };
}

const TableWidget = memo(function TableWidget({ widget }: { widget: MetricsWidget }) {
  // Indexed by (column, row) once. Looking each cell up with a scan over the whole series list
  // made rendering quadratic in the size of the table.
  const model = useMemo(() => {
    const entityKey = findEntityKey(widget.data);
    if (!entityKey) return null;
    const columns = [...new Set(widget.data.map((series) => series.label))];
    const rows = [...new Set(widget.data.map((series) => series.labels[entityKey]).filter(Boolean))];
    const cells = new Map<string, MetricsDataSeries>();
    for (const series of widget.data) {
      const key = `${series.label} ${series.labels[entityKey]}`;
      if (!cells.has(key)) cells.set(key, series);
    }
    return { entityKey, columns, rows, cells };
  }, [widget.data]);

  if (!widget.data.length) return <Empty />;
  if (!model) return <SimpleTable widget={widget} />;
  const { entityKey, columns, rows, cells } = model;
  return <div className="max-h-96 overflow-auto"><table className="w-full text-sm"><thead className="sticky top-0 bg-surface-high"><tr className="text-left text-xs text-on-surface-variant"><th className="px-2 py-2 font-medium">{humanize(entityKey)}</th>{columns.map((column) => <th key={column} className="px-2 py-2 text-right font-medium">{column}</th>)}</tr></thead><tbody>{rows.map((row) => <tr key={row} className="border-b border-outline-variant/15"><td className="max-w-[260px] truncate px-2 py-2 text-on-surface">{row}</td>{columns.map((column) => { const value = cells.get(`${column} ${row}`); return <td key={column} className="px-2 py-2 text-right tabular-nums text-on-surface-variant">{value ? formatValue(last(value), unitForColumn(column, widget.unit)) : '—'}</td>; })}</tr>)}</tbody></table></div>;
});

function SimpleTable({ widget }: { widget: MetricsWidget }) { return <div className="space-y-1">{widget.data.map((series, index) => <div key={`${series.label}-${index}`} className="flex justify-between gap-3 border-b border-outline-variant/15 py-1.5 text-sm"><span className="truncate text-on-surface-variant">{series.label}</span><span className="tabular-nums text-on-surface">{formatValue(last(series), widget.unit)}</span></div>)}</div>; }
function findEntityKey(data: MetricsDataSeries[]) { const priority = ['workflow_name', 'http_route', 'activity_type', 'auth_type', 'operation', 'trigger_type', 'model']; return priority.find((key) => data.some((series) => series.labels[key])); }
function unitForColumn(column: string, fallback: string) { if (/success\s*rate/i.test(column)) return 'percentunit'; if (/p\d+/i.test(column)) return 'ms'; return fallback; }
function humanize(value: string) { return value.replaceAll('_', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase()); }
function last(series: MetricsDataSeries) { return series.points.at(-1)?.value ?? null; }
function Empty() { const { t } = useTranslation('metrics'); return <p className="flex flex-1 items-center justify-center py-6 text-sm text-on-surface-variant">{t('noData')}</p>; }
function SetupState() { const { t } = useTranslation('metrics'); return <div className="np-card flex items-start gap-3 p-6"><WarningAltFilled className="mt-0.5 text-amber-500" size={20} /><div><h3 className="font-semibold text-on-surface">{t('notConfigured')}</h3><p className="mt-1 text-sm text-on-surface-variant">{t('notConfiguredDetail')}</p><Link to="/settings" className="mt-3 inline-block text-sm text-primary hover:underline">{t('openSettings')}</Link></div></div>; }
function LoadingState() { const { t } = useTranslation('common'); return <div className="np-card flex items-center justify-center gap-2 p-16 text-on-surface-variant"><CircleDash className="animate-spin" size={20} />{t('loading')}</div>; }
function ErrorState({ text }: { text: string }) { return <div className="np-card p-6 text-red-500">{text}</div>; }

function formatValue(value: number | null, unit: string) {
  if (value == null || !Number.isFinite(value)) return '—';
  if (unit === 'percentunit' || unit === 'ratio') return `${(value * 100).toFixed(1)} %`;
  if (unit === 'percent') return `${value.toFixed(1)} %`;
  if (unit === 'ms') return value >= 1000 ? `${(value / 1000).toFixed(2)} s` : `${Math.round(value)} ms`;
  if (unit === 's' || unit === 'seconds') return `${value.toFixed(2)} s`;
  if (unit === 'bytes' || unit === 'decbytes') return value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(1)} GB` : `${(value / 1024 / 1024).toFixed(1)} MB`;
  if (unit === 'Bps') return `${(value / 1024 / 1024).toFixed(1)} MB/s`;
  if (unit === 'reqps' || unit === 'rps' || unit === 'ops') return `${value.toFixed(2)}/s`;
  return value < 10 ? value.toFixed(2) : new Intl.NumberFormat().format(Math.round(value));
}
