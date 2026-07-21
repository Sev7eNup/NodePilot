import { describe, it, expect, vi, beforeEach, type Mock } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import type { LiveExecution } from '../../../../hooks/useSignalR';
import { LiveTab } from '../../../../components/designer/execution/LiveExecutionPanel';

type JoinFn = (executionId: string, workflowId: string) => Promise<void>;
type LeaveFn = (executionId: string) => Promise<void>;

// The Live list is virtualized; under jsdom's zero-height layout @tanstack/react-virtual
// renders 0 rows, so the accordion button (our only way to populate expandedIds) never
// mounts. Stub the virtualizer to project every row so the toggle is clickable.
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
// join/leave lifecycle and pulls in heavy children — stub it so expanding is cheap.
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

    // Every live run evicted at once — the panel must still leave what was expanded.
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
