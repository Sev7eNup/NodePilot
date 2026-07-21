import { useEffect, useRef } from 'react';
import * as echarts from 'echarts/core';
import { GaugeChart, LineChart, BarChart, PieChart, HeatmapChart } from 'echarts/charts';
import {
  GridComponent, TooltipComponent, MarkLineComponent, VisualMapComponent, LegendComponent,
} from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';
import type { EChartsOption } from 'echarts';

// SVG renderer (not Canvas) on purpose: it needs no `getContext('2d')`, so it
// renders crisp + scalable AND survives jsdom (vitest) without a canvas polyfill.
echarts.use([
  GaugeChart, LineChart, BarChart, PieChart, HeatmapChart,
  GridComponent, TooltipComponent, MarkLineComponent, VisualMapComponent, LegendComponent,
  SVGRenderer,
]);

/**
 * Thin, dependency-light React wrapper around ECharts core. We don't use
 * `echarts-for-react` because its peer-deps lag React 19; this hand-rolled
 * version is ~40 lines and fully under our control.
 */
export function EChart({
  option, className, style, ariaLabel, onClick,
}: Readonly<{
  option: EChartsOption;
  className?: string;
  style?: React.CSSProperties;
  ariaLabel?: string;
  /** Optional click handler — bound to the ECharts `click` event; receives the raw
   *  event params (incl. `data`, `name`, `dataIndex`). Used by dashboard charts that
   *  act as filters (e.g. donut segment → status filter). */
  onClick?: (params: unknown) => void;
}>) {
  const elRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    const el = elRef.current;
    if (!el) return;
    let chart: echarts.ECharts | null = null;
    try {
      chart = echarts.init(el, undefined, { renderer: 'svg' });
      chartRef.current = chart;
    } catch {
      // jsdom / zero-size container — render nothing, never crash the page.
      return;
    }
    const ro = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(() => chart?.resize())
      : null;
    ro?.observe(el);
    return () => {
      ro?.disconnect();
      chart?.dispose();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    try {
      chartRef.current?.setOption(option, true);
    } catch {
      /* ignore option churn in non-DOM envs */
    }
  }, [option]);

  // Bind/rebind the click handler whenever it changes. setOption doesn't touch event
  // bindings, so this is a separate effect — off() first to avoid stacking handlers.
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart || !onClick) return;
    const handler = (p: unknown) => onClick(p);
    chart.on('click', handler);
    return () => { chart.off('click', handler); };
  }, [onClick]);

  return (
    <div ref={elRef} className={className} style={style} role="img" aria-label={ariaLabel} />
  );
}
