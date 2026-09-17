import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { DashboardPage } from '../../pages/DashboardPage';
import { useAuthStore } from '../../stores/authStore';
import i18n from '../../i18n';
import * as chartTheme from '../../lib/chartTheme';
import type { EChartsOption } from 'echarts';

// The dashboard now subscribes to the SignalR ops-feed for live updates. Mock the hook
// to a no-op (mirrors OperationsPage.test) so no real WebSocket is opened under jsdom.
vi.mock('../../hooks/useDashboardFeed', () => ({ useDashboardFeed: () => {} }));
vi.mock('../../components/common/EChart', () => ({
  EChart: ({ ariaLabel, option }: { ariaLabel?: string; option: EChartsOption }) =>
    <div role="img" aria-label={ariaLabel} data-chart-option={JSON.stringify(option)} />,
}));

const BASE = 'http://localhost';

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/'))
      return orig(`${BASE}${input}`, init);
    return orig(input, init);
  });
}

const server = setupServer(
  http.get(`${BASE}/api/stats/duration-trend`, () => HttpResponse.json({ buckets: [], workflows: [] })),
  http.get(`${BASE}/api/stats/failure-causes`, () => HttpResponse.json({ totalFailed: 0, groups: [], remainingCount: 0 })),
  http.get(`${BASE}/api/observability/config`, () =>
    HttpResponse.json({ enabled: false, traceUiUrlTemplate: null, traceBackendName: null })
  )
);
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
  // Role mutations from individual tests (e.g. Admin banner-shortcut test) must not leak
  // into siblings — reset to the default Viewer so every other test stays role-stable.
  useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: false });
});
afterAll(() => server.close());

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <DashboardPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const BASE_STATS = {
  workflowsTotal: 12,
  workflowsEnabled: 8,
  machinesTotal: 5,
  machinesReachable: 5,
  executionsTotal: 250,
  last24h: { total: 30, succeeded: 28, failed: 2, running: 0, cancelled: 0 },
  retryStats: { finishedCount: 30, retriedCount: 2 },
  last24hBuckets: [],
  topWorkflows: [
    {
      id: 'wf-1', name: 'Disk Check',
      runCount: 10, successCount: 9, failCount: 1,
      avgDurationMs: 12400, p95DurationMs: 38000,
    },
  ],
  running: [],
  armedTriggers: [],
  pendingCount: 0,
  runningCount: 0,
  longRunningCount: 0,
  longRunningSeconds: 600,
  failingWorkflows: [],
  editLocks: [],
  healthHeartbeats: [
    {
      serviceName: 'TriggerOrchestrator',
      lastHeartbeatAt: new Date().toISOString(),
      expectedIntervalSeconds: 30,
      status: 'ok',
      isStale: false,
    },
  ],
  databaseProvider: 'postgres',
  clusterRole: null,
  recentAudit: null,
  recent: [
    {
      id: 'exec-1', workflowId: 'wf-1', workflowName: 'Disk Check',
      status: 'Succeeded', startedAt: new Date(Date.now() - 5000).toISOString(),
      completedAt: new Date(Date.now() - 1000).toISOString(),
      durationMs: 4000, triggeredBy: 'schedule',
    },
  ],
};

describe('DashboardPage', () => {
  it.each([false, true])('keeps chart data while reduced decoration is %s', async (reducedDecoration) => {
    vi.spyOn(chartTheme, 'useChartTokens').mockReturnValue({
      probeRef: { current: null }, tokens: { ...chartTheme.DEFAULT_CHART_TOKENS, reducedDecoration },
    });
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24hBuckets: [{ hourStart: '2026-09-14T10:00:00Z', succeeded: 3, failed: 1, cancelled: 2 }],
    })));
    server.use(http.get(`${BASE}/api/stats/duration-trend`, () => HttpResponse.json({
      buckets: [{ startedAt: '2026-09-14T10:00:00Z', count: 4, medianMs: 1000, p95Ms: 3000 }], workflows: [],
    })));
    const { container } = renderPage();
    await waitFor(() => expect(container.querySelectorAll('[data-chart-option]').length).toBe(3));
    const series = Array.from(container.querySelectorAll<HTMLElement>('[data-chart-option]'))
      .flatMap((chart) => JSON.parse(chart.dataset.chartOption!).series);
    const gauge = series.find((entry) => entry.type === 'gauge');
    expect(gauge.progress.itemStyle.shadowBlur).toBe(reducedDecoration ? 0 : 22);
    const stacked = series.filter((entry) => entry.stack === 'total');
    expect(stacked.map((entry) => entry.data)).toEqual([[3], [1], [2]]);
    expect(new Set(stacked.map((entry) => entry.lineStyle.color)).size).toBe(3);
    for (const entry of series.filter((entry) => entry.areaStyle)) {
      expect(typeof entry.areaStyle.color).toBe(reducedDecoration ? 'string' : 'object');
      if (reducedDecoration) expect(entry.lineStyle.shadowBlur ?? 0).toBe(0);
    }
  });

  it.each([600, 1800])('uses the API threshold %s for the tooltip and running-duration warning', async (seconds) => {
    useAuthStore.setState({ userId: 'admin', username: 'admin', role: 'Admin', isAuthenticated: true });
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      longRunningSeconds: seconds,
      running: [{ id: 'running-1', workflowId: 'wf-1', workflowName: 'Threshold test',
        status: 'Running', startedAt: new Date(Date.now() - 15 * 60_000).toISOString(), triggeredBy: 'manual' }],
    })));
    renderPage();
    const row = (await screen.findByText('Threshold test')).closest('li')!;
    expect(row.querySelector('span.text-red-600') !== null).toBe(seconds === 600);
    expect(screen.getByTitle(`Long-running (> ${seconds / 60}m 0s)`)).toBeInTheDocument();
  });

  it('shows loading state initially', () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => new Promise(() => {})));
    renderPage();
    expect(screen.getByText(/loading/i)).toBeInTheDocument();
  });

  it('renders KPI strip after load', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Workflows')).toBeInTheDocument());
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(screen.getByText('8 enabled')).toBeInTheDocument();
    expect(screen.getByText('Machines')).toBeInTheDocument();
  });

  it.each([
    [0, 0, '—', 'No finished executions'],
    [100, 0, '0%', '0 executions'],
    [16, 1, '6.3%', '1 execution'],
    [59_516, 3690, '6.2%', '3,690 executions'],
    [1, 1, '100%', '1 execution'],
    [1000, 1, '0.1%', '1 execution'],
    [10000, 1, '<0.1%', '1 execution'],
  ])('renders retry share for %s finished and %s retried executions', async (finishedCount, retriedCount, value, hint) => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      retryStats: { finishedCount, retriedCount },
    })));
    renderPage();
    const card = (await screen.findByText('Retries needed (24h)')).closest('div.np-card') as HTMLElement;
    expect(within(card).getByText(value)).toBeInTheDocument();
    expect(within(card).getByText(hint)).toBeInTheDocument();
    expect(screen.queryByText('Runs (24h)')).not.toBeInTheDocument();
    expect(within(card).queryByRole('link')).not.toBeInTheDocument();
  });

  it('explains automatic retries against finished executions with a keyboard-accessible disclosure', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    const summary = await screen.findByLabelText('How the retry share is calculated');
    expect(summary.closest('details')).not.toHaveAttribute('open');
    summary.focus();
    // jsdom does not implement the browser's default keyboard action for summary;
    // the browser test below exercises Enter, while click verifies disclosure content here.
    await userEvent.click(summary);
    expect(summary.closest('details')).toHaveAttribute('open');
    expect(screen.getByText(/Of 30 finished executions.*automatically retried activity/)).toBeVisible();
  });

  it('updates the retry KPI and its denominator with the selected window', async () => {
    const hours: string[] = [];
    server.use(http.get(`${BASE}/api/stats/dashboard`, ({ request }) => {
      const selected = new URL(request.url).searchParams.get('windowHours')!;
      hours.push(selected);
      return HttpResponse.json({ ...BASE_STATS, retryStats: selected === '168'
        ? { finishedCount: 100, retriedCount: 10 } : BASE_STATS.retryStats });
    }));
    renderPage();
    await screen.findByText('Retries needed (24h)');
    await userEvent.click(screen.getByRole('button', { name: '7 days' }));
    const card = (await screen.findByText('Retries needed (7 days)')).closest('div.np-card')!;
    expect(within(card as HTMLElement).getByText('10%')).toBeInTheDocument();
    expect(hours).toEqual(['24', '168']);
  });

  it('localizes retry counts and very small positive percentages in German', async () => {
    await i18n.changeLanguage('de');
    try {
      server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
        ...BASE_STATS, retryStats: { finishedCount: 100_000_000, retriedCount: 3690 },
      })));
      renderPage();
      const card = (await screen.findByText('Wiederholungen nötig (24 h)')).closest('div.np-card') as HTMLElement;
      expect(within(card).getByText(/<0,1\s%/)).toBeInTheDocument();
      expect(within(card).getByText('3.690 Ausführungen')).toBeInTheDocument();
    } finally { await i18n.changeLanguage('en'); }
  });

  it('renders top workflows panel with avg/p95 duration', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Top Workflows (7 days)')).toBeInTheDocument());
    expect(screen.getByText('10 runs')).toBeInTheDocument();
    expect(screen.getByText(/avg/i)).toBeInTheDocument();
    expect(within(screen.getByText('Top Workflows (7 days)').closest('.np-card') as HTMLElement).getByText(/p95/i)).toBeInTheDocument();
  });

  it('renders recent executions with status', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getAllByText('Succeeded').length).toBeGreaterThanOrEqual(1));
  });

  it('renders the window selector with 1h/24h/7d/30d options and requests the selected window', async () => {
    const dash = vi.fn(() => HttpResponse.json(BASE_STATS));
    server.use(http.get(`${BASE}/api/stats/dashboard`, dash));
    renderPage();
    await waitFor(() => expect(screen.getByText('Workflows')).toBeInTheDocument());
    expect(screen.getByText('7 days')).toBeInTheDocument();
    expect(screen.getByText('30 days')).toBeInTheDocument();
    // Default load uses windowHours=24. MSW v2 hands the resolver { request, requestId, ... };
    // the Request's `url` is the full string we asserted on.
    expect(dash).toHaveBeenCalledWith(expect.objectContaining({
      request: expect.objectContaining({ url: expect.stringContaining('windowHours=24') }),
    }));

    const userEvent = (await import('@testing-library/user-event')).default;
    await userEvent.click(screen.getByText('7 days'));
    await waitFor(() => expect(dash).toHaveBeenCalledWith(expect.objectContaining({
      request: expect.objectContaining({ url: expect.stringContaining('windowHours=168') }),
    })));
  });

  it('charts the one-hour window as thirty two-minute line buckets', async () => {
    const pad = (n: number) => String(n).padStart(2, '0');
    const starts = Array.from({ length: 30 }, (_, i) => new Date(2026, 8, 15, 21, 14 + 2 * i));
    const buckets = starts.map((start, i) => ({
      hourStart: start.toISOString(), succeeded: i % 4, failed: i % 7 === 0 ? 1 : 0, cancelled: i % 10 === 0 ? 1 : 0,
    }));
    server.use(http.get(`${BASE}/api/stats/dashboard`, ({ request }) => HttpResponse.json({
      ...BASE_STATS,
      last24hBuckets: new URL(request.url).searchParams.get('windowHours') === '1' ? buckets : [],
    })));
    renderPage();
    await screen.findByText('Workflows');
    await userEvent.click(screen.getByRole('button', { name: '1h' }));
    const chart = await screen.findByRole('img', { name: 'Executions — 1h' });
    const option = JSON.parse(chart.dataset.chartOption!);
    const stacked = option.series.filter((entry: { stack?: string }) => entry.stack === 'total');
    expect(stacked).toHaveLength(3);
    for (const entry of stacked) {
      expect(entry.type).toBe('line');
      expect(entry).not.toHaveProperty('barMaxWidth');
      expect(entry.lineStyle.color).toEqual(expect.any(String));
      expect(entry.areaStyle.color).toBeDefined();
    }
    expect(stacked.map((entry: { data: number[] }) => entry.data)).toEqual([
      buckets.map((b) => b.succeeded), buckets.map((b) => b.failed), buckets.map((b) => b.cancelled),
    ]);
    expect(option.xAxis.boundaryGap).toBe(false);
    expect(option.xAxis.data).toEqual(starts.map((d) => `${pad(d.getHours())}:${pad(d.getMinutes())}`));
  });

  it('renders viewer quick-action bar without mutating buttons', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText(/Quick actions/i)).toBeInTheDocument());
    // Viewer: no create/show-failed/review buttons.
    expect(screen.queryByText(/New workflow/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Show failed/i)).not.toBeInTheDocument();
  });

  it('renders the system-health banner with scheduler status', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText(/System status/i)).toBeInTheDocument());
    expect(screen.getByText(/Scheduler OK/i)).toBeInTheDocument();
  });

  it('renders LLM-config + personal shortcuts in the quick-action bar for Admin', async () => {
    useAuthStore.setState({ userId: 'a1', username: 'admin', role: 'Admin', isAuthenticated: true });
    server.use(
      http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)),
      http.get(`${BASE}/api/maintenance-windows`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/alerting/deliveries`, () => HttpResponse.json([])),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText(/Quick actions/i)).toBeInTheDocument());
    // Admin-only LLM shortcut + all-roles personal shortcut sit next to the alert-rule button.
    expect(screen.getByRole('button', { name: /Show failed/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Review long-running/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /LLM config/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Personal settings/i })).toBeInTheDocument();
  });

  it('dismisses the New Workflow input when clicking elsewhere on the dashboard', async () => {
    // Regression: the inline "new workflow" name input only closed on Escape/submit, so
    // changing your mind and clicking elsewhere left it stuck open. It must close on any
    // outside click. Admin role is required for the mutating quick-action to render.
    useAuthStore.setState({ userId: 'a1', username: 'admin', role: 'Admin', isAuthenticated: true });
    server.use(
      http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)),
      http.get(`${BASE}/api/maintenance-windows`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/alerting/deliveries`, () => HttpResponse.json([])),
    );
    renderPage();
    await waitFor(() => expect(screen.getByText(/Quick actions/i)).toBeInTheDocument());

    const userEvent = (await import('@testing-library/user-event')).default;
    // Open the inline name input.
    await userEvent.click(screen.getByRole('button', { name: /New workflow/i }));
    expect(screen.getByPlaceholderText(/Name of the new workflow/i)).toBeInTheDocument();

    // Click a KPI label that lives outside the new-workflow control -> the input must close.
    await userEvent.click(screen.getByText('Workflows'));
    expect(screen.queryByPlaceholderText(/Name of the new workflow/i)).not.toBeInTheDocument();
  });

  it('filters recent executions using status rows and toggles the filter with the keyboard', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24h: { total: 2, succeeded: 1, failed: 1, running: 0, cancelled: 0 },
      recent: [
        { id: 'e-s', workflowId: 'wf-1', workflowName: 'Succ WF', status: 'Succeeded', startedAt: new Date().toISOString(), completedAt: new Date().toISOString(), durationMs: 100, triggeredBy: 'schedule' },
        { id: 'e-f', workflowId: 'wf-1', workflowName: 'Fail WF', status: 'Failed', startedAt: new Date().toISOString(), completedAt: new Date().toISOString(), durationMs: 100, triggeredBy: 'schedule' },
      ],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Succ WF')).toBeInTheDocument());
    expect(screen.getByText('Fail WF')).toBeInTheDocument();
    // No filter chip initially.
    expect(screen.queryByText(/Show only/i)).not.toBeInTheDocument();
    const userEvent = (await import('@testing-library/user-event')).default;
    const summary = screen.getByRole('group', { name: 'Run Status (24h)' });
    const failed = within(summary).getByRole('button', { name: /Failed/ });
    await userEvent.click(failed);
    expect(failed).toHaveAttribute('aria-pressed', 'true');
    expect(screen.queryByText('Succ WF')).not.toBeInTheDocument();
    expect(screen.getByText('Fail WF')).toBeInTheDocument();
    expect(failed).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    expect(failed).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByText('Succ WF')).toBeInTheDocument();
    await userEvent.keyboard(' ');
    expect(failed).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(screen.getByRole('button', { name: '7 days' }));
    const nextSummary = await screen.findByRole('group', { name: 'Run Status (7 days)' });
    expect(within(nextSummary).getByRole('button', { name: /Failed/ })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByText('Succ WF')).toBeInTheDocument();
  });

  it('renders Next-Fire badge for cron triggers', async () => {
    // 5min + 30s buffer so the floor() in formatRelativeFuture lands deterministically at 5.
    const future = new Date(Date.now() + 5 * 60_000 + 30_000).toISOString();
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      armedTriggers: [{
        workflowId: 'wf-1', workflowName: 'Nightly',
        triggerTypes: ['scheduleTrigger'],
        nextFireUtc: future, nextFireKind: 'cron', pollIntervalSeconds: null,
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Nightly')).toBeInTheDocument());
    expect(screen.getByText(/in [45]m/i)).toBeInTheDocument();
  });

  it('renders event-driven label for fileWatcher triggers', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      armedTriggers: [{
        workflowId: 'wf-2', workflowName: 'Drop Watcher',
        triggerTypes: ['fileWatcherTrigger'],
        nextFireUtc: null, nextFireKind: 'event-driven', pollIntervalSeconds: null,
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Drop Watcher')).toBeInTheDocument());
    expect(screen.getByText('event-driven')).toBeInTheDocument();
  });

  it('renders Poll Xs label for databaseTrigger', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      armedTriggers: [{
        workflowId: 'wf-3', workflowName: 'DB Poller',
        triggerTypes: ['databaseTrigger'],
        nextFireUtc: null, nextFireKind: 'polling', pollIntervalSeconds: 30,
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('DB Poller')).toBeInTheDocument());
    expect(screen.getByText(/Poll 30s/i)).toBeInTheDocument();
  });

  it('shows failing workflows panel content when failures exist', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      failingWorkflows: [{
        id: 'wf-fail-1', name: 'Crashy Job',
        failCount: 7, runCount: 10,
        lastFailureAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Crashy Job')).toBeInTheDocument());
    expect(screen.getByText('7✗')).toBeInTheDocument();
  });

  it('shows edit locks panel content when locks exist', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      editLocks: [{
        workflowId: 'wf-lock-1', workflowName: 'Halted Work',
        lockOwnerUserName: 'alice',
        lockedAt: new Date(Date.now() - 10 * 60_000).toISOString(),
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Halted Work')).toBeInTheDocument());
    expect(screen.getByText(/alice/i)).toBeInTheDocument();
  });

  it('hides admin-audit panel for non-admins (recentAudit=null)', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Workflows')).toBeInTheDocument());
    expect(screen.queryByText(/Recent admin events/i)).not.toBeInTheDocument();
  });

  it('shows admin-audit panel when recentAudit is present', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      recentAudit: [{
        timestamp: new Date(Date.now() - 5 * 60_000).toISOString(),
        actorUserName: 'admin',
        action: 'WORKFLOW_PUBLISHED',
        resourceType: 'Workflow',
        resourceId: 'wf-pub-1',
      }],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText(/Recent admin events/i)).toBeInTheDocument());
    expect(screen.getByText('WORKFLOW_PUBLISHED')).toBeInTheDocument();
    expect(screen.getByText('admin')).toBeInTheDocument();
  });

  it('shows long-running indicator in queue KPI', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      pendingCount: 0,
      runningCount: 3,
      longRunningCount: 1,
    })));
    renderPage();
    const card = (await screen.findByText('Queue', { exact: true })).closest('.np-card')! as HTMLElement;
    expect(within(within(card).getByText('Pending').closest('div')!).getByText('0')).toBeInTheDocument();
    expect(within(within(card).getByText('Running').closest('div')!).getByText('3')).toBeInTheDocument();
    expect(within(card).getByText('1 of these long-running')).toBeInTheDocument();
  });

  it.each([
    ['en', null, 'Disabled', 'Single node', 'HA: disabled'],
    ['de', null, 'Deaktiviert', 'Einzelknoten', 'HA: deaktiviert'],
    ['en', 'leader', 'Leader', 'HA enabled', 'HA: leader'],
    ['en', 'standby', 'Standby', 'HA enabled', 'HA: standby'],
    ['de', 'leader', 'Leader', 'HA aktiviert', 'HA: Leader'],
    ['de', 'standby', 'Standby', 'HA aktiviert', 'HA: Standby'],
  ] as const)('shows HA mode and role in %s with role %s', async (language, clusterRole, value, hint, banner) => {
    await i18n.changeLanguage(language);
    try {
      server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({ ...BASE_STATS, clusterRole })));
      renderPage();
      const card = (await screen.findByText('HA', { exact: true })).closest('.np-card')! as HTMLElement;
      expect(within(card).getByText(value)).toBeInTheDocument();
      expect(within(card).getByText(hint)).toBeInTheDocument();
      expect(screen.getByText(banner)).toBeInTheDocument();
      if (clusterRole === null) {
        expect(within(card).queryByText('Leader')).not.toBeInTheDocument();
        expect(screen.queryByText(/HA: (active|aktiv)$/)).not.toBeInTheDocument();
      }
    } finally {
      await i18n.changeLanguage('en');
    }
  });

  it('keeps the Currently Running list in an out-of-flow scroll container (no row blow-out)', async () => {
    // Layout regression: a long running list must not drive the hero-row height — it scrolls
    // inside an absolutely-positioned region instead. jsdom has no layout, so we pin the
    // structure that makes that true. The real visual check lives in e2e dashboard.spec 11.1c.
    const running = Array.from({ length: 12 }, (_, i) => ({
      id: `run-${i + 1}`,
      workflowId: 'wf-1',
      workflowName: `Running WF ${i + 1}`,
      status: 'Running',
      startedAt: new Date(Date.now() - (i + 1) * 1000).toISOString(),
      triggeredBy: 'schedule',
    }));
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      running,
      runningCount: running.length,
      last24h: { ...BASE_STATS.last24h, running: running.length },
    })));
    renderPage();

    await waitFor(() => expect(screen.getByText('Currently Running')).toBeInTheDocument());

    // All running rows render — none silently dropped.
    for (let i = 1; i <= running.length; i++) {
      expect(screen.getByText(`Running WF ${i}`)).toBeInTheDocument();
    }

    // The list lives in an out-of-flow scroller (absolute inset-0) inside a relative parent,
    // so its content can't stretch the grid row — it scrolls within the row height instead.
    const card = screen.getByText('Currently Running').closest('div.np-card') as HTMLElement;
    const scroller = card.querySelector('.overflow-y-auto') as HTMLElement;
    expect(scroller).toHaveClass('absolute', 'inset-0', 'overflow-y-auto');
    expect(scroller.parentElement).toHaveClass('relative');
  });

  // ── Insights (run-status summary · execution duration · recurring errors) ──

  it('renders the run-status summary with total and all four statuses including zero counts', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Run Status (24h)')).toBeInTheDocument());
    const summary = within(screen.getByRole('group', { name: 'Run Status (24h)' }));
    expect(summary.getByText('30')).toBeInTheDocument();
    expect(summary.getByText('Runs in the selected period')).toBeInTheDocument();
    expect(summary.getAllByRole('button')).toHaveLength(4);
    expect(summary.getByRole('button', { name: 'Succeeded 28 93.3%' })).toBeInTheDocument();
    expect(summary.getByRole('button', { name: 'Cancelled 0 0.0%' })).toBeInTheDocument();
    expect(summary.getByRole('button', { name: 'Running 0 0.0%' })).toBeInTheDocument();
    expect(summary.queryByText('Other')).not.toBeInTheDocument();
  });

  it('folds Pending/Paused/Skipped into an informational Other row', async () => {
    // total 50 but only 45 are succeeded+failed -> 5 land in "Other".
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24h: { total: 50, succeeded: 40, failed: 5, running: 0, cancelled: 0 },
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Run Status (24h)')).toBeInTheDocument());
    const summary = within(screen.getByRole('group', { name: 'Run Status (24h)' }));
    expect(summary.getByText('Other').closest('li')).toHaveTextContent('Other510.0%');
    expect(summary.queryByRole('button', { name: /Other/ })).not.toBeInTheDocument();
  });

  it.each(['en', 'de'])('formats the screenshot distribution and tiny positive shares in %s', async (language) => {
    await i18n.changeLanguage(language);
    try {
      server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
        ...BASE_STATS,
        last24h: { total: 69737, succeeded: 63835, failed: 5894, cancelled: 1, running: 7 },
      })));
      renderPage();
      const summary = within(await screen.findByRole('group', { name: /Run Status|Lauf-Status/ }));
      expect(summary.getByText(language === 'de' ? '69.737' : '69,737')).toBeInTheDocument();
      expect(summary.getByText(language === 'de' ? '91,5 %' : '91.5%')).toBeInTheDocument();
      expect(summary.getByText(language === 'de' ? '8,5 %' : '8.5%')).toBeInTheDocument();
      expect(summary.getAllByText(language === 'de' ? '< 0,1 %' : '< 0.1%')).toHaveLength(2);
    } finally {
      await i18n.changeLanguage('en');
    }
  });

  it('shows the existing empty state when the selected period has no runs', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24h: { total: 0, succeeded: 0, failed: 0, cancelled: 0, running: 0 },
    })));
    renderPage();
    const heading = await screen.findByText('Run Status (24h)');
    const card = within(heading.closest('div.np-card') as HTMLElement);
    expect(card.getByText('No executions yet')).toBeInTheDocument();
    expect(card.queryByRole('button')).not.toBeInTheDocument();
  });

  it('shows an independent empty state for execution durations', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    expect(await screen.findByText('Execution Duration (24h)')).toBeInTheDocument();
    expect(await screen.findByText('No completed runs in the selected period.')).toBeInTheDocument();
  });

  it('renders the duration chart from its own endpoint', async () => {
    const buckets = [
      { hourStart: new Date(Date.now() - 2 * 3600_000).toISOString(), succeeded: 10, failed: 0, cancelled: 0 },
      { hourStart: new Date(Date.now() - 1 * 3600_000).toISOString(), succeeded: 8, failed: 2, cancelled: 0 },
    ];
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({ ...BASE_STATS, last24hBuckets: buckets })));
    server.use(http.get(`${BASE}/api/stats/duration-trend`, () => HttpResponse.json({
      buckets: buckets.map(b => ({ startedAt: b.hourStart, count: 10, medianMs: 1000, p95Ms: 3000 })), workflows: [],
    })));
    renderPage();
    expect(await screen.findByRole('img', { name: 'Execution Duration (24h)' })).toBeInTheDocument();
    expect(screen.queryByText('No completed runs in the selected period.')).not.toBeInTheDocument();
  });

  it('preserves the duration workflow filter when the dashboard window changes', async () => {
    const requests: string[] = [];
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    server.use(http.get(`${BASE}/api/stats/duration-trend`, ({ request }) => {
      requests.push(request.url);
      return HttpResponse.json({ buckets: [], workflows: [{ id: 'wf-1', name: 'Disk Check' }] });
    }));
    renderPage();
    await screen.findByRole('option', { name: 'Disk Check' });
    const user = userEvent.setup();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Workflow for execution duration' }), 'wf-1');
    await waitFor(() => expect(requests.some(url => url.includes('windowHours=24&workflowId=wf-1'))).toBe(true));
    await user.click(screen.getByRole('button', { name: '7 days' }));
    await waitFor(() => expect(requests.some(url => url.includes('windowHours=168&workflowId=wf-1'))).toBe(true));
    expect(screen.getByRole('combobox', { name: 'Workflow for execution duration' })).toHaveValue('wf-1');
  });

  it('styles the duration workflow filter with the card input class, which every skin defines', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    const filter = await screen.findByRole('combobox', { name: 'Workflow for execution duration' });
    expect(filter).toHaveClass('input-field');
    expect(filter).not.toHaveClass('np-input');
  });

  it('retries a failed duration request without hiding other dashboard cards', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    server.use(http.get(`${BASE}/api/stats/duration-trend`, () => new HttpResponse(null, { status: 500 })));
    renderPage();
    expect(await screen.findByText('Could not load execution durations.')).toBeInTheDocument();
    expect(screen.getByText('Run Status (24h)')).toBeInTheDocument();
    server.use(http.get(`${BASE}/api/stats/duration-trend`, () => HttpResponse.json({ buckets: [], workflows: [] })));
    await userEvent.setup().click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('No completed runs in the selected period.')).toBeInTheDocument();
  });

  it('replaces the p95 chart with the independent failure-causes panel', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    expect(await screen.findByText('Most Common Errors (24h)')).toBeInTheDocument();
    expect(screen.queryByText('p95 · Top Workflows (7d)')).not.toBeInTheDocument();
    expect(await screen.findByText('No failed executions in the selected period.')).toBeInTheDocument();
  });

  it('keeps the dashboard available when failure causes cannot load', async () => {
    server.use(http.get(`${BASE}/api/stats/failure-causes`, () => HttpResponse.json({}, { status: 500 })));
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({ ...BASE_STATS, topWorkflows: [] })));
    renderPage();
    expect(await screen.findByText('Could not load failure causes.')).toBeInTheDocument();
    expect(screen.getByText('Workflows')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
});
