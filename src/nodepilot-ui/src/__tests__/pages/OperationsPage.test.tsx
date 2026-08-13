import { describe, it, expect, beforeAll, beforeEach, afterAll, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, within, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { OperationsPage } from '../../pages/OperationsPage';
import { useAuthStore } from '../../stores/authStore';
import { useOperationsStore } from '../../stores/operationsStore';
import { useConfirmStore } from '../../stores/confirmStore';
import { useToastStore } from '../../stores/toastStore';
import type { OperationsGraph, OpsNode } from '../../types/api';

const BASE = 'http://localhost';
const NOW = Date.now();
const MIN = 60_000;

vi.mock('../../hooks/useOperationsFeed', () => ({ useOperationsFeed: () => {} }));

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input as RequestInfo, init);
  });
}

function node(p: Partial<OpsNode>): OpsNode {
  return {
    workflowId: 'wf', name: 'WF', folderId: 'f', folderPath: '/', isEnabled: true,
    runningCount: 0, lastStatus: null, callFrequency: null,
    canRun: true, canEdit: true, ...p,
  };
}

const GRAPH: OperationsGraph = {
  nodes: [
    node({ workflowId: 'wf-1', name: 'Nightly Backup', folderId: 'prod', folderPath: '/Prod', runningCount: 1, callFrequency: 5 }),
    node({ workflowId: 'wf-2', name: 'Report Gen', folderId: 'prod', folderPath: '/Prod', lastStatus: 'Failed', callFrequency: 3 }),
    node({ workflowId: 'wf-3', name: 'Health Check', folderId: 'staging', folderPath: '/Staging', lastStatus: 'Succeeded', callFrequency: 1 }),
  ],
  edges: [],
  running: [{ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
  meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
  recent: [{ executionId: 'ex-2', workflowId: 'wf-2', status: 'Failed', startedAt: new Date(NOW - 10 * MIN).toISOString(), completedAt: new Date(NOW - 8 * MIN).toISOString(), parentExecutionId: null }],
  density: [],
};

const STATS = {
  machinesTotal: 3, machinesReachable: 3,
  pendingCount: 0, runningCount: 1, longRunningCount: 0,
  clusterRole: null,
  healthHeartbeats: [{ serviceName: 'Scheduler', lastHeartbeatAt: new Date(NOW).toISOString(), expectedIntervalSeconds: 60, status: null, isStale: false }],
  armedTriggers: [
    { workflowId: 'wf-1', workflowName: 'Nightly Backup', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(NOW + 30 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null, blockedByWindowName: null },
    { workflowId: 'wf-3', workflowName: 'Health Check', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(NOW + 10 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null, blockedByWindowName: null },
  ],
};

const server = setupServer(
  http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json(GRAPH)),
  http.get(`${BASE}/api/stats/dashboard`, () => HttpResponse.json(STATS)),
  http.get(`${BASE}/api/executions/:id`, ({ params }) => HttpResponse.json({
    id: params.id, workflowId: 'wf-1', status: 'Running',
    startedAt: new Date(NOW - 4 * MIN).toISOString(), completedAt: null,
    triggeredBy: 'manual', errorMessage: null, traceId: null, spanId: null,
    returnData: null, inputParametersJson: null,
  })),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
beforeEach(() => {
  useOperationsStore.getState().reset();
  useConfirmStore.setState({ pending: null });
  useToastStore.setState({ toasts: [] });
});
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderPage() {
  useAuthStore.setState({ isAuthenticated: true, username: 'admin', role: 'Admin' });
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  patchFetch();
  const result = render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <OperationsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { ...result, qc };
}

describe('OperationsPage', () => {
  it('renders timeline bars and the departure board from both snapshots', async () => {
    renderPage();
    // Timeline: running bar for wf-1 + settled failed bar for wf-2.
    expect(await screen.findByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
    // Departure board sorted by fire time: Health Check (+10) before Nightly Backup (+30).
    const board = screen.getByRole('table');
    const rows = within(board).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('Health Check');
    expect(rows[1]).toHaveTextContent('Nightly Backup');
  });

  it('opens the drilldown from a timeline bar and can cancel the running execution', async () => {
    let cancelHit = false;
    server.use(http.post(`${BASE}/api/executions/ex-1/cancel`, () => { cancelHit = true; return HttpResponse.json({}); }));

    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));

    expect(await screen.findByRole('button', { name: 'Open in editor' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(cancelHit).toBe(true));
  });

  // ---- Incident actions --------------------------------------------------------------------

  it('hides the action buttons when the node carries no folder rights', async () => {
    // Regression for the bug this PR fixes: capability used to come from the GLOBAL role, so a
    // global Operator with folder-Viewer rights was offered buttons that then 403'd.
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      ...GRAPH,
      nodes: GRAPH.nodes.map((n) => ({ ...n, canRun: false, canEdit: false })),
    })));
    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));

    expect(await screen.findByRole('button', { name: 'Open in editor' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Cancel all runs/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Quarantine' })).not.toBeInTheDocument();
  });

  it('cancel-all runs only after the confirm dialog is accepted', async () => {
    let hits = 0;
    server.use(http.post(`${BASE}/api/workflows/wf-1/cancel-all`, () => {
      hits++; return HttpResponse.json({ total: 2, signalled: 1 });
    }));
    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));
    fireEvent.click(await screen.findByRole('button', { name: /Cancel all runs/ }));

    // Declining must not fire the request.
    useConfirmStore.getState().settle(false);
    await waitFor(() => expect(useConfirmStore.getState().pending).toBeNull());
    expect(hits).toBe(0);

    fireEvent.click(screen.getByRole('button', { name: /Cancel all runs/ }));
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(true);
    await waitFor(() => expect(hits).toBe(1));
  });

  it('quarantine disables BEFORE cancel-all, so re-armed triggers cannot restart the runs', async () => {
    const order: string[] = [];
    server.use(
      http.post(`${BASE}/api/workflows/wf-1/disable`, () => { order.push('disable'); return new HttpResponse(null, { status: 204 }); }),
      http.post(`${BASE}/api/workflows/wf-1/cancel-all`, () => { order.push('cancel-all'); return HttpResponse.json({ total: 2, signalled: 2 }); }),
    );
    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));
    fireEvent.click(await screen.findByRole('button', { name: 'Quarantine' }));
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(true);

    await waitFor(() => expect(order).toEqual(['disable', 'cancel-all']));
    // total (2), not signalled — force-cancelled zombies count too.
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((t) => t.message.includes('2')),
    ).toBe(true));
  });

  it('reports the partial state when disable succeeded but cancel-all failed', async () => {
    server.use(
      http.post(`${BASE}/api/workflows/wf-1/disable`, () => new HttpResponse(null, { status: 204 })),
      http.post(`${BASE}/api/workflows/wf-1/cancel-all`, () => new HttpResponse(null, { status: 500 })),
    );
    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));
    fireEvent.click(await screen.findByRole('button', { name: 'Quarantine' }));
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(true);

    // Neither "done" nor a generic failure — the workflow IS off, only the runs survived.
    await waitFor(() => expect(
      useToastStore.getState().toasts.some((t) => t.kind === 'error' && t.message.includes('Cancel all runs')),
    ).toBe(true));
  });

  it('quarantine refreshes the departure board so it stops promising a suppressed start', async () => {
    server.use(
      http.post(`${BASE}/api/workflows/wf-1/disable`, () => new HttpResponse(null, { status: 204 })),
      http.post(`${BASE}/api/workflows/wf-1/cancel-all`, () => HttpResponse.json({ total: 0, signalled: 0 })),
    );
    const { qc } = renderPage();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));
    fireEvent.click(await screen.findByRole('button', { name: 'Quarantine' }));
    await waitFor(() => expect(useConfirmStore.getState().pending).not.toBeNull());
    useConfirmStore.getState().settle(true);

    // armedTriggers filters on IsEnabled server-side, so the board is stale until this fires.
    await waitFor(() => expect(spy).toHaveBeenCalledWith({ queryKey: ['ops-dashboard'] }));
  });

  // ---- Window selector + display freeze ------------------------------------------------------

  it('requests the selected window from the server', async () => {
    const urls: string[] = [];
    server.use(http.get(`${BASE}/api/operations/graph`, ({ request }) => {
      urls.push(request.url);
      return HttpResponse.json(GRAPH);
    }));
    renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);
    expect(urls.at(-1)).toContain('windowMinutes=30');

    fireEvent.change(screen.getByLabelText('Window'), { target: { value: '60' } });
    await waitFor(() => expect(urls.at(-1)).toContain('windowMinutes=60'));
  });

  it('freeze stops the polling and shows a badge naming the freeze time', async () => {
    let hits = 0;
    server.use(http.get(`${BASE}/api/operations/graph`, () => { hits++; return HttpResponse.json(GRAPH); }));
    renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);

    fireEvent.click(screen.getByRole('button', { name: 'Freeze view' }));
    expect(await screen.findByTestId('ops-frozen-badge')).toBeInTheDocument();

    const atFreeze = hits;
    await new Promise((r) => setTimeout(r, 120));
    expect(hits).toBe(atFreeze);

    // Unfreezing restores the live label.
    fireEvent.click(screen.getByRole('button', { name: 'Go live' }));
    await waitFor(() => expect(screen.queryByTestId('ops-frozen-badge')).not.toBeInTheDocument());
  });

  it('freeze holds the VIEW but never the store — tombstones keep being written', async () => {
    // The invariant that keeps unfreezing safe: if the feed stopped writing terminal
    // tombstones, a refetch afterwards could resurrect a run that finished during the freeze.
    const { qc } = renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);
    fireEvent.click(screen.getByRole('button', { name: 'Freeze view' }));

    act(() => { useOperationsStore.getState().applyStatus('ex-1', 'wf-1', 'Failed'); });

    // Rendered bar unchanged…
    expect(screen.getByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
    // …but the store recorded the terminal event, so a later snapshot cannot resurrect it.
    expect(useOperationsStore.getState().terminalTombstones['ex-1']).toBeDefined();

    // The run has genuinely settled by the time we go live again.
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      ...GRAPH,
      running: [],
      recent: [{
        executionId: 'ex-1', workflowId: 'wf-1', status: 'Failed',
        startedAt: new Date(NOW - 4 * MIN).toISOString(),
        completedAt: new Date(NOW - 1 * MIN).toISOString(),
        parentExecutionId: null,
      }],
    })));

    fireEvent.click(screen.getByRole('button', { name: 'Go live' }));
    await qc.invalidateQueries({ queryKey: ['operations-graph'] });

    // View follows live data again.
    await waitFor(() => expect(screen.getByTitle(/Nightly Backup · Failed/)).toBeInTheDocument());
  });

  it('shows the empty state when no accessible workflows', async () => {
    server.use(http.get(`${BASE}/api/operations/graph`, () =>
      HttpResponse.json({ nodes: [], edges: [], running: [], recent: [], density: [], meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false } })));
    renderPage();
    expect(await screen.findByText('No accessible workflows.')).toBeInTheDocument();
  });

  it('shows the idle hero when workflows exist but nothing ran recently', async () => {
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      nodes: [node({ workflowId: 'wf-a', name: 'Alpha', folderId: 'f', folderPath: '/', lastStatus: 'Succeeded' })],
      edges: [], running: [], recent: [], density: [], meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
    })));
    renderPage();
    expect(await screen.findByText('Nothing is running right now.')).toBeInTheDocument();
  });

  it('folder filter scopes timeline bars and departure board together', async () => {
    renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);
    const select = screen.getByLabelText('Folder') as HTMLSelectElement;

    // Scope to /Staging: no bars (idle), board only Health Check.
    fireEvent.change(select, { target: { value: 'staging' } });
    await waitFor(() => expect(screen.queryByTitle(/Nightly Backup · Running/)).not.toBeInTheDocument());
    expect(screen.getByText('Nothing is running right now.')).toBeInTheDocument();
    const board = screen.getByRole('table');
    expect(within(board).queryByText('Nightly Backup')).not.toBeInTheDocument();
    expect(within(board).getByText('Health Check')).toBeInTheDocument();

    // Back to all folders: bars return.
    fireEvent.change(select, { target: { value: '' } });
    expect(await screen.findByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
  });

  it('resets the folder filter to All when the chosen folder vanishes from the snapshot', async () => {
    const { qc } = renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);
    const select = screen.getByLabelText('Folder') as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'prod' } });
    expect(select.value).toBe('prod');

    // Next snapshot no longer exposes the /Prod folder (RBAC / scope change).
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      nodes: [node({ workflowId: 'wf-3', name: 'Health Check', folderId: 'staging', folderPath: '/Staging', lastStatus: 'Succeeded' })],
      edges: [], running: [], recent: [], density: [], meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
    })));
    await qc.invalidateQueries({ queryKey: ['operations-graph'] });

    await waitFor(() => expect(select.value).toBe(''));
  });

  it('renders a select option per folder plus the All-folders default', async () => {
    const folders = ['f1', 'f2', 'f3', 'f4', 'f5'];
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      nodes: folders.map((folderId, i) => node({
        workflowId: `wf-${i}`, name: `WF ${i}`, folderId, folderPath: `/${folderId}`, lastStatus: 'Succeeded',
      })),
      edges: [], running: [], recent: [], density: [], meta: { overdueSeconds: 600, windowMinutes: 30, recentSinceUtc: new Date(0).toISOString(), oldestReturnedCompletedAt: null, recentTruncated: false, densityBucketSeconds: 0, densityCapped: false },
    })));
    renderPage();
    const select = await screen.findByLabelText('Folder') as HTMLSelectElement;
    await waitFor(() => expect(select.options).toHaveLength(6));
    expect(select.options[0].textContent).toBe('All folders');
  });
});
