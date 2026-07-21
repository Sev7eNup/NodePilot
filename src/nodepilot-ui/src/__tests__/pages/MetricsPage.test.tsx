import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { buildMetricsChartOption, MetricsPage } from '../../pages/MetricsPage';
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
});
