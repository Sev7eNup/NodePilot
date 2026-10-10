import type { AgentDefinition, AgentRunEvent } from '../types/agents';

/** Members are a render projection, never React Flow nodes or workflow steps. */
export function projectAgentMembers(config: Record<string, unknown>): AgentDefinition[] {
  if (!Array.isArray(config.members)) return [];
  return config.members.filter((member): member is AgentDefinition =>
    !!member && typeof member === 'object' && typeof member.id === 'string' && typeof member.role === 'string');
}

export function memberStatus(events: AgentRunEvent[], memberId: string): string | undefined {
  let status: string | undefined;
  for (const event of events) {
    if (['Running', 'NeedsInput'].includes(status ?? '') && ['run_failed', 'run_cancelled'].includes(event.kind))
      status = event.kind === 'run_failed' ? 'Failed' : 'Cancelled';
    if (event.memberId !== memberId) continue;
    if (['member_started', 'model_started', 'tool_started'].includes(event.kind)) status = 'Running';
    if (event.kind === 'member_completed') status = 'Succeeded';
    if (event.kind === 'member_needs_input') status = 'NeedsInput';
    if (['member_failed', 'model_failed'].includes(event.kind)) status = 'Failed';
  }
  return status;
}
