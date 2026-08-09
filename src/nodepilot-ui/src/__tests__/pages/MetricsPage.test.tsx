import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { buildMetricsChartOption, MetricsPage } from '../../pages/MetricsPage';
import { DEFAULT_CHART_TOKENS } from '../../lib/chartTheme';
import type { MetricsWidget } from '../../types/api';

vi.mock('../../components/common/EChart', () => ({ EChart: ({ ariaLabel }: { ariaLabel?: string }) => <div role="img" aria-label={ariaLabel} /> }));

const BASE = 'http://localhost';
const server = setupServer(
  http.get(`${BASE}/api/observability/config`, () => HttpResponse.json({ enabled: true, prometheusAvailable: true, grafanaBaseUrl: 'http://localhost:3000' })),
  http.get(`${BASE}/api/observability/dashboards/:key`, () => HttpResponse.json({
    available: true, key: 'mission-control', title: 'Mission Control',
    panels: [], series: [], tables: [],
    widgets: [
      { id: 1, title: 'Active executions', description: null, type: 'stat', unit: 'short', grid: { x: 0, y: 0, width: 3, height: 4 }, data: [{ label: 'Value', labels: {}, points: [{ timestamp: 1, value: 3 }] }], error: null },
      { id: 2, title: 'Top failing workflows', description: null, type: 'bargauge', unit: 'short', grid: { x: 0, y: 4, width: 12, height: 8 }, data: [{ label: 'Import users', labels: { workflow_name: 'Import users' }, points: [{ timestamp: 1, value: 4 }] }], error: null },
    ],
  })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderPage(path = '/metrics/mission-control') {
  const originalFetch = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => typeof input === 'string' && input.startsWith('/') ? originalFetch(`${BASE}${input}`, init) : originalFetch(input, init));
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[path]}><Routes><Route path="/metrics/:section" element={<MetricsPage />} /></Routes></MemoryRouter></QueryClientProvider>);
}

describe('MetricsPage', () => {
  it('renders curated metric panels, navigation, and Grafana drill-down', async () => {
    renderPage();
    expect(await screen.findByText('Active executions')).toBeInTheDocument();
    expect(screen.getByText('Top failing workflows')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open in Grafana' })).toHaveAttribute('href', 'http://localhost:3000/d/nodepilot-mission-control');
    expect(screen.getByRole('link', { name: 'Workflows' })).toHaveAttribute('href', '/metrics/workflows');
  });

  it('shows setup guidance when Prometheus is unavailable', async () => {
    server.use(http.get(`${BASE}/api/observability/config`, () => HttpResponse.json({ enabled: true, prometheusAvailable: false })));
    renderPage();
    expect(await screen.findByText('Prometheus is not configured')).toBeInTheDocument();
  });

  it.each(['bargauge', 'piechart', 'heatmap'] as const)('does not turn missing %s values into zero', (type) => {
    const widget: MetricsWidget = {
      id: 99, title: 'Missing values', description: null, type, unit: 'short',
      grid: { x: 0, y: 0, width: 12, height: 8 }, error: null,
      data: [{ label: 'undefined', labels: {}, points: [{ timestamp: 1, value: null }] }],
    };
    const option = buildMetricsChartOption(widget) as { series?: Array<{ data?: unknown[] }> };
    expect(option.series?.[0]?.data).toEqual([]);
  });

  it('paintsAxesLegendAndSeriesFromTheSuppliedThemeTokens', () => {
    // The page used to hardcode #94a3b8 for every axis and legend and a fixed
    // 8-colour series list, so it looked identical in every skin — including light.
    const widget: MetricsWidget = {
      id: 100, title: 'Latency', description: null, type: 'timeseries', unit: 'ms',
      grid: { x: 0, y: 0, width: 12, height: 8 }, error: null,
      data: [
        { label: 'p50', labels: {}, points: [{ timestamp: 1, value: 10 }] },
        { label: 'p95', labels: {}, points: [{ timestamp: 1, value: 20 }] },
      ],
    };
    const tokens = { ...DEFAULT_CHART_TOKENS, axis: '#123456', grid: '#654321', series: ['#aaaaaa', '#bbbbbb'] };
    const option = buildMetricsChartOption(widget, tokens) as {
      legend?: { textStyle?: { color?: string } };
      xAxis?: { axisLabel?: { color?: string } };
      yAxis?: { axisLabel?: { color?: string }; splitLine?: { lineStyle?: { color?: string } } };
      series?: Array<{ itemStyle?: { color?: string } }>;
    };
    expect(option.legend?.textStyle?.color).toBe('#123456');
    expect(option.xAxis?.axisLabel?.color).toBe('#123456');
    expect(option.yAxis?.splitLine?.lineStyle?.color).toBe('#654321');
    // Colour follows the entity by slot order, so filtering a series out must not
    // repaint the survivors.
    expect(option.series?.map((s) => s.itemStyle?.color)).toEqual(['#aaaaaa', '#bbbbbb']);
  });

  it('ordersHeatmapTimestampsNumericallyNotLexicographically', () => {
    // The axis used to be built from a bare .sort(), which compares stringified numbers:
    // [1, 2, 10] came out as [1, 10, 2], so every heatmap covering a window that crosses a
    // digit boundary drew its columns out of order and put each cell under the wrong time.
    // The single-timestamp fixtures above cannot see this - the bug needs at least three.
    const widget: MetricsWidget = {
      id: 102, title: 'Failures over time', description: null, type: 'heatmap', unit: 'short',
      grid: { x: 0, y: 0, width: 12, height: 8 }, error: null,
      data: [{
        label: 'bucket',
        labels: {},
        // Deliberately supplied out of order, so the assertion pins the sort rather than
        // the order the points happened to arrive in.
        points: [
          { timestamp: 10, value: 3 },
          { timestamp: 1, value: 1 },
          { timestamp: 2, value: 2 },
        ],
      }],
    };
    const option = buildMetricsChartOption(widget) as {
      xAxis?: { data?: string[] };
      series?: Array<{ data?: Array<[number, number, number]> }>;
    };

    // Three distinct columns, and the value at each x index belongs to the timestamp that
    // index stands for: x=0 -> ts 1 -> value 1, x=1 -> ts 2 -> value 2, x=2 -> ts 10 -> value 3.
    expect(option.xAxis?.data).toHaveLength(3);
    const byIndex = new Map(option.series?.[0]?.data?.map(([x, , value]) => [x, value]));
    expect(byIndex.get(0)).toBe(1);
    expect(byIndex.get(1)).toBe(2);
    expect(byIndex.get(2)).toBe(3);
  });

  it('assignsSeriesColoursBySlotOrderWithoutCycling', () => {
    const widget: MetricsWidget = {
      id: 101, title: 'Two series', description: null, type: 'timeseries', unit: 'short',
      grid: { x: 0, y: 0, width: 12, height: 8 }, error: null,
      data: [
        { label: 'a', labels: {}, points: [{ timestamp: 1, value: 1 }] },
        { label: 'b', labels: {}, points: [{ timestamp: 1, value: 2 }] },
      ],
    };
    const first = buildMetricsChartOption(widget) as { series?: Array<{ itemStyle?: { color?: string } }> };
    const [slot1, slot2] = first.series!.map((s) => s.itemStyle?.color);
    expect(slot1).toBe(DEFAULT_CHART_TOKENS.series[0]);
    expect(slot2).toBe(DEFAULT_CHART_TOKENS.series[1]);
    expect(slot1).not.toBe(slot2);
  });
});
