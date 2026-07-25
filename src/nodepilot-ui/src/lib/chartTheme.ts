import { useEffect, useRef, useState } from 'react';

/**
 * Shared chart theming for every ECharts surface (Dashboard, Metrics).
 *
 * ECharts renders to SVG/Canvas and cannot resolve `var(--color-*)`, so chart
 * colours have to be concrete hex. This module reads the live, skin-scoped design
 * tokens once per theme change and hands them to the chart builders.
 *
 * ── Categorical series palette ────────────────────────────────────────────────
 * Eight fixed slots, assigned in order and never cycled — colour follows the
 * entity, not its rank, so filtering series out must not repaint the survivors.
 * Light and dark are the same eight hues stepped for their own surface; dark is a
 * selected set, not an automatic flip.
 *
 * Validated as a set against the app's real chart surfaces (light card #ffffff,
 * dark card #1a1c21) on the adjacent pairlist used by lines, bars and stacks:
 *   dark  — lightness band, chroma floor, CVD ΔE 8.4, normal-vision ΔE 19.3,
 *           contrast >= 3:1 … all pass
 *   light — CVD ΔE 9.1, normal-vision ΔE 19.6; aqua/yellow/magenta land below
 *           3:1 on white, which obliges secondary encoding. Every chart that uses
 *           this palette ships a text legend, so identity is never colour-alone.
 *
 * Past three series, scatter/bubble forms (which compare ALL pairs, not just
 * adjacent ones) must fold to "Other" or facet instead — slot 4 puts yellow and
 * orange on screen together and that pair fails the all-pairs floors.
 *
 * These are deliberately NOT the semantic status colours. success/warning/error
 * stay reserved for state and never stand in for "series 4".
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
  /** Split lines / grid lines. Recessive by contract. */
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

/** Literals used when no probe has resolved yet (jsdom, first paint). They mirror
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
 * Re-reads on BOTH `class` and `data-skin`: switching e.g. dark → dark-lila keeps
 * the same `class` (both are `dark` + `np-accent-remap`) and only flips
 * `data-skin`, so a class-only filter would leave charts on the previous skin
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

/** Chart-builder default, so pure builders stay callable without a probe (tests). */
export const DEFAULT_CHART_TOKENS: ChartTokens = FALLBACK;
