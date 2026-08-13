import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OpsDepartureBoard } from '../../../components/operations/OpsDepartureBoard';
import type { OpsArmedTrigger } from '../../../api/operations';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function trigger(p: Partial<OpsArmedTrigger>): OpsArmedTrigger {
  return {
    workflowId: 'wf', workflowName: 'WF', triggerTypes: ['scheduleTrigger'],
    nextFireUtc: null, nextFireKind: null, pollIntervalSeconds: null,
    blockedByWindowName: null, ...p,
  };
}

describe('OpsDepartureBoard', () => {
  it('is the next named keyboard stop after the timeline', () => {
    render(<OpsDepartureBoard triggers={[]} nowMs={NOW} />);
    expect(screen.getByRole('region', { name: 'Next starts' })).toHaveAttribute('tabindex', '0');
  });

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

  it('marks a start that an active maintenance window will suppress', () => {
    render(
      <OpsDepartureBoard
        triggers={[trigger({
          workflowName: 'Nightly Backup',
          nextFireUtc: new Date(NOW + 10 * MIN).toISOString(),
          blockedByWindowName: 'Weekend Freeze',
        })]}
        nowMs={NOW}
      />,
    );
    // The blackout label replaces the countdown — the board must not promise a start
    // that will never happen.
    expect(screen.getByText('maintenance')).toBeInTheDocument();
    expect(screen.queryByText('in 10:00')).not.toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Nightly Backup/ }))
      .toHaveAttribute('title', 'Suppressed by maintenance window “Weekend Freeze”');
  });

  it('keeps a blocked row in its fire-time sort position instead of hiding or demoting it', () => {
    render(
      <OpsDepartureBoard
        triggers={[
          trigger({ workflowId: 'a', workflowName: 'Later', nextFireUtc: new Date(NOW + 30 * MIN).toISOString() }),
          trigger({
            workflowId: 'b', workflowName: 'BlockedSooner',
            nextFireUtc: new Date(NOW + 5 * MIN).toISOString(),
            blockedByWindowName: 'Weekend Freeze',
          }),
        ]}
        nowMs={NOW}
      />,
    );
    const rows = screen.getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('BlockedSooner');
    expect(rows[1]).toHaveTextContent('Later');
  });

  it('leaves unblocked rows on the normal countdown', () => {
    render(
      <OpsDepartureBoard
        triggers={[trigger({ workflowName: 'Free', nextFireUtc: new Date(NOW + 5 * MIN).toISOString() })]}
        nowMs={NOW}
      />,
    );
    expect(screen.getByText('in 5:00')).toBeInTheDocument();
    expect(screen.queryByText('maintenance')).not.toBeInTheDocument();
  });

  it('caps the board at 8 rows', () => {
    const many = Array.from({ length: 12 }, (_, i) => trigger({
      workflowId: `wf-${i}`, workflowName: `WF ${i}`, nextFireUtc: new Date(NOW + (i + 1) * MIN).toISOString(),
    }));
    render(<OpsDepartureBoard triggers={many} nowMs={NOW} />);
    expect(screen.getAllByRole('row')).toHaveLength(9); // 1 header + 8 rows
  });
});
