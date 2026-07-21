import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { OpsEventTicker } from '../../../components/operations/OpsEventTicker';
import type { OpsNode } from '../../../types/api';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

const NODES = new Map<string, OpsNode>([
  ['w1', { workflowId: 'w1', name: 'Nightly Backup', folderId: 'f', folderPath: '/', isEnabled: true, runningCount: 0, lastStatus: null, callFrequency: null }],
]);

describe('OpsEventTicker', () => {
  it('shows the empty state when no events arrived yet', () => {
    render(<OpsEventTicker events={[]} nodesById={NODES} nowMs={NOW} onSelect={() => {}} />);
    expect(screen.getByText('No live events yet.')).toBeInTheDocument();
  });

  it('renders events newest-first with workflow name, status label and relative time', () => {
    render(
      <OpsEventTicker
        events={[
          { executionId: 'e2', workflowId: 'w1', status: 'Failed', atMs: NOW - 10_000 },
          { executionId: 'e1', workflowId: 'w1', status: 'Running', atMs: NOW - 3 * MIN },
        ]}
        nodesById={NODES}
        nowMs={NOW}
        onSelect={() => {}}
      />,
    );
    const items = screen.getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Failed');
    expect(items[0]).toHaveTextContent('just now');
    expect(items[1]).toHaveTextContent('Running');
    expect(items[1]).toHaveTextContent('3 min ago');
    expect(screen.getAllByText('Nightly Backup')).toHaveLength(2);
  });

  it('falls back to a shortened execution id for unknown workflows', () => {
    render(
      <OpsEventTicker
        events={[{ executionId: 'abcdef1234567890', workflowId: 'ghost', status: 'Succeeded', atMs: NOW }]}
        nodesById={NODES}
        nowMs={NOW}
        onSelect={() => {}}
      />,
    );
    expect(screen.getByText('abcdef12')).toBeInTheDocument();
  });

  it('clicking an event selects its execution', () => {
    const onSelect = vi.fn();
    render(
      <OpsEventTicker
        events={[{ executionId: 'e1', workflowId: 'w1', status: 'Succeeded', atMs: NOW }]}
        nodesById={NODES}
        nowMs={NOW}
        onSelect={onSelect}
      />,
    );
    fireEvent.click(screen.getByRole('button'));
    expect(onSelect).toHaveBeenCalledWith('e1');
  });
});
