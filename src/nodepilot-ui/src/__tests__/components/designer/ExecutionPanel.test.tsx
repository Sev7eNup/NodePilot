import { describe, it, expect, vi, beforeAll, beforeEach, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { ExecutionPanel, type SimulationSnapshot } from '../../../components/designer/ExecutionPanel';
import type { LiveExecution, StepUpdate, DatabusEntry } from '../../../hooks/useSignalR';
import type { WorkflowExecution } from '../../../types/api';
import { useDesignStore } from '../../../stores/designStore';

/**
 * ExecutionPanel is a 1500-line bottom-of-editor panel with three tabs (Live / History /
 * Output), an auto-expand-on-simulation effect, and a paused-step inspector branch.
 *
 * Strategy: pin the user-visible branches, not every line. The huge sub-trees (StepTimeline,
 * EntryGroup, ActivityTypeIcon) are exercised indirectly via the parent's Tab-render paths.
 *
 * Mocks:
 *   - PausedVariablesInspector → marker element so we don't drag in its own deps.
 *   - useResizable hook is fine in jsdom (it's pure state) — no mock needed.
 *   - MSW for the 5+ /api endpoints the inner React Query hooks fan out to.
 *
 * We do NOT use vi.useFakeTimers — the initial useQuery resolution is enough for every
 * assertion; the 5s refetch interval doesn't fire within a test's lifetime.
 */

vi.mock('../../../components/designer/debug/PausedVariablesInspector', () => ({
  PausedVariablesInspector: ({ stepName }: { stepName: string }) => (
    <div data-testid="paused-inspector">Paused: {stepName}</div>
  ),
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

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: 'warn' }));
beforeEach(() => useDesignStore.setState({ designerMode: 'expert' }));
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks(); });
afterAll(() => server.close());

function step(extra: Partial<StepUpdate> = {}): StepUpdate {
  return {
    executionId: 'exec-1',
    workflowId: 'wf-1',
    stepId: extra.stepId ?? 'step-a',
    stepName: extra.stepName ?? 'Step A',
    stepType: extra.stepType ?? 'runScript',
    status: extra.status ?? 'Succeeded',
    output: 'ok',
    errorOutput: null,
    startedAt: '2026-04-26T10:00:00Z',
    completedAt: '2026-04-26T10:00:01Z',
    ...extra,
  };
}

function liveExec(overrides: Partial<LiveExecution> = {}): LiveExecution {
  return {
    executionId: 'exec-1',
    status: 'Running',
    steps: [step()],
    startedAt: '2026-04-26T10:00:00Z',
    completedAt: null,
    errorMessage: null,
    databus: {},
    ...overrides,
  };
}

function makeExecution(id: string, status: string): WorkflowExecution {
  return {
    id,
    workflowId: 'wf-1',
    status,
    startedAt: '2026-04-26T10:00:00Z',
    completedAt: '2026-04-26T10:00:05Z',
    triggeredBy: 'manual',
    errorMessage: null,
    traceId: null,
    spanId: null,
    returnData: null,
    inputParametersJson: null,
  };
}

function renderPanel(props: Partial<Parameters<typeof ExecutionPanel>[0]> = {}) {
  patchFetch();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ExecutionPanel
          workflowId="wf-1"
          liveExecution={null}
          connected={false}
          {...props}
        />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('ExecutionPanel', () => {
  it('standardMode_hidesWatchTab', () => {
    useDesignStore.setState({ designerMode: 'standard' });
    renderPanel();
    expect(screen.queryByRole('button', { name: /Watch/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Live/i })).toBeInTheDocument();
  });

  it('rendersThreeTabs_andDefaultsToLive', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel();

    expect(screen.getByText('Live')).toBeInTheDocument();
    expect(screen.getByText('History')).toBeInTheDocument();
    expect(screen.getByText('Output')).toBeInTheDocument();
    // Live-tab default → "No active execution" when liveExecution is null
    expect(screen.getByText(/No active execution/)).toBeInTheDocument();
  });

  it('liveTab_emptyExecution_showsHelpHint', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel();

    expect(screen.getByText(/Click "Test" to run the workflow/)).toBeInTheDocument();
  });

  it('liveTab_withExecution_rendersStepList', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel({
      liveExecution: liveExec({
        steps: [step({ stepId: 'a', stepName: 'First' }), step({ stepId: 'b', stepName: 'Second' })],
      }),
    });

    // Step names appear both in the left list and the right-pane Live Gantt — assert at-least-one occurrence.
    expect(screen.getByText('exec-1')).toBeInTheDocument();
    expect(screen.queryByText('First')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('exec-1'));
    await waitFor(() => expect(screen.getAllByText('First').length).toBeGreaterThan(0));
    expect(screen.getAllByText('Second').length).toBeGreaterThan(0);
  });

  it('liveTab_selectedStep_rendersOutputParametersFromDatabus', async () => {
    server.use(
      http.get(`${BASE}/api/executions`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/workflows/wf-1`, () => HttpResponse.json({
        id: 'wf-1',
        name: 'WF',
        isEnabled: false,
        definitionJson: '{"nodes":[],"edges":[]}',
      })),
      http.get(`${BASE}/api/machines`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])),
    );
    const databus: Record<string, DatabusEntry> = {
      'disk-step.param.freeGb': { value: '42', stepId: 'disk-step', kind: 'param', paramKey: 'freeGb' },
      'disk-step.param.status': { value: 'ok', stepId: 'disk-step', kind: 'param', paramKey: 'status' },
      'diskCheck.param.freeGb': { value: '42', stepId: 'disk-step', kind: 'param', paramKey: 'freeGb' },
    };
    renderPanel({
      liveExecution: liveExec({
        databus,
        steps: [step({ stepId: 'disk-step', stepName: 'Free Disk Space', stepType: 'custom:disk_free_check', output: null })],
      }),
    });

    fireEvent.click(screen.getByText('exec-1'));
    await waitFor(() => expect(screen.getAllByText('Free Disk Space').length).toBeGreaterThan(0));
    fireEvent.click(screen.getAllByText('Free Disk Space')[0]);

    await waitFor(() => expect(screen.getByText('Output parameters')).toBeInTheDocument());
    expect(screen.getByText(/"freeGb": "42"/)).toBeInTheDocument();
    expect(screen.getByText(/"status": "ok"/)).toBeInTheDocument();
  });

  it('liveTab_withParallelExecutions_rendersAccordionPerRun', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const runOne = liveExec({
      executionId: 'exec-one-123456',
      steps: [step({ executionId: 'exec-one-123456', stepId: 'a', stepName: 'First run step' })],
    });
    const runTwo = liveExec({
      executionId: 'exec-two-123456',
      startedAt: '2026-04-26T10:00:01Z',
      steps: [step({ executionId: 'exec-two-123456', stepId: 'b', stepName: 'Second run step' })],
    });

    renderPanel({
      liveExecution: runTwo,
      liveExecutions: [runTwo, runOne],
    });

    expect(screen.getByText('exec-two')).toBeInTheDocument();
    expect(screen.getByText('exec-one')).toBeInTheDocument();
    expect(screen.queryByText('Second run step')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('exec-two'));
    await waitFor(() => expect(screen.getAllByText('Second run step').length).toBeGreaterThan(0));
    fireEvent.click(screen.getByText('exec-one'));
    await waitFor(() => expect(screen.getAllByText('First run step').length).toBeGreaterThan(0));
  });

  it('liveTab_usesLiveExecutionsList_evenWhenLegacyLiveExecutionIsNull', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const run = liveExec({
      executionId: 'exec-list-only',
      steps: [step({ executionId: 'exec-list-only', stepId: 'a', stepName: 'List-only step' })],
    });

    renderPanel({
      liveExecution: null,
      liveExecutions: [run],
    });

    expect(screen.queryByText(/No active execution/)).not.toBeInTheDocument();
    expect(screen.getByText('exec-lis')).toBeInTheDocument();
    expect(screen.queryByText('List-only step')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('exec-lis'));
    await waitFor(() => expect(screen.getAllByText('List-only step').length).toBeGreaterThan(0));
  });

  it('liveTab_noStepSelected_rendersOverviewWithStatsStripAndTimeline', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel({
      liveExecution: liveExec({
        steps: [
          step({ stepId: 'a', stepName: 'Alpha', status: 'Succeeded' }),
          step({ stepId: 'b', stepName: 'Beta',  status: 'Failed' }),
        ],
      }),
    });

    // Stats-Strip header — "Steps" label is unique to the strip
    fireEvent.click(screen.getByText('exec-1'));

    expect(screen.getByText('Steps')).toBeInTheDocument();
    expect(screen.getByText('2/2')).toBeInTheDocument();
    expect(screen.getByText('Elapsed')).toBeInTheDocument();
    // Timeline gantt is the default sub-tab
    expect(screen.getByTestId('live-timeline-gantt')).toBeInTheDocument();
  });

  it('liveTab_overview_clickingTimelineBarShowsInspector_AND_keepsStatsStripAndTabsVisible', async () => {
    // Regression test for Bug-3: clicking a timeline entry should SHOW the inspector
    // without hiding the LiveOverview above it (StatsStrip + Timeline/Console tabs).
    // Previously a ternary `selected ? <inspector> : <overview>` swapped the overview out
    // entirely — the user had to click back and forth between History/Live to see the
    // stats again.
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/workflows/wf-1`, () => HttpResponse.json({
      id: 'wf-1', name: 'WF', isEnabled: false, definitionJson: '{"nodes":[],"edges":[]}',
    })));
    server.use(http.get(`${BASE}/api/machines`, () => HttpResponse.json([])));
    server.use(http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])));
    renderPanel({
      liveExecution: liveExec({ steps: [step({ stepId: 'a', stepName: 'Alpha' })] }),
    });

    fireEvent.click(screen.getByText('exec-1'));

    // Before the click: stats + tabs + timeline are visible.
    expect(screen.getByText('Steps')).toBeInTheDocument();
    expect(screen.getByText('Elapsed')).toBeInTheDocument();
    expect(screen.getByTestId('live-timeline-gantt')).toBeInTheDocument();

    // Click on the Gantt bar.
    const bar = document.querySelector('[data-testid="live-timeline-gantt"] button')!;
    fireEvent.click(bar);

    // After the click: the inspector is shown (Started label) AND stats + tabs +
    // timeline are still visible.
    await waitFor(() => expect(screen.getByText(/Started /)).toBeInTheDocument());
    expect(screen.getByText('Steps')).toBeInTheDocument();
    expect(screen.getByText('Elapsed')).toBeInTheDocument();
    expect(screen.getByTestId('live-timeline-gantt')).toBeInTheDocument();

    // The close (X) button in the inspector hides it again; the overview stays.
    fireEvent.click(screen.getByLabelText('Close step inspector'));
    await waitFor(() => expect(screen.queryByText(/Started /)).not.toBeInTheDocument());
    expect(screen.getByText('Steps')).toBeInTheDocument();
  });

  it('liveTab_pausedStep_rendersPausedInspector_andSkipsStepList', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel({
      liveExecution: liveExec({
        steps: [step({ stepId: 'p', stepName: 'Halted', status: 'Paused', pausedAt: '2026-04-26T10:00:02Z' })],
      }),
    });

    expect(screen.queryByTestId('paused-inspector')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('exec-1'));

    await waitFor(() => expect(screen.getByTestId('paused-inspector')).toBeInTheDocument());
    expect(screen.getByText(/Paused: Halted/)).toBeInTheDocument();
  });

  it('historyTab_empty_showsNoHistoryMessage', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel();

    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText(/No execution history/)).toBeInTheDocument());
  });

  it('historyTab_withExecutions_rendersTableRows', async () => {
    const executions = [makeExecution('exec-old-1', 'Succeeded'), makeExecution('exec-old-2', 'Failed')];
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json(executions)));
    renderPanel();

    fireEvent.click(screen.getByText('History'));

    // Each row's ID button shows execution.id.slice(0, 8) → both render "exec-old".
    await waitFor(() => expect(screen.getAllByText('exec-old').length).toBe(2));
  });

  it('historyTab_columnHeaders_sortRows', async () => {
    const fast = {
      ...makeExecution('aaaaaaaa-fast', 'Succeeded'),
      startedAt: '2026-04-26T10:00:00Z',
      completedAt: '2026-04-26T10:00:01Z',
    };
    const slow = {
      ...makeExecution('bbbbbbbb-slow', 'Failed'),
      startedAt: '2026-04-26T10:00:10Z',
      completedAt: '2026-04-26T10:00:20Z',
    };
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([slow, fast])));
    renderPanel();

    fireEvent.click(screen.getByText('History'));
    await waitFor(() => expect(screen.getByText('aaaaaaaa')).toBeInTheDocument());

    const rowIds = () => Array.from(document.querySelectorAll('[data-row-id]'))
      .map((row) => row.textContent ?? '')
      .filter((text) => text.includes('aaaaaaaa') || text.includes('bbbbbbbb'))
      .map((text) => text.includes('aaaaaaaa') ? 'aaaaaaaa' : 'bbbbbbbb');

    fireEvent.click(screen.getByRole('button', { name: /Duration/ }));
    expect(rowIds()).toEqual(['bbbbbbbb', 'aaaaaaaa']);

    fireEvent.click(screen.getByRole('button', { name: /Duration/ }));
    expect(rowIds()).toEqual(['aaaaaaaa', 'bbbbbbbb']);
  });

  it('historyTab_scopeToggle_switchesEndpoint', async () => {
    let lastUrl = '';
    server.use(
      http.get(`${BASE}/api/executions`, ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json([]);
      }),
      http.get(`${BASE}/api/workflows`, () => HttpResponse.json([])),
    );
    renderPanel();

    fireEvent.click(screen.getByText('History'));
    await waitFor(() => expect(lastUrl).toContain('workflowId=wf-1'));

    fireEvent.click(screen.getByText('All workflows'));

    await waitFor(() => expect(lastUrl).not.toContain('workflowId=wf-1'));
  });

  it('historyTab_replayButton_callsOnReplayWithExecutionId', async () => {
    const onReplay = vi.fn();
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([makeExecution('exec-x', 'Succeeded')])));
    renderPanel({ onReplay });

    fireEvent.click(screen.getByText('History'));
    await waitFor(() => expect(screen.getByText('exec-x')).toBeInTheDocument());

    fireEvent.click(screen.getByTitle(/Show execution path on canvas/));
    expect(onReplay).toHaveBeenCalledWith('exec-x');
  });

  it('historyTab_triageColumns_renderUserStepsFailedStepAndParentBadge', async () => {
    // Triage columns in the History view. A single failed sub-workflow run must render
    // all four data points: username, steps progress, failed-step name, and the
    // "↳ Sub-WF" badge naming the parent workflow.
    const exec: WorkflowExecution = {
      ...makeExecution('exec-triage', 'Failed'),
      startedByUsername: 'alice',
      stepsTotal: 15,
      stepsCompleted: 12,
      failedSteps: [{ stepId: 'step-12', stepName: 'Apply Migration' }],
      parentExecutionId: 'parent-exec-1',
      parentWorkflowName: 'Daily Report',
    };
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([exec])));
    renderPanel();

    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText('exec-tri')).toBeInTheDocument());
    expect(screen.getByText('alice')).toBeInTheDocument();
    expect(screen.getByText('12/15')).toBeInTheDocument();
    expect(screen.getByText('Apply Migration')).toBeInTheDocument();
    expect(screen.getByText(/↳ Daily Report/)).toBeInTheDocument();
  });

  it('historyTab_failedStep_multipleFailedStepsRenderCommaJoined', async () => {
    // Parallel branches can fail at the same time. The server returns the list in
    // chronological order — the grid joins the step names with commas, preserving that
    // order. A step with no name falls back to showing its step ID.
    const exec: WorkflowExecution = {
      ...makeExecution('exec-multi-fail', 'Failed'),
      failedSteps: [
        { stepId: 'branch-a', stepName: 'Send Email' },
        { stepId: 'branch-b', stepName: 'Update DB' },
        { stepId: 'no-label', stepName: null },
      ],
    };
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([exec])));
    renderPanel();

    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText('exec-mul')).toBeInTheDocument());
    expect(screen.getByText('Send Email, Update DB, no-label')).toBeInTheDocument();
  });

  it('historyTab_triageColumns_emptyValuesShowDashes', async () => {
    // A trigger-started run with no user, top-level (no parent), no steps, and no failed
    // step: all three columns show "—". Guards against the grid flashing "undefined/0"
    // or similar for trigger-started runs.
    const exec: WorkflowExecution = {
      ...makeExecution('exec-empty', 'Cancelled'),
      triggeredBy: 'schedule',
      startedByUsername: null,
      stepsTotal: 0,
      stepsCompleted: 0,
      failedSteps: null,
      parentExecutionId: null,
      parentWorkflowName: null,
    };
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([exec])));
    renderPanel();

    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText('exec-emp')).toBeInTheDocument());
    // Three "—" cells (User, Steps, Failed Step) — the Error column is empty for Cancelled.
    const dashes = screen.getAllByText('—');
    expect(dashes.length).toBeGreaterThanOrEqual(3);
    // No Sub-WF badge without a parentExecutionId.
    expect(screen.queryByText(/↳/)).toBeNull();
  });

  it('outputTab_emptyDatabus_showsWaitingMessage_whenSteps', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel({ liveExecution: liveExec() });

    fireEvent.click(screen.getByText('Output'));

    await waitFor(() => expect(screen.getByText(/Waiting for named parameters/)).toBeInTheDocument());
  });

  it('outputTab_emptyDatabus_noLive_showsRunWorkflowHint', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel();

    fireEvent.click(screen.getByText('Output'));

    await waitFor(() => expect(screen.getByText(/No data bus values yet/)).toBeInTheDocument());
  });

  it('outputTab_withDatabusEntries_rendersFilterAndExpandControls', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const databus: Record<string, DatabusEntry> = {
      'manual.userInput': { value: 'hello', kind: 'trigger' },
      'globals.API_URL': { value: 'https://api.test', kind: 'global' },
    };
    renderPanel({ liveExecution: liveExec({ databus }) });

    fireEvent.click(screen.getByText('Output'));

    await waitFor(() => expect(screen.getByPlaceholderText(/Filter by key or value/)).toBeInTheDocument());
    expect(screen.getByText('Expand all')).toBeInTheDocument();
    expect(screen.getByText('Collapse')).toBeInTheDocument();
  });

  it('connectedIndicator_visibleWhenConnectedTrue', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const { container } = renderPanel({ connected: true });

    // The connected indicator is a green-500 dot with title "SignalR connected"
    const indicator = container.querySelector('[title="SignalR connected"]');
    expect(indicator).not.toBeNull();
  });

  it('connectedIndicator_hiddenWhenConnectedFalse', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const { container } = renderPanel({ connected: false });

    const indicator = container.querySelector('[title="SignalR connected"]');
    expect(indicator).toBeNull();
  });

  it('simulationProp_autoExpandsToLiveTabAndShowsSimulation', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const sim: SimulationSnapshot = {
      order: ['step-a', 'step-b'],
      skipped: ['step-c'],
      nodeLabels: { 'step-a': 'A', 'step-b': 'B', 'step-c': 'C' },
      nodeTypes: { 'step-a': 'runScript', 'step-b': 'log', 'step-c': 'restApi' },
      revealIndex: 1,
    };

    // Start with no simulation, then click History tab → user is "elsewhere"
    const { rerender } = renderPanel();
    fireEvent.click(screen.getByText('History'));

    // Now provide simulation prop → effect should auto-jump back to Live
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    rerender(
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <ExecutionPanel workflowId="wf-1" liveExecution={null} connected={false} simulation={sim} />
        </MemoryRouter>
      </QueryClientProvider>
    );

    // After auto-jump to Live, the SimulationTab should render the order count
    await waitFor(() => {
      // SimulationTab renders something distinct — at minimum, no "No active execution" hint
      expect(screen.queryByText(/No active execution/)).not.toBeInTheDocument();
    });
  });

  it('collapseToggle_collapsesPanel_andClickToExpand', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    renderPanel({ liveExecution: liveExec({ status: 'Running' }) });

    // Header has a ChevronDown collapse button — locate it by its accessible label.
    fireEvent.click(screen.getByLabelText('Collapse execution panel'));

    // After collapse: the "Running" pill appears in the collapsed bar
    await waitFor(() => expect(screen.getByText('Running')).toBeInTheDocument());

    // Click anywhere on the collapsed bar to expand again
    const collapsedBar = screen.getByText('Execution').closest('div')!.parentElement!;
    fireEvent.click(collapsedBar);

    // Tabs are visible again
    await waitFor(() => expect(screen.getByText('History')).toBeInTheDocument());
  });

  it('historyTabBadge_showsExecutionCount', async () => {
    const executions = [makeExecution('a', 'Succeeded'), makeExecution('b', 'Succeeded'), makeExecution('c', 'Failed')];
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json(executions)));
    renderPanel();

    // The History tab gets a badge with the count
    await waitFor(() => {
      const historyTab = screen.getByText('History').closest('button')!;
      expect(historyTab.textContent).toContain('3');
    });
  });

  it('outputTabBadge_showsDatabusEntryCount', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const databus: Record<string, DatabusEntry> = {
      'manual.x': { value: '1', kind: 'trigger' },
      'manual.y': { value: '2', kind: 'trigger' },
    };
    renderPanel({ liveExecution: liveExec({ databus }) });

    const outputTab = screen.getByText('Output').closest('button')!;
    expect(outputTab.textContent).toContain('2');
  });

  it('historyTab_stepTimeline_doesNotRenderSkippedSteps', async () => {
    const exec = makeExecution('exec-skip', 'Failed');
    server.use(
      http.get(`${BASE}/api/executions`, () => HttpResponse.json([exec])),
      http.get(`${BASE}/api/executions/exec-skip/steps`, () => HttpResponse.json([
        {
          id: 'se-1', stepId: 'step-run', stepName: 'Ran Step', stepType: 'log',
          status: 'Succeeded', output: null, errorOutput: null, traceOutput: null,
          startedAt: '2026-04-26T10:00:00Z', completedAt: '2026-04-26T10:00:01Z',
          workflowExecutionId: 'exec-skip',
        },
        {
          id: 'se-2', stepId: 'step-skip', stepName: 'Skipped Step', stepType: 'log',
          status: 'Skipped', output: null, errorOutput: null, traceOutput: null,
          startedAt: '2026-04-26T10:00:01Z', completedAt: '2026-04-26T10:00:01Z',
          workflowExecutionId: 'exec-skip',
        },
      ])),
      http.get(`${BASE}/api/workflows/wf-1/nodes/step-run`, () => HttpResponse.json(null)),
    );

    renderPanel();
    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText('exec-ski')).toBeInTheDocument());
    fireEvent.click(screen.getByText('exec-ski').closest('[data-row-id]')!);

    await waitFor(() => expect(screen.getByText('Ran Step')).toBeInTheDocument());
    expect(screen.queryByText('Skipped Step')).not.toBeInTheDocument();
  });

  it('historyTab_expandedStep_rendersOutputParametersJson', async () => {
    const exec = makeExecution('exec-output', 'Succeeded');
    server.use(
      http.get(`${BASE}/api/executions`, () => HttpResponse.json([exec])),
      http.get(`${BASE}/api/executions/exec-output/steps`, () => HttpResponse.json([
        {
          id: 'se-output',
          stepId: 'free-step',
          stepName: 'Free Disk Space',
          stepType: 'custom:disk_free_check',
          status: 'Succeeded',
          output: null,
          errorOutput: null,
          traceOutput: null,
          outputParametersJson: JSON.stringify({ freeGb: '42', status: 'ok' }),
          startedAt: '2026-04-26T10:00:00Z',
          completedAt: '2026-04-26T10:00:01Z',
          workflowExecutionId: 'exec-output',
        },
      ])),
      http.get(`${BASE}/api/workflows/wf-1`, () => HttpResponse.json({
        id: 'wf-1',
        name: 'WF',
        isEnabled: false,
        definitionJson: '{"nodes":[],"edges":[]}',
      })),
      http.get(`${BASE}/api/machines`, () => HttpResponse.json([])),
      http.get(`${BASE}/api/credentials`, () => HttpResponse.json([])),
    );

    renderPanel();
    fireEvent.click(screen.getByText('History'));

    await waitFor(() => expect(screen.getByText('exec-out')).toBeInTheDocument());
    fireEvent.click(screen.getByText('exec-out').closest('[data-row-id]')!);

    await waitFor(() => expect(screen.getByText('Free Disk Space')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Free Disk Space').closest('button')!);

    await waitFor(() => expect(screen.getByText('Output parameters')).toBeInTheDocument());
    expect(screen.getByText(/"freeGb": "42"/)).toBeInTheDocument();
    expect(screen.getByText(/"status": "ok"/)).toBeInTheDocument();
  });
});

// Characterization (pre-/post-refactor): the Live tab joins the per-execution SignalR
// group when a run's accordion is expanded and leaves it on collapse. Pinned here because
// the LiveExecutionPanel extraction moved this wiring out of ExecutionPanel.
describe('LiveTab — execution join/leave (characterization)', () => {
  it('joins on expand and leaves on collapse', async () => {
    server.use(http.get(`${BASE}/api/executions`, () => HttpResponse.json([])));
    const onJoinExecution = vi.fn(() => Promise.resolve());
    const onLeaveExecution = vi.fn(() => Promise.resolve());
    const run = liveExec({
      executionId: 'execjoinABCDEF',
      steps: [step({ executionId: 'execjoinABCDEF', stepId: 'a', stepName: 'Step A' })],
    });
    renderPanel({ liveExecution: run, liveExecutions: [run], onJoinExecution, onLeaveExecution });

    // Accordion header shows the 8-char short id.
    fireEvent.click(screen.getByText('execjoin'));
    await waitFor(() => expect(onJoinExecution).toHaveBeenCalledWith('execjoinABCDEF', 'wf-1'));

    fireEvent.click(screen.getByText('execjoin'));
    await waitFor(() => expect(onLeaveExecution).toHaveBeenCalledWith('execjoinABCDEF'));
  });
});
