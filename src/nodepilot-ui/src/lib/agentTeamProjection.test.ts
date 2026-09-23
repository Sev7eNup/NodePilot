import { describe, expect, it } from 'vitest';
import { projectAgentMembers, memberStatus } from './agentTeamProjection';
import { mergeAgentEvents } from '../hooks/useAgentRuns';
import { buildClonedDataPatch } from './configClone';
import type { AgentRunEvent } from '../types/agents';

const event = (sequence: number, kind: string, memberId = 'researcher'): AgentRunEvent =>
  ({ agentRunId: 'run', sequence, kind, memberId, toolName: null, content: '', timestamp: '2026-09-18T12:00:00Z' });

describe('agent team projection and journal catch-up', () => {
  it('projects only valid members without mutating the persisted graph', () => {
    const config = { task: 'review', members: [{ id: 'supervisor', role: 'Lead' }, null, { role: 'Invalid' }, { id: 'researcher', role: 'Researcher' }] };
    const original = structuredClone(config);
    expect(projectAgentMembers(config).map(m => m.id)).toEqual(['supervisor', 'researcher']);
    expect(config).toEqual(original);
  });

  it('replays independent member status through a question and follow-up', () => {
    const events = [event(1, 'member_started'), event(2, 'member_needs_input'), event(3, 'member_started', 'reviewer')];
    expect(memberStatus(events, 'researcher')).toBe('NeedsInput');
    expect(memberStatus(events, 'reviewer')).toBe('Running');
    expect(memberStatus([...events, event(4, 'member_started'), event(5, 'member_completed')], 'researcher')).toBe('Succeeded');
  });

  it('merges repeated catch-up pages in sequence without duplicate display', () => {
    const initial = [event(1, 'member_started'), event(3, 'tool_completed')];
    const result = mergeAgentEvents(initial, [event(4, 'member_completed'), event(2, 'tool_started'), event(3, 'tool_completed')]);
    expect(result.map(e => e.sequence)).toEqual([1, 2, 3, 4]);
    expect(mergeAgentEvents(result, result)).toEqual(result);
  });

  it('clones nested selections independently, preserves identity bindings and removes legacy retry', () => {
    const source = { activityType: 'aiAgent', targetMachineId: 'target', credentialId: 'credential', config: {
      agent: { tools: [{ name: 'powershell' }] }, retry: { maxAttempts: 2 }, task: 'test' } };
    const patch = buildClonedDataPatch(source, 'aiAgent', 'all');
    expect(patch.targetMachineId).toBe('target');
    expect(patch.credentialId).toBe('credential');
    expect(patch.__configPatch).not.toHaveProperty('retry');
    const copied = patch.__configPatch as typeof source.config;
    copied.agent.tools[0].name = 'cmd';
    expect(source.config.agent.tools[0].name).toBe('powershell');
  });
});
