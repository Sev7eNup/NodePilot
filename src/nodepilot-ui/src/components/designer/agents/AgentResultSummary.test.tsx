import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { AgentResultSummary } from './AgentResultSummary';
import type { AgentRun, AgentRunEvent } from '../../../types/agents';

afterEach(cleanup);
const run: AgentRun = { id: 'run', workflowExecutionId: 'execution', stepId: 'team', status: 'Succeeded',
  startedAt: '', completedAt: '', result: 'Report', error: null, modelCalls: 3, toolCalls: 1,
  delegations: 1, inputTokens: null, outputTokens: null, outcome: 'partial', outcomeReason: 'Client unreachable.' };
const events: AgentRunEvent[] = [{ agentRunId: 'run', sequence: 1, timestamp: '', kind: 'run_conclusion',
  memberId: 'lead', toolName: null, content: JSON.stringify({ coverage: [
    { requirement: 'Read configuration', status: 'fulfilled', basis: 'Configuration read.' },
    { requirement: 'Verify connectivity', status: 'unresolved', basis: 'Client unavailable.' },
  ] }) }];
const show = (patch: Partial<AgentRun> = {}, journal = events, loading = false, unavailable = false) =>
  render(<AgentResultSummary run={{ ...run, ...patch }} events={journal} loading={loading} unavailable={unavailable} />);

describe('AgentResultSummary', () => {
  it('separates technical completion from a partial task assessment', () => {
    show();
    expect(screen.getByTestId('agent-execution-status')).toHaveTextContent('Technical execution: Completed');
    expect(screen.getByTestId('agent-task-outcome')).toHaveTextContent('Partially completed');
  });
  it('labels retained findings after cancellation as an intermediate report', () => {
    show({ status: 'Cancelled' });
    expect(screen.getByText(/An intermediate report is available/)).toBeVisible();
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
  });
  it('puts unresolved deliverables first with their actual limitations', () => {
    show();
    expect(screen.getAllByRole('listitem')[0]).toHaveTextContent('Verify connectivityClient unavailable.');
    expect(screen.getByText('Client unreachable.')).toBeVisible();
  });
  it.each(['Running', 'Failed', 'Cancelled'])('does not display final claims for a %s run', status => {
    show({ status });
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
    expect(screen.queryByText('Client unreachable.')).not.toBeInTheDocument();
    expect(screen.getByRole('heading')).toHaveTextContent(status === 'Running' ? 'Work in progress' : 'Not finally assessed');
  });
  it('distinguishes missing assessment data from no open work', () => {
    show({}, []);
    expect(screen.getByRole('status')).toHaveTextContent('No structured result checklist');
    expect(screen.queryByText(/Still open/)).not.toBeInTheDocument();
  });
  it('shows loading and read errors explicitly', () => {
    const view = show({}, [], true);
    expect(screen.getByRole('status')).toHaveTextContent('Loading result checklist');
    view.rerender(<AgentResultSummary run={run} events={events} loading={false} unavailable />);
    expect(screen.getByRole('status')).toHaveTextContent('could not be loaded');
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
  });
  it('renders an entirely fulfilled assessment without inventing open work', () => {
    show({ outcome: 'completed' }, [{ ...events[0], content: JSON.stringify({ coverage: [
      { requirement: 'Inspect', status: 'fulfilled', basis: '<script>untrusted</script>' },
    ] }) }]);
    expect(screen.queryByText(/Still open/)).not.toBeInTheDocument();
    expect(screen.getByText('<script>untrusted</script>')).toBeVisible();
    expect(document.querySelector('script')).toBeNull();
  });
});
