import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { OpsTimeline } from '../../../components/operations/OpsTimeline';
import type { OpsNode } from '../../../types/api';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function node(workflowId: string, name: string): OpsNode {
  return { workflowId, name, folderId: 'f1', folderPath: '/ops', isEnabled: true, runningCount: 0, lastStatus: null, callFrequency: null };
}

const NODES = new Map([['w1', node('w1', 'Nightly Backup')], ['w2', node('w2', 'Report Gen')]]);

function renderTimeline(overrides: Partial<Parameters<typeof OpsTimeline>[0]> = {}) {
  const onSelect = vi.fn();
  render(
    <OpsTimeline
      nowMs={NOW}
      running={[{ executionId: 'run-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null }]}
      recent={[{ executionId: 'done-1', workflowId: 'w2', status: 'Failed', startedAt: new Date(NOW - 10 * MIN).toISOString(), completedAt: new Date(NOW - 8 * MIN).toISOString(), parentExecutionId: null }]}
      locallySettled={{}}
      scopedWorkflowIds={new Set(['w1', 'w2'])}
      nodesById={NODES}
      selectedExecutionId={null}
      nextStart={null}
      onSelect={onSelect}
      {...overrides}
    />,
  );
  return { onSelect };
}

describe('OpsTimeline', () => {
  it('renders a lane per workflow with name + folder and one bar per execution', () => {
    renderTimeline();
    expect(screen.getByTestId('ops-timeline')).toBeInTheDocument();
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('Report Gen')).toBeInTheDocument();
    expect(screen.getAllByText('/ops')).toHaveLength(2);
    // One bar button per execution, labeled workflow + status.
    expect(screen.getByTitle(/Nightly Backup · Running/)).toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
    // NOW marker on the axis.
    expect(screen.getByText('Now')).toBeInTheDocument();
  });

  it('clicking a bar selects its execution', () => {
    const { onSelect } = renderTimeline();
    fireEvent.click(screen.getByTitle(/Nightly Backup · Running/));
    expect(onSelect).toHaveBeenCalledWith('run-1');
  });

  it('shows a copyable execution-id chip next to each workflow name in the lane label', () => {
    renderTimeline();
    // 8-char prefix of the execution id, in parens, behind the workflow name.
    expect(screen.getByText('(run-1)')).toBeInTheDocument();
    expect(screen.getByText('(done-1)')).toBeInTheDocument();
  });

  it('renders each overlapping run as its own labeled row instead of a ×N badge', () => {
    // Two concurrent runs of the same workflow stack into two sub-rows; each sub-row gets
    // its own full-name label + job-id chip, and no ×2 multiplier is shown.
    renderTimeline({
      running: [
        { executionId: 'run-a', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null },
        { executionId: 'run-b', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: null },
      ],
      recent: [],
    });
    expect(screen.getAllByText('Nightly Backup')).toHaveLength(2);
    expect(screen.queryByText(/×2/)).not.toBeInTheDocument();
    expect(screen.getByText('(run-a)')).toBeInTheDocument();
    expect(screen.getByText('(run-b)')).toBeInTheDocument();
  });

  it('marks the selected bar', () => {
    renderTimeline({ selectedExecutionId: 'run-1' });
    expect(screen.getByTitle(/Nightly Backup · Running/)).toHaveAttribute('aria-pressed', 'true');
  });

  it('renders locally-settled runs not yet confirmed by the snapshot', () => {
    renderTimeline({
      running: [],
      recent: [],
      locallySettled: {
        'fresh-1': { workflowId: 'w1', status: 'Succeeded', settledAtMs: NOW - 1000, startedAtMs: NOW - 2 * MIN },
      },
    });
    expect(screen.getByTitle(/Nightly Backup · Succeeded/)).toBeInTheDocument();
  });

  it('shows the idle hero with the next scheduled start when nothing is in the window', () => {
    renderTimeline({
      running: [], recent: [], locallySettled: {},
      nextStart: { name: 'Inventory Sync', atMs: NOW + 15 * MIN },
    });
    expect(screen.getByText('Nothing is running right now.')).toBeInTheDocument();
    expect(screen.getByText(/Inventory Sync/)).toBeInTheDocument();
    expect(screen.queryByTestId('ops-timeline')).not.toBeInTheDocument();
  });

  it('draws a call connector between a parent bar and its sub-workflow bar', () => {
    renderTimeline({
      running: [
        { executionId: 'parent-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 5 * MIN).toISOString(), parentExecutionId: null },
        { executionId: 'child-1', workflowId: 'w2', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: 'parent-1' },
      ],
      recent: [],
    });
    expect(screen.getByTestId('ops-connectors').querySelectorAll('.np-ops-connector')).toHaveLength(1);
  });

  it('renders no connector layer when no visible parent/child pair exists', () => {
    renderTimeline();
    expect(screen.queryByTestId('ops-connectors')).not.toBeInTheDocument();
  });

  it('filters out-of-scope workflows', () => {
    renderTimeline({ scopedWorkflowIds: new Set(['w2']) });
    expect(screen.queryByTitle(/Nightly Backup/)).not.toBeInTheDocument();
    expect(screen.getByTitle(/Report Gen · Failed/)).toBeInTheDocument();
  });
});
