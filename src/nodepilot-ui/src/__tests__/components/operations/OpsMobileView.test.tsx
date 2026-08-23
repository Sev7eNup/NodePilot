import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { OpsMobileView } from '../../../components/operations/OpsMobileView';
import type { OpsNode, OpsRecentExecution, OpsRunningExecution } from '../../../types/api';

const NOW = Date.parse('2026-08-23T12:00:00Z');
const MIN = 60_000;
const OVERDUE_MS = 10 * MIN;

const NODES: OpsNode[] = [
  { workflowId: 'wf-1', name: 'Nightly Backup', folderId: 'f1', folderPath: '/Ops', isEnabled: true, runningCount: 1, lastStatus: 'Running', callFrequency: null, canRun: true, canEdit: true },
  { workflowId: 'wf-2', name: 'Report Gen', folderId: '', folderPath: '/', isEnabled: true, runningCount: 0, lastStatus: 'Succeeded', callFrequency: null, canRun: true, canEdit: false },
];
const nodesById = new Map(NODES.map((n) => [n.workflowId, n]));
const scoped = new Set(['wf-1', 'wf-2']);

function running(p: Partial<OpsRunningExecution> & { executionId: string }): OpsRunningExecution {
  return {
    workflowId: 'wf-1', status: 'Running',
    startedAt: new Date(NOW - MIN).toISOString(),
    parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null,
    activeStepCount: null,
    ...p,
  };
}

function recent(p: Partial<OpsRecentExecution> & { executionId: string }): OpsRecentExecution {
  return {
    workflowId: 'wf-2', status: 'Succeeded',
    startedAt: new Date(NOW - 3 * MIN).toISOString(),
    completedAt: new Date(NOW - 2 * MIN).toISOString(),
    parentExecutionId: null,
    ...p,
  };
}

function renderView(over: Partial<Parameters<typeof OpsMobileView>[0]> = {}) {
  const onSelect = vi.fn();
  render(
    <OpsMobileView
      nowMs={NOW}
      running={[]}
      recent={[]}
      locallySettled={{}}
      scopedWorkflowIds={scoped}
      nodesById={nodesById}
      overdueMs={OVERDUE_MS}
      selectedExecutionId={null}
      onSelect={onSelect}
      {...over}
    />,
  );
  return { onSelect };
}

describe('OpsMobileView', () => {
  it('says the board is idle when nothing is running', () => {
    renderView();
    expect(screen.getByText('Nothing is running right now.')).toBeInTheDocument();
  });

  it('lists a live run with its name, elapsed time and last finished step', () => {
    renderView({
      running: [running({
        executionId: 'ex-1',
        startedAt: new Date(NOW - 90_000).toISOString(),
        lastCompletedStepName: 'Copy files',
        lastProgressAt: new Date(NOW - 20_000).toISOString(),
      })],
    });

    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('running for 1:30')).toBeInTheDocument();
    expect(screen.getByText('last step 0:20 ago: Copy files')).toBeInTheDocument();
    expect(screen.getByText('1 running')).toBeInTheDocument();
  });

  it('lifts an overdue run into its own section and counts it as stuck', () => {
    renderView({
      running: [
        running({ executionId: 'ex-old', startedAt: new Date(NOW - 42 * MIN).toISOString() }),
        running({ executionId: 'ex-new', workflowId: 'wf-2' }),
      ],
    });

    const stuck = screen.getByRole('region', { name: 'Stuck / long-running' });
    expect(within(stuck).getByText('running for 42:00')).toBeInTheDocument();
    expect(within(stuck).queryByText('Report Gen')).not.toBeInTheDocument();
    // The counter still counts it among the running — it is one, it is just also overdue.
    expect(screen.getByText('2 running')).toBeInTheDocument();
    expect(screen.getByText('1 stuck')).toBeInTheDocument();
  });

  it('puts the longest-running live run first', () => {
    renderView({
      running: [
        running({ executionId: 'ex-young', workflowId: 'wf-2', startedAt: new Date(NOW - MIN).toISOString() }),
        running({ executionId: 'ex-older', workflowId: 'wf-1', startedAt: new Date(NOW - 5 * MIN).toISOString() }),
      ],
    });

    const section = screen.getByRole('region', { name: 'Running' });
    const names = within(section).getAllByRole('button').map((b) => b.textContent ?? '');
    expect(names[0]).toContain('Nightly Backup');
    expect(names[1]).toContain('Report Gen');
  });

  it('shows finished runs newest first, with outcome, duration and how long ago', () => {
    renderView({
      recent: [
        recent({ executionId: 'ex-a', completedAt: new Date(NOW - 5 * MIN).toISOString() }),
        recent({ executionId: 'ex-b', workflowId: 'wf-1', completedAt: new Date(NOW - MIN).toISOString() }),
      ],
    });

    const section = screen.getByRole('region', { name: 'Just finished' });
    const rows = within(section).getAllByRole('button').map((b) => b.textContent ?? '');
    expect(rows[0]).toContain('Nightly Backup');
    expect(rows[0]).toContain('1:00 ago');
    expect(rows[0]).toContain('Succeeded');
    expect(rows[1]).toContain('Report Gen');
  });

  // The regression this section exists for: on a busy estate "just finished" is thousands long,
  // so a failure never survived the cap and the counter pointed at something unreachable.
  it('gives failures their own section so a busy success list cannot bury them', () => {
    renderView({
      recent: [
        ...Array.from({ length: 30 }, (_, i) => recent({
          executionId: `ok-${i}`,
          completedAt: new Date(NOW - (i + 1) * 1000).toISOString(),
        })),
        recent({ executionId: 'ex-bad', status: 'Failed', workflowId: 'wf-1', completedAt: new Date(NOW - 20 * MIN).toISOString() }),
      ],
    });

    const failedSection = screen.getByRole('region', { name: 'Failed' });
    expect(within(failedSection).getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('1 failed')).toBeInTheDocument();
    // ...and it is not repeated among the successes.
    const finished = screen.getByRole('region', { name: 'Just finished' });
    expect(within(finished).queryByText('Nightly Backup')).not.toBeInTheDocument();
  });

  it('counts a timed-out run as failed and leaves a cancelled one in the general list', () => {
    renderView({
      recent: [
        recent({ executionId: 'ex-timeout', status: 'TimedOut', workflowId: 'wf-1' }),
        recent({ executionId: 'ex-cancelled', status: 'Cancelled' }),
      ],
    });

    const failedSection = screen.getByRole('region', { name: 'Failed' });
    expect(within(failedSection).getByText('Nightly Backup')).toBeInTheDocument();
    // A cancellation was somebody's decision, not an incident — it stays where it happened.
    const finished = screen.getByRole('region', { name: 'Just finished' });
    expect(within(finished).getByText('Report Gen')).toBeInTheDocument();
  });

  it('names how many failures it left out instead of truncating silently', () => {
    renderView({
      recent: Array.from({ length: 13 }, (_, i) => recent({
        executionId: `bad-${i}`,
        status: 'Failed',
        completedAt: new Date(NOW - (i + 1) * MIN).toISOString(),
      })),
    });

    const section = screen.getByRole('region', { name: 'Failed' });
    expect(within(section).getAllByRole('button')).toHaveLength(10);
    expect(screen.getByText('+3 more failed')).toBeInTheDocument();
  });

  it('omits the folder line for root-folder workflows but keeps a real one', () => {
    renderView({
      running: [
        running({ executionId: 'ex-1' }),
        running({ executionId: 'ex-2', workflowId: 'wf-2' }),
      ],
    });

    expect(screen.getByText('/Ops')).toBeInTheDocument();
    expect(screen.queryByText('/')).not.toBeInTheDocument();
  });

  it('names how many finished runs it left out instead of truncating silently', () => {
    renderView({
      recent: Array.from({ length: 14 }, (_, i) => recent({
        executionId: `ex-${i}`,
        completedAt: new Date(NOW - (i + 1) * MIN).toISOString(),
      })),
    });

    const section = screen.getByRole('region', { name: 'Just finished' });
    expect(within(section).getAllByRole('button')).toHaveLength(10);
    expect(screen.getByText('+4 more finished')).toBeInTheDocument();
  });

  it('opens the drilldown for the tapped run', () => {
    const { onSelect } = renderView({ running: [running({ executionId: 'ex-1' })] });

    fireEvent.click(screen.getByText('Nightly Backup'));

    expect(onSelect).toHaveBeenCalledWith('ex-1');
  });

  it('ignores runs outside the current folder scope', () => {
    renderView({
      running: [running({ executionId: 'ex-1', workflowId: 'wf-elsewhere' })],
      scopedWorkflowIds: new Set(['wf-1']),
    });

    expect(screen.getByText('Nothing is running right now.')).toBeInTheDocument();
  });
});
