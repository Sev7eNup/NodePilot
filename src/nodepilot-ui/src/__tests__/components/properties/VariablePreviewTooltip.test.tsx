import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act, fireEvent } from '@testing-library/react';
import { VariablePreviewTooltip } from '../../../components/designer/properties/VariablePreviewTooltip';
import type { StepExecution } from '../../../types/api';

/**
 * VariablePreviewTooltip wraps a hoverable surface (typically a variable-row) and pops a
 * delayed bubble showing the last-run value. We pin:
 *   - 250 ms hover delay before opening
 *   - leave-before-delay closes silently
 *   - empty step → "no value" italics
 *   - existing onMouseEnter / onMouseLeave / onClick handlers still fire alongside the tooltip
 *   - cleanup of the timer on unmount (no console errors / setState-on-unmount warnings)
 */

function step(over: Partial<StepExecution> = {}): StepExecution {
  return {
    id: 'sx-1',
    stepId: 'step-42',
    stepName: null,
    stepType: 'runScript',
    targetMachine: null,
    status: 'Succeeded',
    startedAt: null,
    completedAt: null,
    output: 'hello world',
    errorOutput: null,
    traceOutput: null,
    ...over,
  };
}

describe('VariablePreviewTooltip', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('opensTooltip_after250msHover_withResolvedValue', () => {
    render(
      <VariablePreviewTooltip step={step()} expression="{{disk.output}}">
        <div data-testid="row">disk.output</div>
      </VariablePreviewTooltip>
    );
    fireEvent.mouseEnter(screen.getByTestId('row').parentElement!);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(250); });
    const tip = screen.getByRole('tooltip');
    expect(tip.textContent).toContain('hello world');
    expect(tip.textContent).toContain('stdout (last run)');
  });

  it('leaveBeforeDelay_doesNotOpen', () => {
    render(
      <VariablePreviewTooltip step={step()} expression="{{disk.output}}">
        <div data-testid="row">disk.output</div>
      </VariablePreviewTooltip>
    );
    const wrapper = screen.getByTestId('row').parentElement!;
    fireEvent.mouseEnter(wrapper);
    act(() => { vi.advanceTimersByTime(150); });
    fireEvent.mouseLeave(wrapper);
    act(() => { vi.advanceTimersByTime(500); });
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('noStep_showsNoValueMessage', () => {
    // Workflow has never run yet → preview is null → friendly fallback text.
    render(
      <VariablePreviewTooltip step={undefined} expression="{{disk.output}}">
        <div data-testid="row">disk.output</div>
      </VariablePreviewTooltip>
    );
    fireEvent.mouseEnter(screen.getByTestId('row').parentElement!);
    act(() => { vi.advanceTimersByTime(250); });
    const tip = screen.getByRole('tooltip');
    expect(tip.textContent).toContain('Kein Wert vom letzten Lauf');
  });

  it('emptyOutput_showsNoValueMessage', () => {
    render(
      <VariablePreviewTooltip step={step({ output: null })} expression="{{disk.output}}">
        <div data-testid="row">disk.output</div>
      </VariablePreviewTooltip>
    );
    fireEvent.mouseEnter(screen.getByTestId('row').parentElement!);
    act(() => { vi.advanceTimersByTime(250); });
    expect(screen.getByRole('tooltip').textContent).toContain('Kein Wert');
  });

  it('forwardsMouseEnterAndLeaveHandlers', () => {
    // The tooltip wraps existing var-flow-highlight hover handlers — those must still fire.
    const onEnter = vi.fn();
    const onLeave = vi.fn();
    render(
      <VariablePreviewTooltip
        step={step()}
        expression="{{disk.output}}"
        onMouseEnter={onEnter}
        onMouseLeave={onLeave}
      >
        <div data-testid="row" />
      </VariablePreviewTooltip>
    );
    const wrapper = screen.getByTestId('row').parentElement!;
    fireEvent.mouseEnter(wrapper);
    expect(onEnter).toHaveBeenCalledOnce();
    fireEvent.mouseLeave(wrapper);
    expect(onLeave).toHaveBeenCalledOnce();
  });

  it('forwardsClickHandler_forClipboardCopy', () => {
    const onClick = vi.fn();
    render(
      <VariablePreviewTooltip
        step={step()}
        expression="{{disk.output}}"
        onClick={onClick}
      >
        <div data-testid="row" />
      </VariablePreviewTooltip>
    );
    fireEvent.click(screen.getByTestId('row').parentElement!);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('truncatedFlag_surfacesTruncatedBadge', () => {
    const longOutput = 'x'.repeat(500);
    render(
      <VariablePreviewTooltip step={step({ output: longOutput })} expression="{{disk.output}}">
        <div data-testid="row" />
      </VariablePreviewTooltip>
    );
    fireEvent.mouseEnter(screen.getByTestId('row').parentElement!);
    act(() => { vi.advanceTimersByTime(250); });
    expect(screen.getByRole('tooltip').textContent).toContain('truncated');
  });

  it('unmountWhilePending_doesNotThrow', () => {
    // No state-on-unmounted-component warnings — clearTimeout in cleanup must run.
    const { unmount } = render(
      <VariablePreviewTooltip step={step()} expression="{{disk.output}}">
        <div data-testid="row" />
      </VariablePreviewTooltip>
    );
    fireEvent.mouseEnter(screen.getByTestId('row').parentElement!);
    expect(() => {
      unmount();
      vi.advanceTimersByTime(1000);
    }).not.toThrow();
  });
});
