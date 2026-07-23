import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { OpsExecutionDrilldown } from '../../../components/operations/OpsExecutionDrilldown';

const BASE = 'http://localhost';
const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function patchFetch() {
  const orig = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) return orig(`${BASE}${input}`, init);
    return orig(input as RequestInfo, init);
  });
}

const DETAIL = {
  id: 'ex-1', workflowId: 'w1', status: 'Running',
  startedAt: new Date(NOW - 4 * MIN).toISOString(), completedAt: null,
  triggeredBy: 'schedule', errorMessage: null, traceId: null, spanId: null,
  returnData: null, inputParametersJson: null,
  parentExecutionId: null, parentWorkflowName: null,
};

const server = setupServer(
  http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json(DETAIL)),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'bypass' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function renderDrilldown(overrides: Partial<Parameters<typeof OpsExecutionDrilldown>[0]> = {}) {
  const onCancel = vi.fn();
  const onSelectExecution = vi.fn();
  const onOpenEditor = vi.fn();
  const onClose = vi.fn();
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <OpsExecutionDrilldown
        executionId="ex-1"
        workflowName="Nightly Backup"
        folderPath="/Prod"
        callees={[]}
        status="Running"
        startedAtMs={NOW - 4 * MIN}
        completedAtMs={null}
        nowMs={NOW}
        canCancel
        cancelPending={false}
        onCancel={onCancel}
        onOpenEditor={onOpenEditor}
        onSelectExecution={onSelectExecution}
        onClose={onClose}
        {...overrides}
      />
    </QueryClientProvider>,
  );
  return { onCancel, onSelectExecution, onOpenEditor, onClose };
}

describe('OpsExecutionDrilldown', () => {
  it('renders workflow context, live status badge and the fetched triggeredBy', async () => {
    renderDrilldown();
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('/Prod')).toBeInTheDocument();
    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('4:00')).toBeInTheDocument(); // live elapsed
    expect(await screen.findByText('schedule')).toBeInTheDocument();
  });

  it('cancel is offered for active runs with canCancel and forwards the id', () => {
    const { onCancel } = renderDrilldown();
    fireEvent.click(screen.getByRole('button', { name: /Cancel/ }));
    expect(onCancel).toHaveBeenCalledWith('ex-1');
  });

  it('shows the execution id with a copy button', () => {
    renderDrilldown();
    expect(screen.getByText('ex-1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^(Copy|Kopieren)$/ })).toBeInTheDocument();
  });

  it('cancel is hidden without the capability and for settled runs', () => {
    renderDrilldown({ canCancel: false });
    expect(screen.queryByRole('button', { name: /Cancel/ })).not.toBeInTheDocument();
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(screen.queryByRole('button', { name: /Cancel/ })).not.toBeInTheDocument();
  });

  it('renders the error message for failed runs', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Failed', completedAt: new Date(NOW - MIN).toISOString(), errorMessage: 'WinRM timeout on HOST-1',
    })));
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(await screen.findByText('WinRM timeout on HOST-1')).toBeInTheDocument();
  });

  it('shows a navigable parent chip for sub-workflow runs', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, parentExecutionId: 'parent-1', parentWorkflowName: 'Daily Report',
    })));
    const { onSelectExecution } = renderDrilldown();
    fireEvent.click(await screen.findByRole('button', { name: /Daily Report/ }));
    expect(onSelectExecution).toHaveBeenCalledWith('parent-1');
  });

  it('lists the static call topology (callees) when the workflow calls others', () => {
    renderDrilldown({ callees: ['Cleanup Temp', '{{manual.target}}'] });
    expect(screen.getByText('Calls')).toBeInTheDocument();
    expect(screen.getByText('Cleanup Temp')).toBeInTheDocument();
    expect(screen.getByText('{{manual.target}}')).toBeInTheDocument();
  });

  it('open-in-editor and close forward to their callbacks', () => {
    const { onOpenEditor, onClose } = renderDrilldown();
    fireEvent.click(screen.getByRole('button', { name: 'Open in editor' }));
    expect(onOpenEditor).toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalled();
  });
});
