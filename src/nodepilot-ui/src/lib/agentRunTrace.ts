import type { AgentRun, AgentRunEvent } from '../types/agents';

export interface RunMember { id: string; role: string; function?: string; model?: string; targetMachineId?: string }
export interface TraceEvent { type: 'event'; start: AgentRunEvent; end?: AgentRunEvent }
export interface TraceDelegation {
  type: 'delegation'; start: AgentRunEvent; end?: AgentRunEvent;
  from: string; to: string; task: string; reason?: string;
  reviewer: boolean;
  status?: string; response?: string; objectionKind?: string;
  activities: TraceEvent[];
}
export type TraceEntry = TraceEvent | TraceDelegation;

export function traceObject(content: string): Record<string, unknown> {
  try { const value: unknown = JSON.parse(content); return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
  catch { return {}; }
}
const text = (value: unknown): string | undefined => typeof value === 'string' ? value : undefined;

export function runMembers(events: AgentRunEvent[]): RunMember[] {
  const members = new Map<string, RunMember>();
  for (const event of events) {
    if (event.kind === 'run_context') {
      const snapshot = traceObject(event.content).members;
      if (Array.isArray(snapshot)) for (const member of snapshot) {
        if (member && typeof member.id === 'string' && typeof member.role === 'string')
          members.set(member.id, { id: member.id, role: member.role, function: text(member.function),
            model: text(member.model), targetMachineId: text(member.targetMachineId) });
      }
    }
    if (event.memberId && !members.has(event.memberId)) members.set(event.memberId, { id: event.memberId, role: event.memberId });
  }
  return [...members.values()];
}

/** Correlation uses journal order: team delegation and tool invocation are sequential. */
export function agentRunTrace(events: AgentRunEvent[]): TraceEntry[] {
  const entries: TraceEntry[] = [];
  const reviewers = new Set(runMembers(events).filter(m => m.function === 'reviewer').map(m => m.id));
  let active: TraceDelegation | undefined;
  const pending = new Map<string, TraceEvent>();
  for (const event of [...events].sort((a, b) => a.sequence - b.sequence)) {
    if (event.kind === 'run_context') continue;
    if (event.toolName === 'delegate' && event.kind === 'tool_started') {
      const input = traceObject(event.content);
      if (typeof input.memberId === 'string' && event.memberId) {
        active = { type: 'delegation', start: event, from: event.memberId, to: input.memberId,
          task: text(input.task) ?? event.content, reason: text(input.reason), reviewer: reviewers.has(input.memberId), activities: [] };
        entries.push(active);
        continue;
      }
    }
    if (active && event.memberId === active.from && event.toolName === 'delegate'
        && ['tool_completed', 'tool_failed'].includes(event.kind)) {
      const result = traceObject(event.content);
      active.end = event;
      active.status = event.kind === 'tool_failed' || result.error ? 'failed' : text(result.status) ?? 'unknown';
      active.response = text(result.content) ?? text(result.error) ?? event.content;
      active.objectionKind = text(result.objectionKind);
      active = undefined;
      continue;
    }
    const destination = active && event.memberId === active.to ? active.activities : entries;
    const family = event.kind.startsWith('tool_') ? 'tool' : event.kind.startsWith('model_') ? 'model' : undefined;
    const key = JSON.stringify([event.memberId, family, event.toolName]);
    if (family && event.kind.endsWith('_started')) {
      const row: TraceEvent = { type: 'event', start: event };
      destination.push(row); pending.set(key, row);
    } else if (family && /_(completed|failed)$/.test(event.kind) && pending.has(key)) {
      pending.get(key)!.end = event; pending.delete(key);
    } else {
      // Member start/return duplicate the surrounding delegation's task and answer.
      if (active && event.memberId === active.to && ['member_started', 'member_completed', 'member_needs_input', 'member_failed'].includes(event.kind)) continue;
      destination.push({ type: 'event', start: event });
    }
  }
  return entries;
}

export function traceOutcome(entry: TraceEntry, run: AgentRun): string {
  if (entry.type === 'delegation' && entry.status) return entry.status;
  if (entry.end) return entry.end.kind.endsWith('_failed') ? 'failed' : 'completed';
  if (entry.type === 'delegation' || /^(tool|model)_started$/.test(entry.start.kind))
    return run.status === 'Running' ? 'running' : 'interrupted';
  if (entry.start.kind.endsWith('_failed')) return 'failed';
  return '';
}

export function traceMatches(entry: TraceEntry, member: string): boolean {
  return !member || (entry.type === 'delegation' ? entry.from === member || entry.to === member : entry.start.memberId === member);
}

export function traceDuration(start: string, end?: string | null): number | undefined {
  if (!end) return undefined;
  const value = Date.parse(end) - Date.parse(start);
  return Number.isFinite(value) && value >= 0 ? value / 1000 : undefined;
}
