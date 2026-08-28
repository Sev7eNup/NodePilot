import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { GanttChart, type GanttRow } from '../../../../components/designer/timeline/GanttChart';

/**
 * Unit tests for the shared GanttChart used by both History and Live tabs. They cover the
 * rendering contract (caller-normalized rows in, axis and bars out) plus the optional scrub
 * slider and click handler. Exact bar pixel widths are not asserted, only that the bars exist
 * and the structure scales with row count.
 */

const T0 = new Date('2026-04-26T10:00:00Z').getTime();

function row(over: Partial<GanttRow> = {}): GanttRow {
  // Use `in` checks instead of `??` so callers can pass null or 0 to override the defaults;
  // `??` would fall back to the default and mask the null.
  return {
    id: over.id ?? 'r1',
    name: over.name ?? 'Row 1',
    status: over.status ?? 'Succeeded',
    startMs: 'startMs' in over ? over.startMs! : T0,
    endMs: 'endMs' in over ? over.endMs! : T0 + 1000,
  };
}

describe('GanttChart', () => {
  it('rendersOneRowPerInput_withTimeAxisAndDurationLabel', () => {
    render(
      <GanttChart
        rows={[
          row({ id: 'a', name: 'Alpha', status: 'Succeeded' }),
          row({ id: 'b', name: 'Beta',  status: 'Failed', startMs: T0 + 500, endMs: T0 + 1500 }),
        ]}
      />
    );
    expect(screen.getByTestId('gantt-chart')).toBeInTheDocument();
    expect(screen.getByText('Alpha')).toBeInTheDocument();
    expect(screen.getByText('Beta')).toBeInTheDocument();
    // Each row renders its duration label.
    expect(screen.getAllByText(/1\.00s/).length).toBeGreaterThanOrEqual(2);
  });

  it('rendersEmptyState_whenNoStartMsAvailable', () => {
    render(<GanttChart rows={[row({ startMs: null, endMs: null, status: 'Skipped' })]} />);
    expect(screen.getByText(/Waiting for first step/)).toBeInTheDocument();
    expect(screen.queryByTestId('gantt-chart')).toBeNull();
  });

  it('callsOnSelectRow_withRowId_whenRowClicked', () => {
    const onSelect = vi.fn();
    render(<GanttChart rows={[row({ id: 'foo', name: 'Foo' })]} onSelectRow={onSelect} />);
    const button = screen.getByText('Foo').closest('button')!;
    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledWith('foo');
  });

  it('rendersRowsAsNonInteractiveDivs_whenOnSelectRowOmitted', () => {
    render(<GanttChart rows={[row({ id: 'foo', name: 'Foo' })]} />);
    expect(screen.getByText('Foo').closest('button')).toBeNull();
  });

  it('rendersScrubSlider_whenScrubProvided_andFiresOnChangeOnDrag', () => {
    const onChange = vi.fn();
    render(
      <GanttChart
        rows={[row()]}
        scrub={{ value: null, onChange }}
      />
    );
    const slider = document.querySelector('input[type="range"]') as HTMLInputElement;
    expect(slider).not.toBeNull();
    // A change event on the slider propagates to onChange.
    fireEvent.change(slider, { target: { value: T0 + 500 } });
    expect(onChange).toHaveBeenCalled();
  });

  it('omitsScrubSlider_whenScrubAbsent', () => {
    render(<GanttChart rows={[row()]} />);
    expect(document.querySelector('input[type="range"]')).toBeNull();
  });

  it('extendsAxis_whenNowMsBeyondLastEnd_forLiveRunningRow', () => {
    // A running row with endMs=null and nowMs in the future still renders a bar.
    const liveNow = T0 + 5000;
    render(
      <GanttChart
        rows={[row({ id: 'live', name: 'Live', status: 'Running', endMs: null })]}
        nowMs={liveNow}
      />
    );
    // The live row's duration label comes from nowMs - startMs; formatMs renders two
    // decimals below 10s.
    expect(screen.getByText(/5\.00s/)).toBeInTheDocument();
  });
});
