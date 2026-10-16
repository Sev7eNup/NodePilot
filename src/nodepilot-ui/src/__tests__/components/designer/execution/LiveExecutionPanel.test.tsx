import { describe, it, expect, vi, beforeEach, type Mock } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MutationCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { handleMutationError } from '../../../../lib/queryErrorToast';
import { useToastStore } from '../../../../stores/toastStore';
import type { LiveExecution } from '../../../../hooks/useSignalR';
import { LiveTab } from '../../../../components/designer/execution/LiveExecutionPanel';

type JoinFn = (executionId: string, workflowId: string) => Promise<void>;
type LeaveFn = (executionId: string) => Promise<void>;

// The Live list is virtualized; under jsdom's zero-height layout @tanstack/react-virtual
// renders 0 rows, so the accordion button that populates expandedIds never mounts. The
// virtualizer is stubbed to project every row, which keeps the toggle clickable.
vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: (opts: { count: number; getItemKey?: (i: number) => string }) => ({
    getTotalSize: () => opts.count * 30,
    getVirtualItems: () => Array.from({ length: opts.count }, (_, index) => ({
      index,
      start: index * 30,
      key: opts.getItemKey ? opts.getItemKey(index) : index,
    })),
    measureElement: () => {},
  }),
}));

// The expanded detail pane (StatsStrip/LiveTimeline/LiveConsole) is irrelevant to the
// join/leave lifecycle and pulls in heavy children, so it is stubbed to keep expanding cheap.
vi.mock('../../../../components/designer/live/LiveOverview', () => ({
  LiveOverview: () => <div data-testid="live-overview-stub" />,
}));

function makeExec(id: string, workflowId = 'wf-1'): LiveExecution {
  return {
    executionId: id,
    workflowId,
    status: 'Running',
    steps: [],
    startedAt: '2026-06-23T10:00:00Z',
    completedAt: null,
    errorMessage: null,
    databus: {},
  };
}

function liveTabEl(props: Partial<Parameters<typeof LiveTab>[0]> = {}) {
  return (
    <LiveTab
      execution={null}
      executionsLive={[]}
      simulation={null}
      workflowId="wf-1"
      executions={[]}
      panelHeight={400}
      {...props}
    />
  );
}

describe('LiveTab SignalR group lifecycle', () => {
  let onJoin: Mock<JoinFn>;
  let onLeave: Mock<LeaveFn>;

  beforeEach(() => {
    onJoin = vi.fn<JoinFn>(() => Promise.resolve());
    onLeave = vi.fn<LeaveFn>(() => Promise.resolve());
  });

  it('joins the execution group when a run is expanded', () => {
    render(liveTabEl({ executionsLive: [makeExec('exec-1')], onJoinExecution: onJoin, onLeaveExecution: onLeave }));
    fireEvent.click(screen.getByRole('button'));
    expect(onJoin).toHaveBeenCalledWith('exec-1', 'wf-1');
    expect(onLeave).not.toHaveBeenCalled();
  });

  it('leaves expanded groups when the live list empties (no early-return skip)', () => {
    const { rerender } = render(
      liveTabEl({ executionsLive: [makeExec('exec-1')], onJoinExecution: onJoin, onLeaveExecution: onLeave }),
    );
    fireEvent.click(screen.getByRole('button'));
    expect(onJoin).toHaveBeenCalledTimes(1);

    // Every live run is evicted at once; the panel must still leave what was expanded.
    rerender(liveTabEl({ executionsLive: [], onJoinExecution: onJoin, onLeaveExecution: onLeave }));
    expect(onLeave).toHaveBeenCalledWith('exec-1');
  });

  it('leaves still-expanded groups on unmount', () => {
    const { unmount } = render(
      liveTabEl({ executionsLive: [makeExec('exec-1')], onJoinExecution: onJoin, onLeaveExecution: onLeave }),
    );
    fireEvent.click(screen.getByRole('button'));
    expect(onLeave).not.toHaveBeenCalled();

    unmount();
    expect(onLeave).toHaveBeenCalledWith('exec-1');
  });

  it('leaves only the evicted run when one of several disappears', () => {
    const { rerender } = render(
      liveTabEl({
        executionsLive: [makeExec('exec-1'), makeExec('exec-2')],
        onJoinExecution: onJoin,
        onLeaveExecution: onLeave,
      }),
    );
    // Expand exec-1 (first accordion button).
    fireEvent.click(screen.getAllByRole('button')[0]);
    expect(onJoin).toHaveBeenCalledWith('exec-1', 'wf-1');

    // exec-1 evicted, exec-2 stays.
    rerender(liveTabEl({ executionsLive: [makeExec('exec-2')], onJoinExecution: onJoin, onLeaveExecution: onLeave }));
    expect(onLeave).toHaveBeenCalledWith('exec-1');
    expect(onLeave).not.toHaveBeenCalledWith('exec-2');
  });
});

describe('LiveTab paused-step debugger', () => {
  it('reports a failed resume and keeps the edited variables for a retry', async () => {
    useToastStore.setState({ toasts: [] });
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(
      JSON.stringify({ detail: 'Execution is no longer paused' }),
      { status: 409, headers: { 'Content-Type': 'application/json' } },
    ));
    const paused: LiveExecution = {
      ...makeExec('exec-1'),
      steps: [{
        executionId: 'exec-1', workflowId: 'wf-1', stepId: 'step-a', stepName: 'Halted', stepType: 'runScript',
        status: 'Paused', pausedAt: '2026-06-23T10:00:02Z', pausedVariables: { 'globals.Env': 'prod' },
      }],
    };
    // Same mutation cache as the app, so a mutation without its own onError reaches the toast.
    const client = new QueryClient({ mutationCache: new MutationCache({ onError: handleMutationError }) });
    render(<QueryClientProvider client={client}>{liveTabEl({ executionsLive: [paused] })}</QueryClientProvider>);
    fireEvent.click(screen.getByRole('button'));

    fireEvent.change(await screen.findByDisplayValue('prod'), { target: { value: 'test' } });
    fireEvent.click(screen.getByRole('button', { name: /Continue/ }));

    await waitFor(() => expect(useToastStore.getState().toasts.map((t) => t.message))
      .toContain('Execution is no longer paused'));
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/executions/exec-1/resume');
    expect(screen.getByDisplayValue('test')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Continue/ })).toBeEnabled();
    fetchSpy.mockRestore();
  });
});
