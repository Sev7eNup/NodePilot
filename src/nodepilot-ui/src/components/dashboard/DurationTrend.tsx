import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import type { EChartsOption } from 'echarts';
import { api } from '../../api/client';
import { EChart } from '../common/LazyEChart';
import type { ChartTokens } from '../../lib/chartTheme';
import { formatDate, formatDuration, formatNumber } from '../../lib/format';

interface DurationBucket {
  startedAt: string;
  count: number;
  medianMs: number | null;
  p95Ms: number | null;
}
interface DurationTrendResponse {
  buckets: DurationBucket[];
  workflows: { id: string; name: string }[];
}

export const durationTrendQuery = (windowHours: number, workflowId = '') => ({
  queryKey: ['dashboard-duration-trend', windowHours, workflowId],
  queryFn: () => api.get<DurationTrendResponse>(`/stats/duration-trend?windowHours=${windowHours}${workflowId ? `&workflowId=${encodeURIComponent(workflowId)}` : ''}`),
  refetchInterval: 120_000,
  staleTime: 120_000,
  retry: false,
});

export function DurationTrend({ windowHours, windowLabel, tokens, workflowId, onWorkflowChange }: Readonly<{
  windowHours: number; windowLabel: string; tokens: ChartTokens;
  workflowId: string; onWorkflowChange: (id: string) => void;
}>) {
  const { t } = useTranslation(['dashboard', 'common']);
  const query = useQuery(durationTrendQuery(windowHours, workflowId));
  // Keep the available choices while a newly selected series loads.
  const optionsQuery = useQuery(durationTrendQuery(windowHours));
  const workflows = query.data?.workflows ?? optionsQuery.data?.workflows ?? [];
  const option = useMemo<EChartsOption>(() => {
    const buckets = query.data?.buckets ?? [];
    const labels = buckets.map(b => formatDate(b.startedAt, {
      ...(windowHours > 24 ? { month: '2-digit', day: '2-digit' } as const : {}),
      hour: '2-digit', minute: '2-digit',
    }));
    return {
      grid: { left: 0, right: 10, top: 16, bottom: 0, containLabel: true },
      tooltip: {
        trigger: 'axis', backgroundColor: tokens.surfaceHigh, borderColor: tokens.grid,
        textStyle: { color: tokens.onSurface, fontSize: 11 },
        formatter: (params: unknown) => {
          const point = (params as { dataIndex: number }[])[0];
          const b = buckets[point?.dataIndex];
          if (!b) return '';
          return `${labels[point.dataIndex]}<br/>${t('dashboard:durationTrend.median')}: ${formatDuration(b.medianMs)}<br/>P95: ${formatDuration(b.p95Ms)}<br/>${t('dashboard:durationTrend.samples', { count: b.count, formattedCount: formatNumber(b.count) })}`;
        },
      },
      xAxis: {
        type: 'category', boundaryGap: false, data: labels,
        axisLine: { show: false }, axisTick: { show: false },
        axisLabel: { color: tokens.axis, fontSize: 10, hideOverlap: true },
      },
      yAxis: {
        type: 'value', min: 0,
        axisLabel: { color: tokens.axis, fontSize: 10, formatter: (ms: number) => formatDuration(ms) },
        splitLine: { lineStyle: { color: tokens.grid } },
      },
      series: (['medianMs', 'p95Ms'] as const).map((key, index) => ({
        name: index === 0 ? t('dashboard:durationTrend.median') : 'P95',
        type: 'line', smooth: false, connectNulls: false,
        showSymbol: true, showAllSymbol: true, symbolSize: 6,
        itemStyle: { color: tokens.series[index] },
        lineStyle: { width: 2, type: index === 0 ? 'solid' : 'dashed' },
        data: buckets.map((b, i) => b[key] == null ? null : ({
          value: b[key],
          symbol: buckets[i - 1]?.[key] == null && buckets[i + 1]?.[key] == null ? 'circle' : 'none',
        })),
      })),
    };
  }, [query.data, windowHours, t, tokens]);

  return (
    <div className="flex flex-col flex-1 min-h-[240px] min-w-0" aria-busy={query.isFetching}>
      <select className="np-input w-full text-xs mb-2" aria-label={t('dashboard:durationTrend.workflow')}
        value={workflowId} onChange={event => onWorkflowChange(event.target.value)}>
        <option value="">{t('dashboard:durationTrend.allWorkflows')}</option>
        {workflowId && !workflows.some(w => w.id === workflowId) && <option value={workflowId}>{t('dashboard:durationTrend.unavailable')}</option>}
        {workflows.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
      </select>
      <div className="flex items-center gap-4 text-xs text-on-surface-variant mb-1">
        <span className="flex items-center gap-1.5"><span className="w-4 border-t-2" style={{ borderColor: tokens.series[0] }} />{t('dashboard:durationTrend.median')}</span>
        <span className="flex items-center gap-1.5"><span className="w-4 border-t-2 border-dashed" style={{ borderColor: tokens.series[1] }} />P95</span>
      </div>
      <p className="text-[11px] text-outline mb-2">{t('dashboard:durationTrend.description')}</p>
      {query.isPending ? <p role="status" className="text-sm text-outline m-auto">{t('common:loadingDots')}</p>
        : query.isError ? <div role="alert" className="m-auto text-center text-sm">
          <p className="text-on-surface-variant mb-3">{t('dashboard:durationTrend.error')}</p>
          <button type="button" className="np-btn np-btn-secondary" onClick={() => void query.refetch()}>{t('dashboard:durationTrend.retry')}</button>
        </div>
        : !query.data?.buckets.some(b => b.count > 0) ? <p className="text-sm text-outline m-auto text-center">{t('dashboard:durationTrend.empty')}</p>
        : <EChart option={option} className="flex-1 min-h-40 w-full" ariaLabel={t('dashboard:durationTrend.title', { window: windowLabel })} />}
    </div>
  );
}
