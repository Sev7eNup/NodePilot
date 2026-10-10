import type { AgentRun, AgentRunEvent } from '../types/agents';

export interface RunMember { id: string; role: string; function?: string; model?: string; targetMachineId?: string }
export interface TraceEvent { type: 'event'; start: AgentRunEvent; end?: AgentRunEvent }
export interface TraceDelegation {
  type: 'delegation'; start: AgentRunEvent; end?: AgentRunEvent;
  from: string; to: string; task: string; reason?: string;
  reviewer: boolean;
  delegationId: string; batchId?: string; batchSize?: number;
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

/** Members have one open assignment each; delegation IDs correlate interleaved batches. */
export function agentRunTrace(events: AgentRunEvent[]): TraceEntry[] {
  const entries: TraceEntry[] = [];
  const reviewers = new Set(runMembers(events).filter(m => m.function === 'reviewer').map(m => m.id));
  const byId = new Map<string, TraceDelegation>();
  const open = new Map<string, TraceDelegation>();
  const pending = new Map<string, TraceEvent>();
  for (const event of [...events].sort((a, b) => a.sequence - b.sequence)) {
    if (event.kind === 'run_context') continue;
    if (event.kind === 'member_started') {
      const input = traceObject(event.content);
      if (typeof input.delegationId === 'string' && event.memberId) {
        const delegation: TraceDelegation = { type: 'delegation', start: event, from: text(input.from) ?? '', to: event.memberId,
          delegationId: input.delegationId, batchId: text(input.batchId), batchSize: typeof input.batchSize === 'number' ? input.batchSize : undefined,
          task: text(input.task) ?? event.content, reason: text(input.reason), reviewer: reviewers.has(event.memberId), activities: [] };
        entries.push(delegation);
        byId.set(input.delegationId, delegation); open.set(event.memberId, delegation);
        continue;
      }
    }
    if (['member_completed', 'member_needs_input', 'member_failed'].includes(event.kind)) {
      const result = traceObject(event.content);
      const delegation = byId.get(text(result.delegationId) ?? '');
      if (delegation) {
        delegation.end = event;
        delegation.status = text(result.status) ?? event.kind.slice('member_'.length);
        delegation.response = text(result.content) ?? event.content;
        delegation.objectionKind = text(result.objectionKind);
        if (open.get(delegation.to) === delegation) open.delete(delegation.to);
        continue;
      }
    }
    const destination = open.get(event.memberId ?? '')?.activities ?? entries;
    const family = event.kind.startsWith('tool_') ? 'tool' : event.kind.startsWith('model_') ? 'model' : undefined;
    const key = JSON.stringify([event.memberId, family, event.toolName]);
    if (family && event.kind.endsWith('_started')) {
      const row: TraceEvent = { type: 'event', start: event };
      destination.push(row); pending.set(key, row);
    } else if (family && /_(completed|failed)$/.test(event.kind) && pending.has(key)) {
      pending.get(key)!.end = event; pending.delete(key);
    } else {
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

export type TraceItem =
  | { kind: 'entry'; entry: TraceEntry }
  | { kind: 'group'; memberId: string | null; entries: TraceEvent[] };

/** Smallest run of consecutive same-member events that is folded into one group. */
export const MIN_GROUP_SIZE = 3;

/**
 * Folds consecutive top-level events of one member (typically the supervisor's model and tool
 * calls between assignments) into a group, so assignments stay visible instead of drowning in
 * single-line events. Delegations and shorter runs stay individual entries.
 */
export function groupTrace(entries: TraceEntry[]): TraceItem[] {
  const items: TraceItem[] = [];
  let run: TraceEvent[] = [];
  const flush = () => {
    if (run.length >= MIN_GROUP_SIZE) items.push({ kind: 'group', memberId: run[0].start.memberId, entries: run });
    else for (const entry of run) items.push({ kind: 'entry', entry });
    run = [];
  };
  for (const entry of entries) {
    if (entry.type !== 'event') { flush(); items.push({ kind: 'entry', entry }); continue; }
    if (run.length > 0 && run[0].start.memberId !== entry.start.memberId) flush();
    run.push(entry);
  }
  flush();
  return items;
}
