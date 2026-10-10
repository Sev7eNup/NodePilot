import { describe, expect, it } from 'vitest';
import { groupTrace, type TraceDelegation, type TraceEntry, type TraceEvent } from '../../lib/agentRunTrace';
import type { AgentRunEvent } from '../../types/agents';

let sequence = 0;
const event = (memberId: string | null, kind = 'model_started'): AgentRunEvent =>
  ({ agentRunId: 'r', sequence: ++sequence, timestamp: '2026-10-10T10:00:00Z', kind, memberId, toolName: null, content: '' });
const ev = (memberId: string | null): TraceEvent => ({ type: 'event', start: event(memberId) });
const delegation = (): TraceDelegation => ({
  type: 'delegation', start: event('b', 'member_started'), from: 'a', to: 'b', task: 't', reviewer: false, delegationId: 'd', activities: [],
});

describe('groupTrace', () => {
  it('keeps short runs of events as individual entries', () => {
    const entries: TraceEntry[] = [ev('a'), ev('a')];
    expect(groupTrace(entries).map(i => i.kind)).toEqual(['entry', 'entry']);
  });

  it('folds three or more consecutive events of one member into a group', () => {
    const entries: TraceEntry[] = [ev('a'), ev('a'), ev('a'), ev('a')];
    const items = groupTrace(entries);
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({ kind: 'group', memberId: 'a' });
    expect(items[0].kind === 'group' && items[0].entries).toHaveLength(4);
  });

  it('never folds a delegation and splits runs around it', () => {
    const items = groupTrace([ev('a'), ev('a'), ev('a'), delegation(), ev('a'), ev('a')]);
    expect(items.map(i => i.kind)).toEqual(['group', 'entry', 'entry', 'entry']);
  });

  it('starts a new group when the member changes', () => {
    const items = groupTrace([ev('a'), ev('a'), ev('a'), ev('b'), ev('b'), ev('b')]);
    expect(items.map(i => i.kind === 'group' && i.memberId)).toEqual(['a', 'b']);
  });

  it('returns nothing for an empty trace', () => {
    expect(groupTrace([])).toEqual([]);
  });
});
