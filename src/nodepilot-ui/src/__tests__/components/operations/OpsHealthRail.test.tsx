import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OpsHealthRail } from '../../../components/operations/OpsHealthRail';
import type { OpsHeartbeat } from '../../../api/operations';

function hb(serviceName: string, isStale: boolean): OpsHeartbeat {
  return { serviceName, lastHeartbeatAt: '2026-07-19T12:00:00Z', expectedIntervalSeconds: 60, status: null, isStale };
}

describe('OpsHealthRail', () => {
  it('renders the machine fraction and heartbeat list with stale badges', () => {
    render(
      <OpsHealthRail
        machinesTotal={5}
        machinesReachable={4}
        heartbeats={[hb('Scheduler', false), hb('NotificationDispatcher', true)]}
        clusterRole={null}
        reasons={[]}
      />,
    );
    expect(screen.getByText('4/5')).toBeInTheDocument();
    expect(screen.getByText('Scheduler')).toBeInTheDocument();
    expect(screen.getByText('NotificationDispatcher')).toBeInTheDocument();
    expect(screen.getByText('stale')).toBeInTheDocument();
  });

  it('highlights contributors flagged by the pulse reasons', () => {
    const { container } = render(
      <OpsHealthRail
        machinesTotal={3}
        machinesReachable={1}
        heartbeats={[hb('Scheduler', true)]}
        clusterRole={null}
        reasons={['machinesUnreachable', 'staleHeartbeat']}
      />,
    );
    expect(container.querySelectorAll('.np-ops-health--hot')).toHaveLength(2);
  });

  it('renders the cluster role chip only when a role is reported', () => {
    const { rerender } = render(
      <OpsHealthRail machinesTotal={0} machinesReachable={0} heartbeats={[]} clusterRole={null} reasons={[]} />,
    );
    expect(screen.queryByText('Cluster role')).not.toBeInTheDocument();
    rerender(
      <OpsHealthRail machinesTotal={0} machinesReachable={0} heartbeats={[]} clusterRole="leader" reasons={[]} />,
    );
    expect(screen.getByText('Cluster role')).toBeInTheDocument();
    expect(screen.getByText('Leader')).toBeInTheDocument();
  });
});
