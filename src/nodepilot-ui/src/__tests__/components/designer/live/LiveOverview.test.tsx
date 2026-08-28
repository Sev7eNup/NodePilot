import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { StatsStrip } from '../../../../components/designer/live/StatsStrip';
import { LiveTimeline } from '../../../../components/designer/live/LiveTimeline';
import { LiveOverview } from '../../../../components/designer/live/LiveOverview';
import type { LiveExecution, StepUpdate } from '../../../../hooks/useSignalR';

/**
 * Unit tests for the live-overview right-pane components. The pure-DOM ones (StatsStrip,
 * LiveTimeline) are tested directly; LiveOverview is exercised without rendering the MiniMap,
 * since ReactFlow needs a real layout engine. Map sub-tab visibility is asserted at the parent
 * ExecutionPanel level.
 */

const T0 = '2026-04-26T10:00:00Z';

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
    startedAt: extra.startedAt ?? T0,
    completedAt: extra.completedAt ?? '2026-04-26T10:00:01Z',
    ...extra,
  };
}

function exec(overrides: Partial<LiveExecution> = {}): LiveExecution {
  return {
    executionId: 'exec-1',
    status: 'Running',
    steps: [],
    startedAt: T0,
    completedAt: null,
    errorMessage: null,
    databus: {},
    ...overrides,
  };
}

describe('StatsStrip', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // Pin "now" near the run start so elapsed math is predictable
    vi.setSystemTime(new Date('2026-04-26T10:00:05Z'));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('rendersStepCounter_andSucceededFailedRunningCounts_excludingSkipped', () => {
    render(
      <StatsStrip
        execution={exec({
          steps: [
            step({ stepId: '1', status: 'Succeeded' }),
            step({ stepId: '2', status: 'Failed' }),
            step({ stepId: '3', status: 'Skipped' }),
            step({ stepId: '4', status: 'Running', completedAt: undefined }),
          ],
        })}
      />
    );

    // Skipped is control flow, not executed work, so the Live counter leaves it out.
    // Two of the three executed steps are terminal; the third is still running.
    expect(screen.getByText('2/3')).toBeInTheDocument();
    expect(screen.getByText('Steps')).toBeInTheDocument();
    expect(screen.getByText('Elapsed')).toBeInTheDocument();
    expect(screen.getByTitle(/1 succeeded/)).toBeInTheDocument();
    expect(screen.getByTitle(/1 failed/)).toBeInTheDocument();
    expect(screen.queryByTitle(/skipped/)).toBeNull();
    expect(screen.getByTitle(/1 running/)).toBeInTheDocument();
  });

  it('hidesZeroCounters', () => {
    render(<StatsStrip execution={exec({ steps: [step({ status: 'Succeeded' })] })} />);
    expect(screen.queryByTitle(/failed/)).toBeNull();
    expect(screen.queryByTitle(/skipped/)).toBeNull();
  });

  it('rendersETAWhenMedianProvidedAndStillRunning', () => {
    render(
      <StatsStrip
        execution={exec({ status: 'Running', steps: [step({ status: 'Running', completedAt: undefined })] })}
        medianRunDurationMs={20_000}
      />
    );
    expect(screen.getByText('ETA')).toBeInTheDocument();
  });

  it('hidesETAWhenExecutionIsTerminal', () => {
    render(
      <StatsStrip
        execution={exec({ status: 'Succeeded', completedAt: '2026-04-26T10:00:02Z', steps: [step()] })}
        medianRunDurationMs={20_000}
      />
    );
    expect(screen.queryByText('ETA')).toBeNull();
  });

  it('elapsedTickerUpdatesWhileRunning', () => {
    const { container } = render(
      <StatsStrip execution={exec({ status: 'Running', steps: [step({ status: 'Running', completedAt: undefined })] })} />
    );
    const initial = container.textContent;
    act(() => {
      vi.setSystemTime(new Date('2026-04-26T10:00:30Z'));
      vi.advanceTimersByTime(600);
    });
    expect(container.textContent).not.toBe(initial);
  });
});

describe('LiveTimeline', () => {
  it('rendersOneRowPerStep_withDurationLabel', () => {
    render(
      <LiveTimeline
        execution={exec({
          steps: [
            step({ stepId: 'a', stepName: 'Alpha', status: 'Succeeded' }),
            step({ stepId: 'b', stepName: 'Beta',  status: 'Failed' }),
          ],
        })}
      />
    );
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Beta')).toBeInTheDocument();
    // Duration column formats the step runtime; the default step runs for one second.
    expect(screen.getAllByText(/1\.00s/).length).toBeGreaterThan(0);
  });

  it('clickingARowInvokesOnSelectStepWithStepId', () => {
    const onSelect = vi.fn();
    render(
      <LiveTimeline
        execution={exec({ steps: [step({ stepId: 'foo', stepName: 'Foo' })] })}
        onSelectStep={onSelect}
      />
    );
    const button = screen.getByText('Foo').closest('button')!;
    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledWith('foo');
  });

  it('rendersWaitingMessageWhenNoSteps', () => {
    render(<LiveTimeline execution={exec({ steps: [] })} />);
    expect(screen.getByText(/Waiting for first step/)).toBeInTheDocument();
  });
});

describe('LiveOverview', () => {
  it('rendersStatsStripAndDefaultsToTimelineTab', () => {
    render(<LiveOverview execution={exec({ steps: [step()] })} />);

    // Stats-Strip header
    expect(screen.getByText('Steps')).toBeInTheDocument();
    expect(screen.getByText('Elapsed')).toBeInTheDocument();
    // Sub-tabs both render
    expect(screen.getByText('Timeline')).toBeInTheDocument();
    expect(screen.getByText('Console')).toBeInTheDocument();
    // Default content: Timeline / Gantt
    expect(screen.getByTestId('live-timeline-gantt')).toBeInTheDocument();
    expect(screen.getByTestId('gantt-chart')).toBeInTheDocument();
    // Console isn't rendered yet
    expect(screen.queryByTestId('live-console-stream')).toBeNull();
  });

  it('switchesToConsoleSubTab_whenConsoleClicked', () => {
    render(<LiveOverview execution={exec({ steps: [step()] })} />);
    fireEvent.click(screen.getByText('Console'));

    // Console replaces the Gantt
    expect(screen.getByTestId('live-console-stream')).toBeInTheDocument();
    expect(screen.queryByTestId('gantt-chart')).toBeNull();
  });
});
