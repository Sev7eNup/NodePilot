import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OpsDepartureBoard } from '../../../components/operations/OpsDepartureBoard';
import type { OpsArmedTrigger } from '../../../api/operations';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function trigger(p: Partial<OpsArmedTrigger>): OpsArmedTrigger {
  return {
    workflowId: 'wf', workflowName: 'WF', triggerTypes: ['scheduleTrigger'],
    nextFireUtc: null, nextFireKind: null, pollIntervalSeconds: null, ...p,
  };
}

describe('OpsDepartureBoard', () => {
  it('shows the empty state without triggers', () => {
    render(<OpsDepartureBoard triggers={[]} nowMs={NOW} />);
    expect(screen.getByText('No scheduled starts.')).toBeInTheDocument();
  });

  it('sorts rows by next fire time with null fires last and renders the countdown', () => {
    render(
      <OpsDepartureBoard
        triggers={[
          trigger({ workflowId: 'a', workflowName: 'Later', nextFireUtc: new Date(NOW + 30 * MIN).toISOString() }),
          trigger({ workflowId: 'b', workflowName: 'EventDriven', nextFireUtc: null, triggerTypes: ['fileWatcherTrigger'] }),
          trigger({ workflowId: 'c', workflowName: 'Sooner', nextFireUtc: new Date(NOW + 5 * MIN).toISOString() }),
        ]}
        nowMs={NOW}
      />,
    );
    const rows = screen.getAllByRole('row').slice(1); // skip header row
    expect(rows[0]).toHaveTextContent('Sooner');
    expect(rows[0]).toHaveTextContent('in 5:00');
    expect(rows[1]).toHaveTextContent('Later');
    expect(rows[2]).toHaveTextContent('EventDriven');
    expect(rows[2]).toHaveTextContent('—');
    expect(rows[2]).toHaveTextContent('fileWatcherTrigger');
  });

  it('marks past fire times as overdue', () => {
    render(
      <OpsDepartureBoard
        triggers={[trigger({ workflowName: 'Missed', nextFireUtc: new Date(NOW - 2 * MIN).toISOString() })]}
        nowMs={NOW}
      />,
    );
    expect(screen.getByText('overdue')).toBeInTheDocument();
  });

  it('caps the board at 8 rows', () => {
    const many = Array.from({ length: 12 }, (_, i) => trigger({
      workflowId: `wf-${i}`, workflowName: `WF ${i}`, nextFireUtc: new Date(NOW + (i + 1) * MIN).toISOString(),
    }));
    render(<OpsDepartureBoard triggers={many} nowMs={NOW} />);
    expect(screen.getAllByRole('row')).toHaveLength(9); // 1 header + 8 rows
  });
});
