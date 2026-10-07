import { describe, expect, it } from 'vitest';
import { agentRunCoverage } from './agentRunSummary';
import type { AgentRunEvent } from '../types/agents';

const coverage = [
  { requirement: 'Read configuration', status: 'fulfilled', basis: 'Configuration inspected.' },
  { requirement: 'Verify connectivity', status: 'unresolved', basis: 'Client unavailable.' },
];
const event = (sequence: number, content: unknown): AgentRunEvent => ({
  sequence, kind: 'run_conclusion', content: JSON.stringify(content), agentRunId: 'run',
  timestamp: '', memberId: 'lead', toolName: null,
});

describe('agentRunCoverage', () => {
  it('uses the latest assessment by sequence, independent of input ordering', () => {
    expect(agentRunCoverage([event(9, { coverage }), event(2, { coverage: [coverage[0]] })])).toEqual(coverage);
  });
  it('does not mistake earlier member questions for remaining work', () => {
    expect(agentRunCoverage([{ ...event(10, { coverage }), kind: 'member_needs_input' }])).toBeNull();
  });
  it.each([null, {}, { coverage: [] }, { coverage: [coverage[0], { status: 'unresolved' }] },
    { coverage: [{ ...coverage[0], status: 'unknown' }] }, { coverage: [{ ...coverage[0], basis: ' ' }] }])(
    'does not present missing or invalid coverage as an empty checklist: %j', value => {
      expect(agentRunCoverage([event(2, value)])).toBeNull();
    });
  it('does not fall back to an outdated assessment when the latest is malformed', () => {
    expect(agentRunCoverage([event(1, { coverage }), { ...event(2, {}), content: '{' }])).toBeNull();
  });
});
