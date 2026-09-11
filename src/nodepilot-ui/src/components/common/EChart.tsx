import { useEffect, useRef } from 'react';
import * as echarts from 'echarts/core';
import { GaugeChart, LineChart, BarChart, PieChart, HeatmapChart } from 'echarts/charts';
import {
  GridComponent, TooltipComponent, MarkLineComponent, VisualMapComponent, LegendComponent,
} from 'echarts/components';
import { CanvasRenderer, SVGRenderer } from 'echarts/renderers';
import type { EChartsOption } from 'echarts';

// SVG is the default renderer on purpose: it needs no `getContext('2d')`, so it renders crisp
// and scalable, and works in jsdom (vitest) without a canvas polyfill. Canvas is registered
// alongside it for the one chart type that draws a cell per data point — see the `renderer`
// prop below.
echarts.use([
  GaugeChart, LineChart, BarChart, PieChart, HeatmapChart,
  GridComponent, TooltipComponent, MarkLineComponent, VisualMapComponent, LegendComponent,
  SVGRenderer, CanvasRenderer,
]);

/**
 * Thin wrapper around ECharts core, used instead of `echarts-for-react` because its
 * peer dependencies lag behind React 19. Keeping it hand-rolled keeps it small and
 * fully under this project's control.
 */
export function EChart({
  option, className, style, ariaLabel, onClick, renderer = 'svg',
}: Readonly<{
  option: EChartsOption;
  className?: string;
  style?: React.CSSProperties;
  ariaLabel?: string;
  /** Optional click handler — bound to the ECharts `click` event; receives the raw
   *  event params (incl. `data`, `name`, `dataIndex`). Used by dashboard charts that
   *  act as filters (e.g. a donut segment click sets a status filter). */
  onClick?: (params: unknown) => void;
  /** SVG everywhere by default. Canvas is for charts that draw one mark per data point —
   *  a heatmap over a day at minute resolution is tens of thousands of them, and as SVG that
   *  is tens of thousands of DOM elements. Same data, same values, one element. */
  renderer?: 'svg' | 'canvas';
}>) {
  const elRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    const el = elRef.current;
    if (!el) return;
    let chart: echarts.ECharts | null = null;
    try {
      chart = echarts.init(el, undefined, { renderer });
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
  }, [renderer]);

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
