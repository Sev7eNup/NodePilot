import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { OpsTimeline } from '../../../components/operations/OpsTimeline';
import type { OpsNode } from '../../../types/api';

const NOW = Date.parse('2026-07-19T12:00:00Z');
const MIN = 60_000;

function node(workflowId: string, name: string): OpsNode {
  return { workflowId, name, folderId: 'f1', folderPath: '/ops', isEnabled: true, runningCount: 0, lastStatus: null, callFrequency: null, canRun: true, canEdit: true };
}

const NODES = new Map([['w1', node('w1', 'Nightly Backup')], ['w2', node('w2', 'Report Gen')]]);

function renderTimeline(overrides: Partial<Parameters<typeof OpsTimeline>[0]> = {}) {
  const onSelect = vi.fn();
  render(
    <OpsTimeline
      nowMs={NOW}
      running={[{ executionId: 'run-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }]}
      recent={[{ executionId: 'done-1', workflowId: 'w2', status: 'Failed', startedAt: new Date(NOW - 10 * MIN).toISOString(), completedAt: new Date(NOW - 8 * MIN).toISOString(), parentExecutionId: null }]}
      density={[]}
      locallySettled={{}}
      scopedWorkflowIds={new Set(['w1', 'w2'])}
      nodesById={NODES}
      selectedExecutionId={null}
      nextStart={null}
      overdueMs={10 * MIN}
      windowMs={20 * MIN}
      historyFromMs={null}
      recentSinceMs={NOW - 20 * MIN}
      densityBucketSeconds={0}
      densityCapped={false}
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
        { executionId: 'run-a', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 4 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
        { executionId: 'run-b', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
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
        { executionId: 'parent-1', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 5 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
        { executionId: 'child-1', workflowId: 'w2', status: 'Running', startedAt: new Date(NOW - 3 * MIN).toISOString(), parentExecutionId: 'parent-1', stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null },
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

describe('OpsTimeline — overdue runs', () => {
  it('marks a Running bar past the threshold and lifts it into the stuck strip', () => {
    renderTimeline({
      running: [{ executionId: 'run-old', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    const bar = screen.getByTitle(/Nightly Backup · Running/);
    expect(bar.className).toContain('np-ops-bar--overdue');
    // The strip is the point: a 3-hour hang is clamped to the window edge and otherwise
    // looks identical to a 21-minute run.
    expect(screen.getByLabelText('Stuck / long-running')).toBeInTheDocument();
  });

  it('leaves a young run unmarked and renders no strip', () => {
    renderTimeline();
    const bar = screen.getByTitle(/Nightly Backup · Running/);
    expect(bar.className).not.toContain('np-ops-bar--overdue');
    expect(screen.queryByLabelText('Stuck / long-running')).not.toBeInTheDocument();
  });

  it('states the real start time on a bar clipped at the window edge', () => {
    renderTimeline({
      running: [{ executionId: 'run-old', workflowId: 'w1', status: 'Running', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    expect(screen.getByTitle(/Nightly Backup · Running/).textContent).toMatch(/‹ \d{2}:\d{2}/);
  });

  it('does not mark a long-queued Pending run as overdue', () => {
    renderTimeline({
      running: [{ executionId: 'run-pending', workflowId: 'w1', status: 'Pending', startedAt: new Date(NOW - 3 * 60 * MIN).toISOString(), parentExecutionId: null, stepsFinished: null, lastCompletedStepName: null, lastProgressAt: null, activeStepCount: null }],
      recent: [],
    });
    expect(screen.queryByLabelText('Stuck / long-running')).not.toBeInTheDocument();
  });
});

describe('OpsTimeline — density for the stretch bars cannot reach', () => {
  // The 4 h window on a busy system: ~4000 finished runs exist, the snapshot ships the newest
  // 1000 as bars and the rest as counts. Before this, everything older than the newest ~30 min
  // was an empty hatched band and the window selector was decoration.
  const DENSITY_ARGS = {
    running: [],
    recent: [{
      executionId: 'newest', workflowId: 'w1', status: 'Succeeded',
      startedAt: new Date(NOW - 31 * MIN).toISOString(),
      completedAt: new Date(NOW - 30 * MIN).toISOString(), parentExecutionId: null,
    }],
    windowMs: 240 * MIN,
    recentSinceMs: NOW - 240 * MIN,
    historyFromMs: NOW - 30 * MIN,   // seam: bars start here, density covers what is left of it
    densityBucketSeconds: 300,
    density: [{
      workflowId: 'w1',
      buckets: [
        { bucketIndex: 3, total: 12, failed: 0, cancelled: 0 },
        { bucketIndex: 4, total: 20, failed: 3, cancelled: 1 },
      ],
    }],
  };

  it('draws one cell per bucket left of the seam and names its runs in the tooltip', () => {
    renderTimeline(DENSITY_ARGS);
    const cells = screen.getAllByTestId('ops-density-cell');
    expect(cells).toHaveLength(2);
    expect(cells[1].getAttribute('title')).toContain('20 runs');
    expect(cells[1].getAttribute('title')).toContain('3 failed');
    expect(cells[1].getAttribute('title')).toContain('1 cancelled');
  });

  it('omits the zero counts from a clean bucket rather than printing "0 failed"', () => {
    renderTimeline(DENSITY_ARGS);
    const cells = screen.getAllByTestId('ops-density-cell');
    expect(cells[0].getAttribute('title')).toContain('12 runs');
    expect(cells[0].getAttribute('title')).not.toContain('failed');
  });

  it('states the window total and suppresses the "no history" band', () => {
    renderTimeline(DENSITY_ARGS);
    // 12 + 20 counted, 3 of them failed — the answer to "what happened in these four hours?".
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('32 finished runs');
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('3 failed');
    // The band claims nothing came back for that stretch; density is the refutation.
    expect(screen.queryByTestId('ops-history-gap')).not.toBeInTheDocument();
  });

  it('says so when the aggregate itself was capped', () => {
    renderTimeline({ ...DENSITY_ARGS, densityCapped: true });
    expect(screen.getByTestId('ops-density-notice').textContent).toContain('More than');
  });

  it('gives a workflow with density but no bar a lane of its own', () => {
    // Every run of w2 fell past the raw cap. Without a lane its history would have nowhere to
    // draw and the workflow would read as idle — the exact misreading density exists to prevent.
    renderTimeline({
      ...DENSITY_ARGS,
      density: [{ workflowId: 'w2', buckets: [{ bucketIndex: 5, total: 7, failed: 0, cancelled: 0 }] }],
    });
    expect(screen.getByText('Report Gen')).toBeInTheDocument();
    expect(screen.getAllByTestId('ops-density-cell')).toHaveLength(1);
  });

  it('drops density for out-of-scope workflows, notice included', () => {
    // w2 stays visible (so the lane view still renders) but w1's density must not leak into a
    // folder-filtered board — nor into the run total the notice states.
    renderTimeline({
      ...DENSITY_ARGS,
      scopedWorkflowIds: new Set(['w2']),
      recent: [{
        executionId: 'w2-run', workflowId: 'w2', status: 'Succeeded',
        startedAt: new Date(NOW - 31 * MIN).toISOString(),
        completedAt: new Date(NOW - 30 * MIN).toISOString(), parentExecutionId: null,
      }],
    });
    expect(screen.getByTestId('ops-timeline')).toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-cell')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-notice')).not.toBeInTheDocument();
  });

  it('renders nothing extra on a quiet snapshot', () => {
    renderTimeline();
    expect(screen.queryByTestId('ops-density-cell')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ops-density-notice')).not.toBeInTheDocument();
  });
});

describe('OpsTimeline — duration stays comparable at wide windows', () => {
  // Regression: the bar-width floor used to be 6 px. At 1 h a 2-minute run renders ~9 px and a
  // 20-second one ~3 px, so the floor flattened both to the same length — every bar in the
  // 1 h / 4 h views looked identical and "which run took longer?" was unanswerable.
  const wideRuns = {
    running: [],
    recent: [
      { executionId: 'long', workflowId: 'w1', status: 'Succeeded',
        startedAt: new Date(NOW - 12 * MIN).toISOString(),
        completedAt: new Date(NOW - 10 * MIN).toISOString(), parentExecutionId: null },
      { executionId: 'short', workflowId: 'w2', status: 'Succeeded',
        startedAt: new Date(NOW - 6 * MIN).toISOString(),
        completedAt: new Date(NOW - 6 * MIN + 20_000).toISOString(), parentExecutionId: null },
    ],
  };

  it('writes the duration beside bars too narrow to hold it', () => {
    renderTimeline({ ...wideRuns, windowMs: 240 * MIN });
    // Both runs are sub-pixel-ish at 4 h, so neither can carry an inside label — the text
    // beside the bar is what keeps them distinguishable.
    expect(screen.getByText('2:00')).toBeInTheDocument();
    expect(screen.getByText('0:20')).toBeInTheDocument();
  });

  it('does not duplicate the duration when it already fits inside the bar', () => {
    renderTimeline({
      running: [],
      recent: [{
        executionId: 'wide', workflowId: 'w1', status: 'Succeeded',
        startedAt: new Date(NOW - 12 * MIN).toISOString(),
        completedAt: new Date(NOW - 2 * MIN).toISOString(), parentExecutionId: null,
      }],
      windowMs: 20 * MIN,
    });
    expect(screen.getAllByText('10:00')).toHaveLength(1);
  });
});
