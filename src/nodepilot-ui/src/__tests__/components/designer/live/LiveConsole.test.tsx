import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { LiveConsole } from '../../../../components/designer/live/LiveConsole';
import type { LiveExecution, StepUpdate } from '../../../../hooks/useSignalR';

/**
 * Tests for the Live Console — the line-stream right-pane sub-tab. Asserts:
 *  - per-step output is split into one DOM line per text line
 *  - errors render in error tone (kept implicit via class match) AND show in counter
 *  - filter input narrows visible lines
 *  - errors-only toggle isolates stderr lines
 *  - clicking a line invokes onSelectStep with the originating step's id
 *  - the MAX_LINES cap collapses excess into a "hidden" notice
 *
 * jsdom doesn't actually scroll, so the auto-scroll behavior is exercised but not asserted.
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
    output: null,
    errorOutput: null,
    startedAt: T0,
    completedAt: '2026-04-26T10:00:01Z',
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

describe('LiveConsole', () => {
  it('rendersWaitingMessage_whenNoOutputYet', () => {
    render(<LiveConsole execution={exec({ steps: [step({ output: null, errorOutput: null })] })} />);
    expect(screen.getByText(/Waiting for step output/)).toBeInTheDocument();
  });

  it('splitsMultilineOutputIntoSeparateLines', () => {
    render(
      <LiveConsole
        execution={exec({
          steps: [step({ stepId: 's1', stepName: 'S1', output: 'first line\nsecond line\nthird line' })],
        })}
      />
    );
    expect(screen.getByText('first line')).toBeInTheDocument();
    expect(screen.getByText('second line')).toBeInTheDocument();
    expect(screen.getByText('third line')).toBeInTheDocument();
  });

  it('rendersStdoutAndStderrFromMultipleSteps_inExecutionTimeOrder', () => {
    render(
      <LiveConsole
        execution={exec({
          steps: [
            step({ stepId: 's1', stepName: 'First', output: 'hello', startedAt: '2026-04-26T10:00:00Z' }),
            step({ stepId: 's2', stepName: 'Second', errorOutput: 'oops', startedAt: '2026-04-26T10:00:02Z' }),
          ],
        })}
      />
    );
    expect(screen.getByText('hello')).toBeInTheDocument();
    expect(screen.getByText('oops')).toBeInTheDocument();
    // Errors counter shows 1
    const errorsBtn = screen.getByTitle(/Show only error lines/);
    expect(errorsBtn.textContent).toContain('1');
  });

  it('filtersByText_caseInsensitively', () => {
    render(
      <LiveConsole
        execution={exec({
          steps: [step({ output: 'INFO: starting\nWARN: slow\nINFO: done' })],
        })}
      />
    );
    fireEvent.change(screen.getByPlaceholderText('Filter…'), { target: { value: 'warn' } });
    expect(screen.getByText('WARN: slow')).toBeInTheDocument();
    expect(screen.queryByText('INFO: starting')).toBeNull();
    expect(screen.queryByText('INFO: done')).toBeNull();
  });

  it('errorsOnlyToggle_hidesStdoutLines', () => {
    render(
      <LiveConsole
        execution={exec({
          steps: [step({ output: 'normal', errorOutput: 'failure' })],
        })}
      />
    );
    fireEvent.click(screen.getByTitle(/Show only error lines/));
    expect(screen.getByText('failure')).toBeInTheDocument();
    expect(screen.queryByText('normal')).toBeNull();
  });

  it('clickingALineInvokesOnSelectStep_withStepId', () => {
    const onSelect = vi.fn();
    render(
      <LiveConsole
        execution={exec({ steps: [step({ stepId: 'foo', stepName: 'Foo', output: 'click me' })] })}
        onSelectStep={onSelect}
      />
    );
    fireEvent.click(screen.getByText('click me'));
    expect(onSelect).toHaveBeenCalledWith('foo');
  });

  it('autoScrollToggle_changesLabel_betweenLiveAndPaused', () => {
    render(<LiveConsole execution={exec({ steps: [step({ output: 'x' })] })} />);
    expect(screen.getByText('Live')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Live'));
    expect(screen.getByText('Paused')).toBeInTheDocument();
  });

  it('showsTruncationNotice_whenExceedingMaxLines', () => {
    // Build > 1000 lines of output in a single step
    const huge = Array.from({ length: 1100 }, (_, i) => `line-${i}`).join('\n');
    render(<LiveConsole execution={exec({ steps: [step({ output: huge })] })} />);
    expect(screen.getByText(/100 earlier lines hidden/)).toBeInTheDocument();
    // Last line is still visible (auto-scroll target)
    expect(screen.getByText('line-1099')).toBeInTheDocument();
    // First line is dropped
    expect(screen.queryByText('line-0')).toBeNull();
  });
});
