import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { DashboardPage } from '../../pages/DashboardPage';
import { useAuthStore } from '../../stores/authStore';

// The dashboard now subscribes to the SignalR ops-feed for live updates. Mock the hook
// to a no-op (mirrors OperationsPage.test) so no real WebSocket is opened under jsdom.
vi.mock('../../hooks/useDashboardFeed', () => ({ useDashboardFeed: () => {} }));

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

  it('renders the Runs (24h) KPI without the inline sparkline graphic', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24hBuckets: [
        { hourStart: new Date(Date.now() - 2 * 3600_000).toISOString(), succeeded: 5, failed: 1, cancelled: 0 },
        { hourStart: new Date(Date.now() - 1 * 3600_000).toISOString(), succeeded: 7, failed: 0, cancelled: 0 },
      ],
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Runs (24h)')).toBeInTheDocument());
    const card = screen.getByText('Runs (24h)').closest('div.np-card') as HTMLElement;
    // The removed sparkline was the only 72px-wide inline svg; the Carbon label icon is 12px.
    expect(card.querySelector('svg[width="72"]')).toBeNull();
    // Total renders in full now that nothing competes for the row.
    expect(card.textContent).toContain('30');
  });

  it('renders top workflows panel with avg/p95 duration', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Top Workflows (7 days)')).toBeInTheDocument());
    expect(screen.getByText('10 runs')).toBeInTheDocument();
    expect(screen.getByText(/avg/i)).toBeInTheDocument();
    // The new "p95 · Top Workflows" chart title also matches /p95/i, so assert ≥1 instead of unique.
    expect(screen.getAllByText(/p95/i).length).toBeGreaterThanOrEqual(1);
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

    // Click a KPI label that lives outside the new-workflow control → the input must close.
    await userEvent.click(screen.getByText('Workflows'));
    expect(screen.queryByPlaceholderText(/Name of the new workflow/i)).not.toBeInTheDocument();
  });

  it('filters the recent executions table by status when the donut segment is clicked', async () => {
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
    await waitFor(() => expect(screen.getByText(/0p · 3r/)).toBeInTheDocument());
    expect(screen.getByText(/1 long/i)).toBeInTheDocument();
  });

  it('renders cluster role badge when HA is configured', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      clusterRole: 'leader',
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Leader')).toBeInTheDocument());
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

  // ── Insight charts (run-status donut · success-rate trend · p95 bars) ──

  it('renders the run-status donut with centre total and legend', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Run Status (24h)')).toBeInTheDocument());
    // Centre overlay label is the only plain "runs" text node on the page.
    expect(screen.getByText('runs')).toBeInTheDocument();
  });

  it('folds Pending/Paused/Skipped into an "Other" donut segment', async () => {
    // total 50 but only 45 are succeeded+failed → 5 land in "Other".
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({
      ...BASE_STATS,
      last24h: { total: 50, succeeded: 40, failed: 5, running: 0, cancelled: 0 },
    })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Run Status (24h)')).toBeInTheDocument());
    expect(screen.getByText('Other 5')).toBeInTheDocument();
  });

  it('shows empty state for the success-rate trend when there are no buckets', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('Success Rate Trend (24h)')).toBeInTheDocument());
    // Both the 24h area chart and the trend fall back to the empty state with no buckets.
    expect(screen.getAllByText('No executions yet').length).toBeGreaterThanOrEqual(2);
  });

  it('renders the success-rate trend chart when hourly buckets exist', async () => {
    const buckets = [
      { hourStart: new Date(Date.now() - 2 * 3600_000).toISOString(), succeeded: 10, failed: 0, cancelled: 0 },
      { hourStart: new Date(Date.now() - 1 * 3600_000).toISOString(), succeeded: 8, failed: 2, cancelled: 0 },
    ];
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({ ...BASE_STATS, last24hBuckets: buckets })));
    renderPage();
    await waitFor(() => expect(screen.getByText('Success Rate Trend (24h)')).toBeInTheDocument());
    // With data present, the trend no longer shows the empty state (only the running panel is empty here).
    expect(screen.queryByText('No executions yet')).not.toBeInTheDocument();
  });

  it('renders the p95 top-workflows chart when duration data exists', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(BASE_STATS)));
    renderPage();
    await waitFor(() => expect(screen.getByText('p95 · Top Workflows (7d)')).toBeInTheDocument());
    expect(screen.queryByText('No duration data yet')).not.toBeInTheDocument();
  });

  it('shows empty state for p95 chart when no workflow has duration data', async () => {
    server.use(http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json({ ...BASE_STATS, topWorkflows: [] })));
    renderPage();
    await waitFor(() => expect(screen.getByText('p95 · Top Workflows (7d)')).toBeInTheDocument());
    expect(screen.getByText('No duration data yet')).toBeInTheDocument();
  });
});
