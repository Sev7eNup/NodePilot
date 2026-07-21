import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { OpsPulseHeader } from '../../../components/operations/OpsPulseHeader';

const NOW = Date.parse('2026-07-19T12:34:56Z');

describe('OpsPulseHeader', () => {
  it('renders the nominal state word with counts and the live clock', () => {
    render(<OpsPulseHeader reasons={[]} state="nominal" runningCount={3} pendingCount={1} longRunningCount={0} nowMs={NOW} />);
    expect(screen.getByRole('status')).toHaveTextContent('Operations nominal');
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('1')).toBeInTheDocument();
    expect(screen.getByText(new Date(NOW).toLocaleTimeString())).toBeInTheDocument();
  });

  it('hides the long-running chip at zero and shows it when > 0', () => {
    const { rerender } = render(<OpsPulseHeader reasons={[]} state="nominal" runningCount={0} pendingCount={0} longRunningCount={0} nowMs={NOW} />);
    expect(screen.queryByText('long-running')).not.toBeInTheDocument();
    rerender(<OpsPulseHeader reasons={[]} state="degraded" runningCount={0} pendingCount={0} longRunningCount={2} nowMs={NOW} />);
    expect(screen.getByText('long-running')).toBeInTheDocument();
  });

  it('lists the contributing reasons next to the state word', () => {
    render(<OpsPulseHeader reasons={['allMachinesUnreachable', 'staleHeartbeat']} state="incident" runningCount={0} pendingCount={0} longRunningCount={0} nowMs={NOW} />);
    expect(screen.getByText(/no machine reachable/)).toBeInTheDocument();
    expect(screen.getByText(/stale service heartbeat/)).toBeInTheDocument();
  });

  it.each([
    ['degraded', 'Degraded', 'np-ops-pulse--degraded'],
    ['incident', 'Incident', 'np-ops-pulse--incident'],
  ] as const)('renders the %s state with its banner class', (state, label, cls) => {
    const { container } = render(<OpsPulseHeader reasons={[]} state={state} runningCount={0} pendingCount={0} longRunningCount={0} nowMs={NOW} />);
    expect(screen.getByRole('status')).toHaveTextContent(label);
    expect(container.querySelector(`.${cls}`)).not.toBeNull();
  });
});
