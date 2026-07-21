import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import { PerformanceSection } from '../../../components/admin-settings/PerformanceSection';

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

const engine = {
  sectionPath: 'Engine',
  payload: {
    debug: { maxPauseMinutes: 10 },
    maxConcurrentExecutions: { global: 5000, perUser: 2000 },
    maxConcurrentSteps: 600,
    runspace: { minRunspaces: 256, maxRunspaces: 768 },
  },
  etag: '"e-1"', isHotReloadable: false, effectiveSource: {},
};
const dispatch = {
  sectionPath: 'ExecutionDispatch',
  payload: { capacity: 2048, workerCount: 600 },
  etag: '"d-1"', isHotReloadable: false, effectiveSource: {},
};
const threading = {
  sectionPath: 'Threading',
  payload: { minWorkerThreads: 768, minIoCompletionThreads: 768 },
  etag: '"t-1"', isHotReloadable: true, effectiveSource: {},
};
const remote = {
  sectionPath: 'Remote',
  payload: {
    requireWinRmSsl: true,
    winRm: { operationTimeoutSeconds: 300, openTimeoutSeconds: 30 },
    pool: { enabled: true, maxConcurrentPerMachine: 5, maxIdlePerKey: 5, idleTtlSeconds: 120 },
  },
  etag: '"r-1"', isHotReloadable: false, effectiveSource: {},
};

function renderAll() {
  server.use(
    http.get('/api/admin/settings/Engine', () => HttpResponse.json(engine)),
    http.get('/api/admin/settings/ExecutionDispatch', () => HttpResponse.json(dispatch)),
    http.get('/api/admin/settings/Threading', () => HttpResponse.json(threading)),
    http.get('/api/admin/settings/Remote', () => HttpResponse.json(remote)),
  );
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><PerformanceSection /></QueryClientProvider>);
}

describe('PerformanceSection', () => {
  it('renders Engine + Dispatch + Threading + Remote cards', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('5000')).toBeInTheDocument());
    expect(screen.getByDisplayValue('2048')).toBeInTheDocument();
    expect(screen.getByDisplayValue('300')).toBeInTheDocument();
    // Two NumberInputs with 768 (Threading) + one (Runspace MaxRunspaces) — at least two.
    expect(screen.getAllByDisplayValue('768').length).toBeGreaterThanOrEqual(2);
  });

  it('shows the hot-reload hint only on the Threading card', async () => {
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('5000')).toBeInTheDocument());
    // Engine / ExecutionDispatch / Remote need a service restart to take effect → only Threading carries the hint.
    expect(screen.getAllByText(/Changes apply immediately/i).length).toBe(1);
  });

  it('Remote save serializes nested Pool + WinRm payload in PascalCase', async () => {
    let putBody: unknown = null;
    server.use(http.put('/api/admin/settings/Remote', async ({ request }) => {
      putBody = await request.json();
      return HttpResponse.json({ ...remote, etag: '"r-2"' });
    }));
    renderAll();
    await waitFor(() => expect(screen.getByDisplayValue('5000')).toBeInTheDocument());
    const saves = screen.getAllByRole('button', { name: /speichern|save/i });
    // Last save button = Remote card.
    fireEvent.click(saves[saves.length - 1]);
    await waitFor(() => {
       
      const body = putBody as any;
      expect(body?.RequireWinRmSsl).toBe(true);
      expect(body?.WinRm?.OperationTimeoutSeconds).toBe(300);
      expect(body?.Pool?.MaxConcurrentPerMachine).toBe(5);
    });
  });
});
