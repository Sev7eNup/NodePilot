import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
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
  const onRetry = vi.fn();
  const onCancelAll = vi.fn();
  const onQuarantine = vi.fn();
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
        canRun
        canEdit
        workflowEnabled
        runningCount={1}
        activity={null}
        pendingAction={null}
        onCancel={onCancel}
        onRetry={onRetry}
        onCancelAll={onCancelAll}
        onQuarantine={onQuarantine}
        onOpenEditor={onOpenEditor}
        onSelectExecution={onSelectExecution}
        onClose={onClose}
        {...overrides}
      />
    </QueryClientProvider>,
  );
  return { onCancel, onRetry, onCancelAll, onQuarantine, onSelectExecution, onOpenEditor, onClose };
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

  it('cancel is offered for active runs with canRun and forwards the id', () => {
    const { onCancel } = renderDrilldown();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCancel).toHaveBeenCalledWith('ex-1');
  });

  it('shows the execution id with a copy button', () => {
    renderDrilldown();
    expect(screen.getByText('ex-1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^(Copy|Kopieren)$/ })).toBeInTheDocument();
  });

  it('cancel is hidden without folder-Run rights and for settled runs', () => {
    renderDrilldown({ canRun: false });
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN, runningCount: 0 });
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
  });

  // ---- Incident actions ------------------------------------------------------------------

  it('offers retry only on the terminal states the endpoint actually accepts', () => {
    for (const status of ['Succeeded', 'Failed', 'Cancelled']) {
      const { unmount } = render(<div />);
      unmount();
      renderDrilldown({ status, completedAtMs: NOW - MIN, runningCount: 0 });
      expect(screen.getAllByRole('button', { name: 'Retry run' }).length).toBeGreaterThan(0);
      cleanup();
    }
  });

  it('hides retry for TimedOut — the endpoint rejects that state', () => {
    renderDrilldown({ status: 'TimedOut', completedAtMs: NOW - MIN, runningCount: 0 });
    expect(screen.queryByRole('button', { name: 'Retry run' })).not.toBeInTheDocument();
  });

  it('disables retry on a disabled workflow and explains why', () => {
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN, workflowEnabled: false, runningCount: 0 });
    const retry = screen.getByRole('button', { name: 'Retry run' });
    expect(retry).toBeDisabled();
    expect(retry).toHaveAttribute('title', 'The workflow is disabled — re-enable it to retry.');
  });

  it('offers cancel-all only while runs are in flight, with the count', () => {
    const { onCancelAll } = renderDrilldown({ runningCount: 3 });
    const button = screen.getByRole('button', { name: 'Cancel all runs (3)' });
    fireEvent.click(button);
    expect(onCancelAll).toHaveBeenCalled();

    cleanup();
    renderDrilldown({ runningCount: 0, status: 'Failed', completedAtMs: NOW - MIN });
    expect(screen.queryByRole('button', { name: /Cancel all runs/ })).not.toBeInTheDocument();
  });

  it('gates quarantine on folder-Edit, not on folder-Run', () => {
    // The distinction that matters: disable needs Edit, cancel needs only Run.
    const { onQuarantine } = renderDrilldown({ canRun: true, canEdit: true });
    fireEvent.click(screen.getByRole('button', { name: 'Quarantine' }));
    expect(onQuarantine).toHaveBeenCalled();

    cleanup();
    renderDrilldown({ canRun: true, canEdit: false });
    expect(screen.queryByRole('button', { name: 'Quarantine' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });

  it('hides quarantine when the workflow is already off and idle', () => {
    renderDrilldown({ workflowEnabled: false, runningCount: 0, status: 'Failed', completedAtMs: NOW - MIN });
    expect(screen.queryByRole('button', { name: 'Quarantine' })).not.toBeInTheDocument();
  });

  it('disables every action while one is in flight', () => {
    renderDrilldown({ pendingAction: 'quarantine', runningCount: 2 });
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Cancel all runs/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Quarantine' })).toBeDisabled();
  });

  // ---- Step activity (no percentage, on purpose) -------------------------------------------

  it('shows finished-step count and last progress for a live run', async () => {
    renderDrilldown({
      activity: { stepsFinished: 4, lastCompletedStepName: 'Copy files', lastProgressAtMs: NOW - 11 * MIN },
    });
    expect(await screen.findByText('Steps finished')).toBeInTheDocument();
    expect(screen.getByText('4')).toBeInTheDocument();
    expect(screen.getByText('Last progress')).toBeInTheDocument();
    expect(screen.getByText('11:00 ago · Copy files')).toBeInTheDocument();
  });

  it('never renders a progress percentage — no honest total exists mid-run', () => {
    renderDrilldown({
      activity: { stepsFinished: 4, lastCompletedStepName: 'Copy files', lastProgressAtMs: NOW - MIN },
    });
    expect(screen.queryByText(/%/)).not.toBeInTheDocument();
    expect(screen.queryByText(/4\s*\/\s*\d+/)).not.toBeInTheDocument();
  });

  it('renders nothing for a run that was not enriched (null is unknown, not zero)', async () => {
    renderDrilldown({
      activity: { stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null },
    });
    expect(await screen.findByText('schedule')).toBeInTheDocument(); // detail loaded
    expect(screen.queryByText('Steps finished')).not.toBeInTheDocument();
    expect(screen.queryByText('Last progress')).not.toBeInTheDocument();
  });

  it('shows completed/total only once the run is terminal', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Succeeded', completedAt: new Date(NOW - MIN).toISOString(),
      stepsTotal: 9, stepsCompleted: 8,
    })));
    renderDrilldown({ status: 'Succeeded', completedAtMs: NOW - MIN, runningCount: 0 });
    expect(await screen.findByText('8 / 9')).toBeInTheDocument();
    expect(screen.queryByText('Steps finished')).not.toBeInTheDocument();
  });

  it('renders the error message for failed runs', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Failed', completedAt: new Date(NOW - MIN).toISOString(), errorMessage: 'WinRM timeout on HOST-1',
    })));
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(await screen.findByText('WinRM timeout on HOST-1')).toBeInTheDocument();
  });

  it('names the failed steps, not just the error message', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Failed', completedAt: new Date(NOW - MIN).toISOString(),
      errorMessage: 'Step failed',
      failedSteps: [{ stepId: 'step-7', stepName: 'Check Disk' }],
    })));
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(await screen.findByText('Failed steps')).toBeInTheDocument();
    expect(screen.getByText('Check Disk')).toBeInTheDocument();
  });

  it('falls back to the step id when a failed step has no label', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Failed', completedAt: new Date(NOW - MIN).toISOString(),
      failedSteps: [{ stepId: 'step-42', stepName: null }],
    })));
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(await screen.findByText('step-42')).toBeInTheDocument();
  });

  it('renders every failed step when parallel branches fail together', async () => {
    server.use(http.get(`${BASE}/api/executions/ex-1`, () => HttpResponse.json({
      ...DETAIL, status: 'Failed', completedAt: new Date(NOW - MIN).toISOString(),
      failedSteps: [
        { stepId: 'step-3', stepName: 'Branch A' },
        { stepId: 'step-4', stepName: 'Branch B' },
      ],
    })));
    renderDrilldown({ status: 'Failed', completedAtMs: NOW - MIN });
    expect(await screen.findByText('Branch A')).toBeInTheDocument();
    expect(screen.getByText('Branch B')).toBeInTheDocument();
  });

  it('omits the failed-steps section when there are none', async () => {
    renderDrilldown();
    expect(await screen.findByText('schedule')).toBeInTheDocument(); // detail loaded
    expect(screen.queryByText('Failed steps')).not.toBeInTheDocument();
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
