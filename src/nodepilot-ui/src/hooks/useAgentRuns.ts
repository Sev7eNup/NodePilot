import { useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { AgentRun, AgentRunEvent } from '../types/agents';
import { captureAuthBoundaryGeneration, assertAuthBoundaryGenerationCurrent } from '../security/authBoundary';

export const agentRunsKey = (executionId: string) => ['agent-runs', executionId] as const;
export const agentEventsKey = (runId: string) => ['agent-events', runId] as const;

export function mergeAgentEvents(previous: AgentRunEvent[], incoming: AgentRunEvent[]): AgentRunEvent[] {
  const bySequence = new Map(previous.map(event => [event.sequence, event]));
  for (const event of incoming) bySequence.set(event.sequence, event);
  return [...bySequence.values()].sort((a, b) => a.sequence - b.sequence);
}

export function useAgentRuns(executionId: string | null | undefined, active = false) {
  return useQuery({ queryKey: agentRunsKey(executionId ?? ''), enabled: !!executionId,
    queryFn: () => api.get<AgentRun[]>(`/agents/runs?executionId=${executionId}`),
    refetchInterval: query => active || query.state.data?.some(run => run.status === 'Running') ? 3000 : false });
}

export function useAgentEvents(run: AgentRun | undefined) {
  const client = useQueryClient();
  const id = run?.id;
  const status = run?.status;
  useEffect(() => { if (id) void client.invalidateQueries({ queryKey: agentEventsKey(id) }); }, [client, id, status]);
  return useQuery({ queryKey: agentEventsKey(id ?? ''), enabled: !!id,
    queryFn: async () => {
      const boundary = captureAuthBoundaryGeneration();
      let events = client.getQueryData<AgentRunEvent[]>(agentEventsKey(id!)) ?? [];
      // Only REST-confirmed contiguous sequences advance the cursor. SignalR is a wake-up
      // notification, never the source of truth, so a dropped event cannot create a hole.
      for (;;) {
        const after = events.at(-1)?.sequence ?? 0;
        const page = await api.get<AgentRunEvent[]>(`/agents/runs/${id}/events?after=${after}&pageSize=500`);
        assertAuthBoundaryGenerationCurrent(boundary);
        events = mergeAgentEvents(events, page);
        if (page.length < 500) return events;
      }
    }, refetchInterval: status === 'Running' ? 2000 : false });
}
