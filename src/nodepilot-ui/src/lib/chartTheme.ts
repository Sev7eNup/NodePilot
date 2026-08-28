import { useEffect, useRef, useState } from 'react';

/**
 * Shared chart theming for every ECharts surface (Dashboard, Metrics).
 *
 * ECharts renders to SVG and Canvas and cannot resolve `var(--color-*)`, so chart
 * colours have to be concrete hex values. This module reads the live, skin-scoped
 * design tokens once per theme change and hands them to the chart builders.
 *
 * The categorical palette has eight fixed slots, assigned in order and never cycled,
 * so a colour belongs to an entity rather than to its rank and filtering series out
 * leaves the remaining colours unchanged. Light and dark hold the same hues, each
 * tuned for its own surface. Several light hues stay below 3:1 contrast on white, so
 * charts using the palette also carry a text legend and never encode identity by
 * colour alone. Scatter and bubble charts compare all pairs at once, so past three
 * series they group the rest or facet instead.
 *
 * These slots are not the semantic status colours: success, warning and error stay
 * reserved for state.
 */
export const CHART_SERIES_LIGHT = [
  '#2a78d6', // 1 blue
  '#eb6834', // 2 orange
  '#1baf7a', // 3 aqua
  '#eda100', // 4 yellow
  '#e87ba4', // 5 magenta
  '#008300', // 6 green
  '#4a3aa7', // 7 violet
  '#e34948', // 8 red
] as const;

export const CHART_SERIES_DARK = [
  '#3987e5', // 1 blue
  '#d95926', // 2 orange
  '#199e70', // 3 aqua
  '#c98500', // 4 yellow
  '#d55181', // 5 magenta
  '#008300', // 6 green
  '#9085e9', // 7 violet
  '#e66767', // 8 red
] as const;

export interface ChartTokens {
  isDark: boolean;
  /** Axis labels, legend text, tick text. */
  axis: string;
  /** Split lines and grid lines. Kept visually recessive. */
  grid: string;
  /** Tooltip + popup background. */
  surfaceHigh: string;
  /** Tooltip + popup text. */
  onSurface: string;
  /** Accent gradient stops, for the accent-coloured bars on the dashboard. */
  primaryContainer: string;
  primary: string;
  /** The categorical palette for the active base. */
  series: readonly string[];
}

/** Values used before a probe resolves, such as in jsdom or on first paint. They mirror
 *  the dark skin, which is what `system` resolves to on a dark OS. */
const FALLBACK: ChartTokens = {
  isDark: false,
  axis: '#9ca3af',
  grid: 'rgba(148,163,184,.16)',
  surfaceHigh: '#212328',
  onSurface: '#e9ebef',
  primaryContainer: '#2467d9',
  primary: '#6da8ff',
  series: CHART_SERIES_LIGHT,
};

/**
 * Mount the returned `probeRef` on any element inside the themed scope
 * (`.np-shell` / `.np-designer`), then feed `tokens` to the chart builders.
 *
 * Re-reads on both `class` and `data-skin`: switching from dark to dark-lila keeps
 * the same `class` (both are `dark` plus `np-accent-remap`) and only changes
 * `data-skin`, so watching `class` alone would leave charts on the previous skin
 * until a reload.
 */
export function useChartTokens(): { probeRef: React.RefObject<HTMLDivElement | null>; tokens: ChartTokens } {
  const probeRef = useRef<HTMLDivElement>(null);
  const [tokens, setTokens] = useState<ChartTokens>(FALLBACK);

  useEffect(() => {
    const read = () => {
      const el = probeRef.current;
      if (!el) return;
      const cs = getComputedStyle(el);
      const g = (name: string, fallback: string) => cs.getPropertyValue(name).trim() || fallback;
      const isDark = document.documentElement.classList.contains('dark');
      setTokens({
        isDark,
        axis: g('--color-on-surface-variant', FALLBACK.axis),
        grid: g('--color-outline-variant', FALLBACK.grid),
        surfaceHigh: g('--color-surface-high', FALLBACK.surfaceHigh),
        onSurface: g('--color-on-surface', FALLBACK.onSurface),
        primaryContainer: g('--color-primary-container', FALLBACK.primaryContainer),
        primary: g('--color-primary', FALLBACK.primary),
        series: isDark ? CHART_SERIES_DARK : CHART_SERIES_LIGHT,
      });
    };
    read();
    const mo = new MutationObserver(read);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'data-skin'] });
    return () => mo.disconnect();
  }, []);

  return { probeRef, tokens };
}

/** Default for chart builders, so they stay callable without a probe, as in tests. */
export const DEFAULT_CHART_TOKENS: ChartTokens = FALLBACK;
