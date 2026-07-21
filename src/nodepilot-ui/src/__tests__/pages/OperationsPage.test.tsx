import { describe, it, expect, beforeAll, beforeEach, afterAll, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { OperationsPage } from '../../pages/OperationsPage';
import { useAuthStore } from '../../stores/authStore';
import { useOperationsStore } from '../../stores/operationsStore';
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
    runningCount: 0, lastStatus: null, callFrequency: null, ...p,
  };
}

const GRAPH: OperationsGraph = {
  nodes: [
    node({ workflowId: 'wf-1', name: 'Nightly Backup', folderId: 'prod', folderPath: '/Prod', runningCount: 1, callFrequency: 5 }),
    node({ workflowId: 'wf-2', name: 'Report Gen', folderId: 'prod', folderPath: '/Prod', lastStatus: 'Failed', callFrequency: 3 }),
    node({ workflowId: 'wf-3', name: 'Health Check', folderId: 'staging', folderPath: '/Staging', lastStatus: 'Succeeded', callFrequency: 1 }),
  ],
  edges: [],
  running: [{ executionId: 'ex-1', workflowId: 'wf-1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null }],
  recent: [{ executionId: 'ex-2', workflowId: 'wf-2', status: 'Failed', startedAt: new Date(NOW - 10 * MIN).toISOString(), completedAt: new Date(NOW - 8 * MIN).toISOString(), parentExecutionId: null }],
  capabilities: { canCancel: true },
};

const STATS = {
  machinesTotal: 3, machinesReachable: 3,
  pendingCount: 0, runningCount: 1, longRunningCount: 0,
  clusterRole: null,
  healthHeartbeats: [{ serviceName: 'Scheduler', lastHeartbeatAt: new Date(NOW).toISOString(), expectedIntervalSeconds: 60, status: null, isStale: false }],
  armedTriggers: [
    { workflowId: 'wf-1', workflowName: 'Nightly Backup', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(NOW + 30 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null },
    { workflowId: 'wf-3', workflowName: 'Health Check', triggerTypes: ['scheduleTrigger'], nextFireUtc: new Date(NOW + 10 * MIN).toISOString(), nextFireKind: 'cron', pollIntervalSeconds: null },
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
beforeEach(() => useOperationsStore.getState().reset());
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
  it('renders pulse header, timeline bars and the departure board from both snapshots', async () => {
    renderPage();
    // Timeline: running bar for wf-1 + settled failed bar for wf-2.
    expect(await screen.findByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
    // Pulse header: recent failure (8 min ago) → degraded; 1 active.
    expect(screen.getByRole('status')).toHaveTextContent('Degraded');
    // Departure board sorted by fire time: Health Check (+10) before Nightly Backup (+30).
    const board = screen.getByRole('table');
    const rows = within(board).getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('Health Check');
    expect(rows[1]).toHaveTextContent('Nightly Backup');
    // Health rail machines fraction.
    expect(screen.getByText('3/3')).toBeInTheDocument();
  });

  it('opens the drilldown from a timeline bar and can cancel the running execution', async () => {
    let cancelHit = false;
    server.use(http.post(`${BASE}/api/executions/ex-1/cancel`, () => { cancelHit = true; return HttpResponse.json({}); }));

    renderPage();
    fireEvent.click(await screen.findByTitle(/Nightly Backup · Running/));

    expect(await screen.findByRole('button', { name: 'Open in editor' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Cancel/ }));
    await waitFor(() => expect(cancelHit).toBe(true));
  });

  it('shows the empty state when no accessible workflows', async () => {
    server.use(http.get(`${BASE}/api/operations/graph`, () =>
      HttpResponse.json({ nodes: [], edges: [], running: [], recent: [], capabilities: { canCancel: false } })));
    renderPage();
    expect(await screen.findByText('No accessible workflows.')).toBeInTheDocument();
  });

  it('shows the idle hero when workflows exist but nothing ran recently', async () => {
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      nodes: [node({ workflowId: 'wf-a', name: 'Alpha', folderId: 'f', folderPath: '/', lastStatus: 'Succeeded' })],
      edges: [], running: [], recent: [], capabilities: { canCancel: true },
    })));
    renderPage();
    expect(await screen.findByText('Nothing is running right now.')).toBeInTheDocument();
  });

  it('folder filter scopes timeline bars, ticker and departure board together', async () => {
    renderPage();
    await screen.findByTitle(/Nightly Backup · Running/);
    const select = screen.getByRole('combobox') as HTMLSelectElement;

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
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'prod' } });
    expect(select.value).toBe('prod');

    // Next snapshot no longer exposes the /Prod folder (RBAC / scope change).
    server.use(http.get(`${BASE}/api/operations/graph`, () => HttpResponse.json({
      nodes: [node({ workflowId: 'wf-3', name: 'Health Check', folderId: 'staging', folderPath: '/Staging', lastStatus: 'Succeeded' })],
      edges: [], running: [], recent: [], capabilities: { canCancel: true },
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
      edges: [], running: [], recent: [], capabilities: { canCancel: false },
    })));
    renderPage();
    const select = await screen.findByRole('combobox') as HTMLSelectElement;
    await waitFor(() => expect(select.options).toHaveLength(6));
    expect(select.options[0].textContent).toBe('All folders');
  });
});
