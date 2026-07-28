import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { OpsStuckStrip } from '../../../components/operations/OpsStuckStrip';
import type { PlacedBar } from '../../../lib/opsTimeline';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function bar(p: Partial<PlacedBar> & { executionId: string }): PlacedBar {
  return {
    workflowId: 'wf-1', status: 'Running',
    startedAtMs: NOW - 30 * MIN, completedAtMs: null, parentExecutionId: null,
    stepsFinished: null, lastCompletedStepName: null, lastProgressAtMs: null,
    leftPx: 0, widthPx: 100, clippedLeft: true, laneIndex: 0, subRow: 0,
    ...p,
  };
}

const NAMES: Record<string, string> = { 'wf-1': 'Nightly Backup', 'wf-2': 'Report Gen' };
const nameFor = (id: string) => NAMES[id] ?? id;

function renderStrip(bars: PlacedBar[]) {
  const onSelect = vi.fn();
  render(<OpsStuckStrip bars={bars} nowMs={NOW} nameFor={nameFor} onSelect={onSelect} />);
  return { onSelect };
}

describe('OpsStuckStrip', () => {
  it('renders nothing at all when no run is stuck', () => {
    const { container } = render(
      <OpsStuckStrip bars={[]} nowMs={NOW} nameFor={nameFor} onSelect={vi.fn()} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('names the workflow, how long it has been running and when it started', () => {
    renderStrip([bar({ executionId: 'ex-1', startedAtMs: NOW - 42 * MIN })]);
    expect(screen.getByText('Stuck / long-running')).toBeInTheDocument();
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('running for 42:00')).toBeInTheDocument();
    // The start time is the thing a clipped bar cannot convey on its own.
    expect(screen.getByText(/since \d{2}:\d{2}/)).toBeInTheDocument();
  });

  it('lists the oldest run first — longest running is most likely genuinely stuck', () => {
    renderStrip([
      bar({ executionId: 'ex-young', workflowId: 'wf-2', startedAtMs: NOW - 15 * MIN }),
      bar({ executionId: 'ex-old', workflowId: 'wf-1', startedAtMs: NOW - 90 * MIN }),
    ]);
    const items = screen.getAllByRole('button');
    expect(items[0]).toHaveTextContent('Nightly Backup');
    expect(items[1]).toHaveTextContent('Report Gen');
  });

  it('caps the list and reports how many it left out', () => {
    const many = Array.from({ length: 8 }, (_, i) =>
      bar({ executionId: `ex-${i}`, startedAtMs: NOW - (30 + i) * MIN }));
    renderStrip(many);
    expect(screen.getAllByRole('button')).toHaveLength(5);
    expect(screen.getByText('+3 more')).toBeInTheDocument();
  });

  it('forwards the execution id so the strip opens the drilldown', () => {
    const { onSelect } = renderStrip([bar({ executionId: 'ex-1' })]);
    fireEvent.click(screen.getByRole('button', { name: /Nightly Backup/ }));
    expect(onSelect).toHaveBeenCalledWith('ex-1');
  });

  it('distinguishes "long" from "stuck on one step" when activity data is present', () => {
    renderStrip([bar({
      executionId: 'ex-1', startedAtMs: NOW - 90 * MIN,
      lastProgressAtMs: NOW - 11 * MIN, lastCompletedStepName: 'Copy files',
    })]);
    expect(screen.getByText('running for 1:30:00')).toBeInTheDocument();
    expect(screen.getByText('last step 11:00 ago: Copy files')).toBeInTheDocument();
  });

  it('falls back to the start time when the run carries no activity data', () => {
    renderStrip([bar({ executionId: 'ex-1', lastProgressAtMs: null })]);
    expect(screen.getByText(/since \d{2}:\d{2}/)).toBeInTheDocument();
    expect(screen.queryByText(/last step/)).not.toBeInTheDocument();
  });
});
