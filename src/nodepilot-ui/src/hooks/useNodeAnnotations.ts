import { useCallback, useMemo, useState } from 'react';
import type { Node, Edge } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { MachineOption, StepExecution } from '../types/api';
import { useDesignStore } from '../stores/designStore';
import { REMOTE_ACTIVITY_TYPES } from '../components/designer/properties/shared';
import { findEdgePathBetween } from '../lib/upstreamVariables';

type LiveExecution = {
  status: string;
  steps: { stepId: string; status: string }[];
} | null | undefined;

type SelectedItem = { type: 'node' | 'edge'; id: string } | null;

export type StepHealthEntry = { status: string; startedAt: string };

export interface StepStats {
  totalRuns: number;
  failedRuns: number;
  failureRate: number;
  avgDurationMs: number;
  p95DurationMs: number;
  lastDurationMs: number;
}

/** Display-only node data from runtime sources. useDisplayedGraph merges it into the projection. */
export interface NodeAnnotation {
  __liveStatus?: string;
  __health?: StepHealthEntry[];
  __stats?: StepStats;
  __machineColorIdx?: number;
  __varFlowRole?: 'producer' | 'consumer';
  /** Only ever false: custom nodes pause their live-ticking indicators on a disabled workflow. */
  __workflowEnabled?: false;
}

// Stable empty set while no variable is hovered, so the edge projection keeps its memo.
const EMPTY_EDGE_SET: ReadonlySet<string> = new Set<string>();

export interface NodeAnnotationsApi {
  /** Annotations per node id; nodes without any are absent. */
  nodeAnnotations: ReadonlyMap<string, NodeAnnotation>;
  /** Edges on the path from the hovered variable's producer to the selected node. */
  varFlowEdgeIds: ReadonlySet<string>;
  /** Step stats from /step-stats, also the input of the critical-path overlay. */
  stepStats: Readonly<Record<string, StepStats>> | undefined;
  /** [{id, name, colorIdx}] in stable color-assignment order, for the machine-coloring legend. */
  legendMachines: { id: string; name: string; colorIdx: number }[];
  /** Pass to PropertiesPanel as `onVarHover`; hovering a variable row highlights the producer
   *  node and the path leading to it. */
  handleVarHover: (producerNodeId: string | null) => void;
}

/**
 * Derives the runtime annotations the canvas shows on top of the authored graph:
 *   - __liveStatus: from the SignalR live execution and scrubbable replay
 *   - __health: from /step-health (last 8 outcomes per node, sparkline)
 *   - __stats: from /step-stats (avg/p95/failureRate; backs perf annotations and heatmap)
 *   - __machineColorIdx: stable color index per unique target machine
 *   - __varFlowRole on nodes and varFlowEdgeIds: producer/consumer hover highlight
 *   - __workflowEnabled: false while the workflow is disabled
 *
 * Nothing here writes into the graph state. Undo, save and dirty tracking only ever see authored
 * data, and an annotation is never stale after the graph is rebuilt from the server.
 */
export function useNodeAnnotations({
  workflowId,
  workflowIsEnabled,
  nodes,
  edges,
  selected,
  liveExecution,
  replayExecutionId,
  replaySteps,
  scrubTimeMs,
  machines,
}: {
  workflowId: string | undefined;
  workflowIsEnabled: boolean;
  nodes: Node[];
  edges: Edge[];
  selected: SelectedItem;
  liveExecution: LiveExecution;
  replayExecutionId: string | null;
  replaySteps: StepExecution[] | undefined;
  scrubTimeMs: number | null;
  machines: MachineOption[];
}): NodeAnnotationsApi {
  // ---- __liveStatus: SignalR live > replay > none ----
  const liveStatusById = useMemo((): ReadonlyMap<string, string> | null => {
    if (liveExecution && liveExecution.steps.length > 0) {
      return new Map(liveExecution.steps.map((s) => [s.stepId, s.status]));
    }
    if (!replayExecutionId || !replaySteps || replaySteps.length === 0) return null;
    if (scrubTimeMs == null) return new Map(replaySteps.map((s) => [s.stepId, s.status]));
    return new Map(
      replaySteps.flatMap((s) => {
        const start = s.startedAt ? new Date(s.startedAt).getTime() : null;
        const end = s.completedAt ? new Date(s.completedAt).getTime() : null;
        if (!start || start > scrubTimeMs) return [];
        const status = (!end || end > scrubTimeMs) ? 'Running' : s.status;
        return [[s.stepId, status]] as [string, string][];
      }),
    );
  }, [liveExecution, replayExecutionId, replaySteps, scrubTimeMs]);

  // ---- __health: sparkline data per activity node ----
  const nodeIds = useMemo(() => nodes.filter((n) => n.type === 'activity').map((n) => n.id), [nodes]);
  const { data: stepHealth } = useQuery({
    queryKey: ['step-health', workflowId, nodeIds.join(',')],
    queryFn: () => api.get<Record<string, StepHealthEntry[]>>(
      `/workflows/${workflowId}/step-health?stepIds=${nodeIds.join(',')}&limit=8`,
    ),
    enabled: !!workflowId && nodeIds.length > 0,
    refetchInterval: 60_000,
    staleTime: 30_000,
  });

  // ---- __stats: avg/p95/failureRate (used by perf annotations + failure heatmap) ----
  const { data: stepStats } = useQuery({
    queryKey: ['step-stats', workflowId],
    queryFn: () => api.get<Record<string, StepStats>>(`/workflows/${workflowId}/step-stats?windowDays=30`),
    enabled: !!workflowId,
    refetchInterval: 5 * 60_000,
    staleTime: 60_000,
  });

  // ---- __machineColorIdx: stable color index per unique target machine ----
  const machineColoringEnabled = useDesignStore((s) => s.machineColoringEnabled);
  // Value-based cache key: recompute only when the assigned machines change, not on every new
  // `nodes` array identity (the designer produces one on any drag). Hoisted into a local because
  // the dependency list must contain simple expressions (react-hooks/use-memo). Building it at
  // all is skipped while the feature is off, since it is itself a per-frame scan.
  const targetMachineIdKey = machineColoringEnabled
    ? nodes.map((n) => (n.data as Record<string, unknown>).targetMachineId).join(',')
    : '';
  const sortedMachineIds = useMemo(
    () => [...new Set(
      nodes
        .filter((n) => n.type === 'activity' && REMOTE_ACTIVITY_TYPES.has((n.data as Record<string, unknown>).activityType as string))
        .map((n) => (n.data as Record<string, unknown>).targetMachineId as string | null)
        .filter((id): id is string => !!id && !id.startsWith('{{'))
    )].sort((a, b) => a.localeCompare(b)),
    // Keyed on the derived string rather than `nodes`; depending on `nodes` would defeat the memo.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [targetMachineIdKey],
  );

  const legendMachines = useMemo(() => {
    return sortedMachineIds.map((id, colorIdx) => {
      const machine = machines.find((m) => m.id === id);
      return { id, name: machine ? `${machine.name} (${machine.hostname})` : id, colorIdx };
    });
  }, [sortedMachineIds, machines]);

  // ---- __varFlowRole on nodes + varFlowEdgeIds (PropertiesPanel hover) ----
  const [varFlowProducerId, setVarFlowProducerId] = useState<string | null>(null);
  const handleVarHover = useCallback((producerNodeId: string | null) => {
    setVarFlowProducerId(producerNodeId);
  }, []);
  const selectedNodeId = selected?.type === 'node' ? selected.id : null;

  // Edge-topology key (source and target only): stable across data-only edge updates.
  const edgeTopologyKey = useMemo(
    () => edges.map((e) => `${e.id}:${e.source}:${e.target}`).join('|'),
    [edges],
  );

  const varFlowEdgeIds = useMemo(() => {
    if (!varFlowProducerId || !selectedNodeId) return EMPTY_EDGE_SET;
    return findEdgePathBetween(varFlowProducerId, selectedNodeId, edges);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [varFlowProducerId, selectedNodeId, edgeTopologyKey]);

  const nodeAnnotations = useMemo(() => {
    const byNode = new Map<string, NodeAnnotation>();
    for (const n of nodes) {
      const data = n.data as Record<string, unknown>;
      const annotation: NodeAnnotation = {};
      const liveStatus = liveStatusById?.get(n.id);
      if (liveStatus) annotation.__liveStatus = liveStatus;
      const health = stepHealth?.[n.id];
      if (health) annotation.__health = health;
      const stats = stepStats?.[n.id];
      if (stats) annotation.__stats = stats;
      if (!workflowIsEnabled) annotation.__workflowEnabled = false;
      const machineId = data.targetMachineId as string | null | undefined;
      if (machineColoringEnabled && n.type === 'activity' && machineId && !machineId.startsWith('{{')
        && REMOTE_ACTIVITY_TYPES.has(data.activityType as string)) {
        const idx = sortedMachineIds.indexOf(machineId);
        if (idx >= 0) annotation.__machineColorIdx = idx;
      }
      if (varFlowProducerId) {
        if (n.id === varFlowProducerId) annotation.__varFlowRole = 'producer';
        else if (n.id === selectedNodeId) annotation.__varFlowRole = 'consumer';
      }
      if (Object.keys(annotation).length > 0) byNode.set(n.id, annotation);
    }
    return byNode;
  }, [nodes, liveStatusById, stepHealth, stepStats, workflowIsEnabled, machineColoringEnabled,
    sortedMachineIds, varFlowProducerId, selectedNodeId]);

  return { nodeAnnotations, varFlowEdgeIds, stepStats, legendMachines, handleVarHover };
}
