import { describe, expect, it } from 'vitest';
import { agentRunTrace, runMembers, traceMatches, traceOutcome } from './agentRunTrace';
import type { AgentRun, AgentRunEvent } from '../types/agents';
import { memberStatus } from './agentTeamProjection';

const event = (sequence: number, kind: string, memberId: string | null = 'lead', toolName: string | null = null, content = ''): AgentRunEvent =>
  ({ agentRunId: 'run', sequence, timestamp: `2026-09-20T12:00:${String(sequence).padStart(2, '0')}Z`, kind, memberId, toolName, content });
const run = { status: 'Running' } as AgentRun;
const input = JSON.stringify({ delegationId: 'd1', from: 'lead', task: 'Check the endpoint', reason: 'The cause is not yet supported' });

describe('agent run communication trace', () => {
  it('correlates interleaved parallel members by delegation id', () => {
    const start = (id: string) => JSON.stringify({ delegationId: id, from: 'lead', task: id, batchId: 'batch', batchSize: 2 });
    const end = (id: string) => JSON.stringify({ delegationId: id, status: 'completed', content: `Answer ${id}` });
    const trace = agentRunTrace([event(1, 'member_started', 'a', null, start('a1')), event(2, 'member_started', 'b', null, start('b1')),
      event(3, 'tool_started', 'a', 'read'), event(4, 'tool_started', 'b', 'read'), event(5, 'tool_completed', 'b', 'read', 'B'),
      event(6, 'team_board', 'b', null, '{"ids":["ev-1"]}'), event(7, 'member_completed', 'b', null, end('b1')),
      event(8, 'tool_completed', 'a', 'read', 'A'), event(9, 'member_completed', 'a', null, end('a1'))]);
    expect(trace).toHaveLength(2);
    expect(trace[0]).toMatchObject({ to: 'a', batchSize: 2, response: 'Answer a1', activities: [{ start: { sequence: 3 }, end: { sequence: 8 } }] });
    expect(trace[1]).toMatchObject({ to: 'b', response: 'Answer b1', activities: [{ start: { sequence: 4 }, end: { sequence: 5 } }, { start: { kind: 'team_board' } }] });
  });
  it('pairs repeated handoffs, replies and their own tool evidence without mixing sessions', () => {
    const events = [event(2, 'member_started', 'review', null, input),
      event(3, 'model_started', 'review'), event(4, 'model_completed', 'review'),
      event(5, 'tool_started', 'review', 'powershell', '{"script":"Get-Service"}'), event(6, 'tool_completed', 'review', 'powershell', 'service stopped'),
      event(8, 'member_needs_input', 'review', null, JSON.stringify({ delegationId: 'd1', status: 'needs_input', content: 'Check policy', objectionKind: 'evidence' })),
      event(9, 'member_started', 'review', null, input.replace('d1', 'd2')), event(10, 'member_completed', 'review', null, '{"delegationId":"d2","status":"completed","content":"Policy checked"}')];
    const trace = agentRunTrace(events);
    expect(trace).toHaveLength(2);
    expect(trace[0]).toMatchObject({ type: 'delegation', from: 'lead', to: 'review', status: 'needs_input', response: 'Check policy', reason: 'The cause is not yet supported',
      activities: [{ start: { sequence: 3 }, end: { sequence: 4 } }, { start: { sequence: 5 }, end: { sequence: 6, content: 'service stopped' } }] });
    expect(trace[1]).toMatchObject({ start: { sequence: 9 }, end: { sequence: 10 }, status: 'completed', activities: [] });
    expect(traceMatches(trace[0], 'lead')).toBe(true);
    expect(traceMatches(trace[0], 'review')).toBe(true);
    expect(traceMatches(trace[0], 'other')).toBe(false);
  });

  it('does not show a timed out delegation or tool as still running', () => {
    const trace = agentRunTrace([event(1, 'member_started', 'review', null, input), event(2, 'model_started', 'review'),
      event(3, 'model_failed', 'review', null, 'Model timeout'), event(4, 'run_failed', null, null, 'Model timeout')]);
    expect(traceOutcome(trace[0], run)).toBe('running');
    expect(traceOutcome(trace[0], { ...run, status: 'Failed' })).toBe('interrupted');
    expect(trace[0]).toMatchObject({ activities: [{ end: { kind: 'model_failed' } }] });
    expect(trace[1]).toMatchObject({ start: { kind: 'run_failed' } });
    expect(memberStatus([event(1, 'member_started', 'review'), event(2, 'model_failed', 'review')], 'review')).toBe('Failed');
    expect(memberStatus([event(1, 'member_started', 'review'), event(2, 'run_cancelled', null)], 'review')).toBe('Cancelled');
  });

  it('uses the recorded member snapshot, while retaining IDs for events without a snapshot', () => {
    const events = [event(1, 'run_context', null, null, JSON.stringify({ members: [{ id: 'review', role: 'Evidence review', function: 'reviewer', model: 'model-at-run-time' }] })),
      event(2, 'model_started', 'review'), event(3, 'model_started', 'legacy')];
    expect(runMembers(events)).toEqual([{ id: 'review', role: 'Evidence review', function: 'reviewer', model: 'model-at-run-time', targetMachineId: undefined }, { id: 'legacy', role: 'legacy' }]);
    expect(agentRunTrace(events)).toHaveLength(2);
  });

  it('retains malformed, truncated and unfamiliar events in the trace', () => {
    const trace = agentRunTrace([event(1, 'tool_started', 'lead', 'delegate', '{broken'), event(2, 'future_event', null, null, '<script>data</script>')]);
    expect(trace).toHaveLength(2);
    expect(trace[0]).toMatchObject({ type: 'event', start: { content: '{broken' } });
    expect(trace[1]).toMatchObject({ start: { content: '<script>data</script>' } });
  });
});
