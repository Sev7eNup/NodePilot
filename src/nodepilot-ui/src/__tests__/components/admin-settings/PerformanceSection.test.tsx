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

const sizingValues = [
  { key: 'Engine:Runspace:MinRunspaces', value: 4, bound: 'Cpu' },
  { key: 'Engine:Runspace:MaxRunspaces', value: 32, bound: 'Cpu' },
  { key: 'Engine:MaxConcurrentSteps', value: 256, bound: 'Cpu' },
  { key: 'Threading:MinWorkerThreads', value: 200, bound: 'Cpu' },
  { key: 'Threading:MinIoCompletionThreads', value: 200, bound: 'Cpu' },
  { key: 'ExecutionDispatch:WorkerCount', value: 24, bound: 'Cpu' },
  { key: 'ExecutionDispatch:Capacity', value: 192, bound: 'Cpu' },
  { key: 'Engine:MaxConcurrentExecutions:Global', value: 240, bound: 'Cpu' },
  { key: 'Engine:MaxConcurrentExecutions:PerUser', value: 96, bound: 'Cpu' },
];

/**
 * Defaults to manual tuning so the pre-existing assertions keep exercising editable fields;
 * the mode-specific behaviour is covered by the dedicated cases further down.
 */
function renderAll(sizing: Partial<{
  manualTuning: boolean; desiredManualTuning: boolean; usableMemoryBytes: number | null;
}> = {}) {
  const manualTuning = sizing.manualTuning ?? true;
  // The Performance section stores the DESIRED mode — that is what the checkbox shows, and it is
  // `desiredManualTuning` in the sizing plan, not the mode the process booted in.
  const desiredManualTuning = sizing.desiredManualTuning ?? manualTuning;
  server.use(
    http.get('/api/admin/settings/Performance', () => HttpResponse.json({
      sectionPath: 'Performance', payload: { manualTuning: desiredManualTuning },
      etag: '"p-1"', isHotReloadable: false, effectiveSource: {},
    })),
    http.get('/api/admin/settings/effective-sizing', () => HttpResponse.json({
      manualTuning,
      desiredManualTuning,
      processorCount: 8,
      usableMemoryBytes: sizing.usableMemoryBytes === undefined ? 16 * 1024 ** 3 : sizing.usableMemoryBytes,
      isDesktop: false,
      values: sizingValues,
    })),
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

  it('greys out the plan-governed fields while automatic sizing is active', async () => {
    // The stored numbers stay visible, but under automatic sizing they are not what the process
    // runs on — letting an operator edit them would imply an effect they do not have.
    renderAll({ manualTuning: false });
    // 600 is both MaxConcurrentSteps and the dispatch WorkerCount — the plan governs both.
    await waitFor(() => expect(screen.getAllByDisplayValue('600')).toHaveLength(2));
    for (const field of screen.getAllByDisplayValue('600')) expect(field).toBeDisabled();
    expect(screen.getByDisplayValue('2048')).toBeDisabled(); // dispatch queue capacity
    expect(screen.getByDisplayValue('256')).toBeDisabled();  // MinRunspaces
  });

  it('keeps the fields the plan does not govern editable under automatic sizing', async () => {
    renderAll({ manualTuning: false });
    // MaxConcurrentExecutions is a safety cap against trigger loops, not a throughput knob —
    // overriding a deliberately configured guard would disarm it.
    await waitFor(() => expect(screen.getByDisplayValue('5000')).not.toBeDisabled());
    expect(screen.getByDisplayValue('2000')).not.toBeDisabled();
    // The debug pause is not part of sizing either.
    expect(screen.getByDisplayValue('10')).not.toBeDisabled();
  });

  it('shows the value actually in force, not the stored one', async () => {
    renderAll({ manualTuning: false });
    // 32 is hardware-derived; the section still stores 768.
    await waitFor(() => expect(screen.getByText(/Aktiv: 32|Active: 32/)).toBeInTheDocument());
    // The detected hardware is stated explicitly, so the derived numbers can be sanity-checked.
    expect(screen.getByText(/Erkannt: 8 Kerne|Detected: 8 cores/)).toBeInTheDocument();
  });

  it('keeps the fields editable under manual tuning', async () => {
    renderAll({ manualTuning: true });
    await waitFor(() => expect(screen.getByDisplayValue('5000')).not.toBeDisabled());
    expect(screen.getByDisplayValue('2048')).not.toBeDisabled();
  });

  it('flags a saved mode that has not taken effect yet', async () => {
    // Saved manual while the process still runs the automatic plan: runspace pool and dispatch
    // queue are sized once at boot, so this needs a restart rather than a config reload.
    renderAll({ manualTuning: false, desiredManualTuning: true });
    await waitFor(() => expect(
      screen.getByText(/gespeicherte Modus weicht|saved mode differs/i),
    ).toBeInTheDocument());
    // …and the cards below say the same thing in their own terms instead of claiming automatic
    // sizing is what the operator asked for.
    expect(screen.getAllByText(/Manuelles Tuning ist gewählt|Manual tuning is selected/i).length).toBe(3);
    expect(screen.queryByText(/Automatische Dimensionierung ist aktiv|Automatic sizing is active/i)).not.toBeInTheDocument();
  });

  it('flips the cards the moment the mode checkbox is clicked', async () => {
    // The whole point of the checkbox is that it changes what the cards below are: leaving them
    // greyed out and labelled "automatic sizing is active" until a restart made the switch
    // unusable — the values a restart would pick up could not even be typed.
    renderAll({ manualTuning: false });
    await waitFor(() => expect(screen.getByDisplayValue('256')).toBeDisabled());
    expect(screen.getAllByText(/Automatische Dimensionierung ist aktiv|Automatic sizing is active/i).length).toBe(3);

    fireEvent.click(screen.getByRole('checkbox', { name: /Manuelles Tuning|Manual tuning/i }));

    await waitFor(() => expect(screen.getByDisplayValue('256')).not.toBeDisabled());
    expect(screen.getAllByText(/Manuelles Tuning ist gewählt|Manual tuning is selected/i).length).toBe(3);
    expect(screen.queryByText(/Automatische Dimensionierung ist aktiv|Automatic sizing is active/i)).not.toBeInTheDocument();
  });

  it('says the configured values still govern when automatic sizing is only chosen', async () => {
    renderAll({ manualTuning: true });
    await waitFor(() => expect(screen.getByDisplayValue('256')).not.toBeDisabled());

    fireEvent.click(screen.getByRole('checkbox', { name: /Manuelles Tuning|Manual tuning/i }));

    await waitFor(() => expect(
      screen.getAllByText(/Automatische Dimensionierung ist gewählt|Automatic sizing is selected/i).length,
    ).toBe(3));
    // Still booted manual → the stored numbers are what the process runs on, so they stay editable.
    expect(screen.getByDisplayValue('256')).not.toBeDisabled();
  });

  it('keeps the hot-reload hint tied to the booted mode, not to the checkbox', async () => {
    // ThreadPoolTuningService follows the boot plan; ticking the box does not make a Threading
    // save apply live, so promising it would be a lie.
    renderAll({ manualTuning: false });
    await waitFor(() => expect(screen.getByDisplayValue('256')).toBeDisabled());
    expect(screen.queryByText(/Changes apply immediately|sofort/i)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('checkbox', { name: /Manuelles Tuning|Manual tuning/i }));

    await waitFor(() => expect(screen.getByDisplayValue('256')).not.toBeDisabled());
    expect(screen.queryByText(/Changes apply immediately|sofort/i)).not.toBeInTheDocument();
  });

  it('reports unknown memory rather than implying a detected size', async () => {
    renderAll({ manualTuning: false, usableMemoryBytes: null });
    await waitFor(() => expect(screen.getByText(/unbekannt|unknown/)).toBeInTheDocument());
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
