import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { WorkflowInfoCard } from '../../../components/designer/WorkflowInfoCard';
import { formatDuration } from '../../../lib/format';
import type { Workflow } from '../../../types/api';

// We never assert on formatRelative() output (it is "now"-relative and would be
// time-dependent); only stable, deterministic fields are checked.
function makeWorkflow(overrides: Partial<Workflow> = {}): Workflow {
  return {
    id: 'wf-1',
    name: 'Nightly Backup',
    description: null,
    definitionJson: '{}',
    version: 7,
    isEnabled: true,
    createdAt: '2026-06-20T10:00:00Z',
    updatedAt: '2026-06-23T09:00:00Z',
    createdBy: 'admin',
    updatedBy: 'admin',
    activityCount: 12,
    triggerTypes: ['scheduleTrigger'],
    lastExecution: {
      id: 'e1', status: 'Succeeded', startedAt: '2026-06-23T09:00:00Z',
      completedAt: '2026-06-23T09:00:05Z', durationMs: 4200,
    },
    successCount: 18,
    totalCount: 20,
    avgDurationMs: 5100,
    checkedOutByUserName: null,
    checkedOutAt: null,
    folderPath: '/VRZ/Maintenance',
    ...overrides,
  } as Workflow;
}

describe('WorkflowInfoCard', () => {
  it('null_rendersEmptyPlaceholder', () => {
    render(<WorkflowInfoCard workflow={null} />);
    expect(screen.getByText('Hover a workflow to see details.')).toBeInTheDocument();
  });

  it('renders_coreFields', () => {
    render(<WorkflowInfoCard workflow={makeWorkflow()} />);
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
    expect(screen.getByText('v7')).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();            // steps
    expect(screen.getByText('Schedule')).toBeInTheDocument();      // localized trigger label
    expect(screen.getByText('18/20')).toBeInTheDocument();         // success
    expect(screen.getByText(formatDuration(5100))).toBeInTheDocument(); // avg duration
  });

  it('lastExecutionNull_hidesLastRunRow_butKeepsRest', () => {
    render(<WorkflowInfoCard workflow={makeWorkflow({ lastExecution: null })} />);
    expect(screen.queryByText('Last run')).not.toBeInTheDocument();
    expect(screen.getByText('Nightly Backup')).toBeInTheDocument();
  });

  it('checkedOut_showsLockedBy_andNullCheckedOutAtDoesNotThrow', () => {
    render(<WorkflowInfoCard workflow={makeWorkflow({ checkedOutByUserName: 'alice', checkedOutAt: null })} />);
    expect(screen.getByText('Locked by')).toBeInTheDocument();
    expect(screen.getByText('alice')).toBeInTheDocument();
  });

  it('noLock_hidesLockedByRow', () => {
    render(<WorkflowInfoCard workflow={makeWorkflow()} />);
    expect(screen.queryByText('Locked by')).not.toBeInTheDocument();
  });
});
