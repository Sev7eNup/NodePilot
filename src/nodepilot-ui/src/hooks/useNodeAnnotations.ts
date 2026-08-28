import { useCallback, useEffect, useMemo, useState } from 'react';
import type { Node, Edge } from '@xyflow/react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { ManagedMachine, StepExecution } from '../types/api';
import { useDesignStore } from '../stores/designStore';
import { REMOTE_ACTIVITY_TYPES } from '../components/designer/properties/shared';
import { findEdgePathBetween } from '../lib/upstreamVariables';

type LiveExecution = {
  status: string;
  steps: { stepId: string; status: string }[];
} | null | undefined;

type SelectedItem = { type: 'node' | 'edge'; id: string } | null;

// Stable empty-set reference returned by varFlowEdgeIds when no hover is active. Module-level so
// the useMemo does not hand out a fresh Set per render and retrigger the effect deps.
const EMPTY_EDGE_SET = new Set<string>();

export interface NodeAnnotationsApi {
  /** [{id, name, colorIdx}] in stable color-assignment order, for the machine-coloring legend. */
  legendMachines: { id: string; name: string; colorIdx: number }[];
  /** Pass to PropertiesPanel as `onVarHover`; hovering a variable row highlights the producer
   *  node and the path leading to it. */
  handleVarHover: (producerNodeId: string | null) => void;
}

/**
 * Bundles the effects that annotate the node graph for display:
 *   - __liveStatus: from the SignalR live execution and scrubbable replay
 *   - __health: from /step-health (last 8 outcomes per node, sparkline)
 *   - __stats: from /step-stats (avg/p95/failureRate; backs perf annotations and heatmap)
 *   - __machineColorIdx: stable color index per unique target machine
 *   - __varFlowRole on nodes and __varFlowHighlighted on edges: producer/consumer hover highlight
 *
 * They live in one hook because they share the same `setNodes`/`setEdges` and react to
 * overlapping inputs.
 */
export function useNodeAnnotations({
  workflowId,
  workflowIsEnabled,
  nodes,
  setNodes,
  edges,
  setEdges,
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
  setNodes: React.Dispatch<React.SetStateAction<Node[]>>;
  edges: Edge[];
  setEdges: React.Dispatch<React.SetStateAction<Edge[]>>;
  selected: SelectedItem;
  liveExecution: LiveExecution;
  replayExecutionId: string | null;
  replaySteps: StepExecution[] | undefined;
  scrubTimeMs: number | null;
  machines: ManagedMachine[];
}): NodeAnnotationsApi {
  // ---- __liveStatus: SignalR live > replay > clear ----
  useEffect(() => {
    if (liveExecution && liveExecution.steps.length > 0) {
      const statusByStepId = new Map<string, string>();
      for (const s of liveExecution.steps) statusByStepId.set(s.stepId, s.status);
      setNodes((nds: Node[]) => nds.map((n) => {
        const next = statusByStepId.get(n.id);
        const current = (n.data as Record<string, unknown>).__liveStatus as string | undefined;
        if (next === current) return n;
        return { ...n, data: { ...n.data, __liveStatus: next } };
      }));
      return;
    }

    let statusByStepId: Map<string, string> | null = null;
    if (replayExecutionId && replaySteps && replaySteps.length > 0) {
      if (scrubTimeMs != null) {
        statusByStepId = new Map(
          replaySteps.flatMap((s) => {
            const start = s.startedAt ? new Date(s.startedAt).getTime() : null;
            const end = s.completedAt ? new Date(s.completedAt).getTime() : null;
            if (!start || start > scrubTimeMs) return [];
            const status = (!end || end > scrubTimeMs) ? 'Running' : s.status;
            return [[s.stepId, status]] as [string, string][];
          }),
        );
      } else {
        statusByStepId = new Map<string, string>(replaySteps.map((s) => [s.stepId, s.status]));
      }
    }

    setNodes((nds: Node[]) => nds.map((n) => {
      const next = statusByStepId?.get(n.id);
      const current = (n.data as Record<string, unknown>).__liveStatus as string | undefined;
      if (next === current) return n;
      if (next) return { ...n, data: { ...n.data, __liveStatus: next } };
      const d = n.data as Record<string, unknown>;
      if (!('__liveStatus' in d)) return n;
      const { __liveStatus, ...rest } = d;
      void __liveStatus;
      return { ...n, data: rest };
    }));
  }, [liveExecution, replayExecutionId, replaySteps, scrubTimeMs, setNodes]);

  // ---- __health: sparkline data per activity node ----
  const nodeIds = useMemo(() => nodes.filter((n) => n.type === 'activity').map((n) => n.id), [nodes]);
  const { data: stepHealth } = useQuery({
    queryKey: ['step-health', workflowId, nodeIds.join(',')],
    queryFn: () => api.get<Record<string, { status: string; startedAt: string }[]>>(
      `/workflows/${workflowId}/step-health?stepIds=${nodeIds.join(',')}&limit=8`,
    ),
    enabled: !!workflowId && nodeIds.length > 0,
    refetchInterval: 60_000,
    staleTime: 30_000,
  });

  // `nodes` belongs in the deps: after save/publish/lock the editor rebuilds `nodes` from
  // `definitionJson`, which drops __health, and without a re-run the dots stay gone until the
  // next refetch. Returning the original array when nothing changed keeps that from looping.
  useEffect(() => {
    if (!stepHealth) return;
    setNodes((nds: Node[]) => {
      let mutated = false;
      const next = nds.map((n) => {
        const health = stepHealth[n.id];
        if (!health && !('__health' in (n.data as Record<string, unknown>))) return n;
        if (!health) {
          const { __health: _h, ...rest } = n.data as Record<string, unknown>;
          void _h;
          mutated = true;
          return { ...n, data: rest };
        }
        if ((n.data as Record<string, unknown>).__health === health) return n;
        mutated = true;
        return { ...n, data: { ...n.data, __health: health } };
      });
      return mutated ? next : nds;
    });
  }, [stepHealth, nodes, setNodes]);

  // ---- __stats: avg/p95/failureRate (used by perf annotations + failure heatmap) ----
  const { data: stepStats } = useQuery({
    queryKey: ['step-stats', workflowId],
    queryFn: () => api.get<Record<string, {
      totalRuns: number; failedRuns: number; failureRate: number;
      avgDurationMs: number; p95DurationMs: number; lastDurationMs: number;
    }>>(`/workflows/${workflowId}/step-stats?windowDays=30`),
    enabled: !!workflowId,
    refetchInterval: 5 * 60_000,
    staleTime: 60_000,
  });

  // `nodes` in the deps plus the stable-ref pattern, for the same reason as __health above: a
  // node rebuild from `definitionJson` would otherwise strip __stats until the next refetch.
  useEffect(() => {
    if (!stepStats) return;
    setNodes((nds: Node[]) => {
      let mutated = false;
      const next = nds.map((n) => {
        const stats = stepStats[n.id];
        const had = '__stats' in (n.data as Record<string, unknown>);
        if (!stats && !had) return n;
        if (!stats) {
          const { __stats: _s, ...rest } = n.data as Record<string, unknown>;
          void _s;
          mutated = true;
          return { ...n, data: rest };
        }
        if ((n.data as Record<string, unknown>).__stats === stats) return n;
        mutated = true;
        return { ...n, data: { ...n.data, __stats: stats } };
      });
      return mutated ? next : nds;
    });
  }, [stepStats, nodes, setNodes]);

  // ---- __workflowEnabled: false marks the workflow disabled, so custom nodes pause their
  // live-ticking indicators (e.g. the scheduleTrigger countdown). Enabled is the default, so the
  // annotation is removed again on true instead of being set everywhere. `nodes` stays in the
  // deps because lock/unlock/publish/disable rebuild `nodes` from `definitionJson` and would
  // wipe the annotation; returning the original array when nothing changed avoids a loop. ----
  useEffect(() => {
    setNodes((nds: Node[]) => {
      let mutated = false;
      const next = nds.map((n) => {
        const current = (n.data as Record<string, unknown>).__workflowEnabled as boolean | undefined;
        if (!workflowIsEnabled) {
          if (current === false) return n;
          mutated = true;
          return { ...n, data: { ...n.data, __workflowEnabled: false } };
        }
        if (current === undefined) return n;
        mutated = true;
        const { __workflowEnabled: _w, ...rest } = n.data as Record<string, unknown>;
        void _w;
        return { ...n, data: rest };
      });
      return mutated ? next : nds;
    });
  }, [workflowIsEnabled, nodes, setNodes]);

  // ---- __machineColorIdx: stable color index per unique target machine ----
  const machineColoringEnabled = useDesignStore((s) => s.machineColoringEnabled);
  // Value-based cache key: recompute only when the assigned machines change, not on every new
  // `nodes` array identity (the designer produces one on any drag). Hoisted into a local because
  // the dependency list must contain simple expressions (react-hooks/use-memo).
  const targetMachineIdKey = nodes
    .map((n) => (n.data as Record<string, unknown>).targetMachineId)
    .join(',');
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

  useEffect(() => {
    setNodes((nds: Node[]) => nds.map((n) => {
      if (!machineColoringEnabled || n.type !== 'activity') {
        if (!('__machineColorIdx' in (n.data as Record<string, unknown>))) return n;
        const { __machineColorIdx: _mc, ...rest } = n.data as Record<string, unknown>;
        void _mc;
        return { ...n, data: rest };
      }
      const machineId = (n.data as Record<string, unknown>).targetMachineId as string | null;
      if (!machineId || machineId.startsWith('{{') || !REMOTE_ACTIVITY_TYPES.has((n.data as Record<string, unknown>).activityType as string)) {
        if (!('__machineColorIdx' in (n.data as Record<string, unknown>))) return n;
        const { __machineColorIdx: _mc, ...rest } = n.data as Record<string, unknown>;
        void _mc;
        return { ...n, data: rest };
      }
      const idx = sortedMachineIds.indexOf(machineId);
      if ((n.data as Record<string, unknown>).__machineColorIdx === idx) return n;
      return { ...n, data: { ...n.data, __machineColorIdx: idx } };
    }));
  }, [machineColoringEnabled, sortedMachineIds, setNodes]);

  // ---- __varFlowRole on nodes + __varFlowHighlighted on edges (PropertiesPanel hover) ----
  const [varFlowProducerId, setVarFlowProducerId] = useState<string | null>(null);
  const handleVarHover = useCallback((producerNodeId: string | null) => {
    setVarFlowProducerId(producerNodeId);
  }, []);

  useEffect(() => {
    setNodes((nds: Node[]) => nds.map((n) => {
      const role: 'producer' | 'consumer' | undefined =
        !varFlowProducerId ? undefined :
        n.id === varFlowProducerId ? 'producer' :
        n.id === (selected?.type === 'node' ? selected.id : null) ? 'consumer' :
        undefined;
      const current = (n.data as Record<string, unknown>).__varFlowRole;
      if (current === role) return n;
      if (!role) {
        if (!('__varFlowRole' in (n.data as Record<string, unknown>))) return n;
        const { __varFlowRole: _r, ...rest } = n.data as Record<string, unknown>;
        void _r;
        return { ...n, data: rest };
      }
      return { ...n, data: { ...n.data, __varFlowRole: role } };
    }));
  }, [varFlowProducerId, selected, setNodes]);

  // Edge-topology key (source and target only): stable across data-only edge updates, so the
  // path memo does not recompute when __varFlowHighlighted flips.
  const edgeTopologyKey = useMemo(
    () => edges.map((e) => `${e.id}:${e.source}:${e.target}`).join('|'),
    [edges],
  );

  const varFlowEdgeIds = useMemo(() => {
    if (!varFlowProducerId || selected?.type !== 'node') return EMPTY_EDGE_SET;
    return findEdgePathBetween(varFlowProducerId, selected.id, edges);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [varFlowProducerId, selected, edgeTopologyKey]);

  useEffect(() => {
    setEdges((eds: Edge[]) => {
      let anyChanged = false;
      const next = eds.map((e) => {
        const highlighted = varFlowEdgeIds.has(e.id);
        const current = !!(e.data as Record<string, unknown>).__varFlowHighlighted;
        if (current === highlighted) return e;
        anyChanged = true;
        if (!highlighted) {
          const { __varFlowHighlighted: _h, ...rest } = e.data as Record<string, unknown>;
          void _h;
          return { ...e, data: rest };
        }
        return { ...e, data: { ...e.data, __varFlowHighlighted: true } };
      });
      // Return the original ref when nothing changed, so React Flow does not emit a new edges
      // array and retrigger varFlowEdgeIds through the topology-key memo.
      return anyChanged ? next : eds;
    });
  }, [varFlowEdgeIds, setEdges]);

  return { legendMachines, handleVarHover };
}
