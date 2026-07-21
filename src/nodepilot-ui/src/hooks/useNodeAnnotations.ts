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

// Stable empty-set reference — returned by varFlowEdgeIds when no hover is active. Module-level
// so the useMemo doesn't return a fresh Set() per render and retrigger the effect's deps.
const EMPTY_EDGE_SET = new Set<string>();

export interface NodeAnnotationsApi {
  /** [{id, name, colorIdx}] in stable color-assignment order — for the machine-coloring legend. */
  legendMachines: { id: string; name: string; colorIdx: number }[];
  /** Pass to PropertiesPanel as `onVarHover` — hovers on a variable row light up its producer + path. */
  handleVarHover: (producerNodeId: string | null) => void;
}

/**
 * Bundles every "annotate the node graph for visual purposes" effect:
 *   - __liveStatus  ← SignalR live execution + scrubbable replay
 *   - __health      ← /step-health (last 8 outcomes per node, sparkline)
 *   - __stats       ← /step-stats (avg/p95/failureRate; backs perf annotations + heatmap)
 *   - __machineColorIdx ← stable color index per unique target machine
 *   - __varFlowRole on nodes + __varFlowHighlighted on edges ← producer/consumer hover highlight
 *
 * Kept in one hook because they all share the same `setNodes`/`setEdges` and react to overlapping
 * inputs — splitting would multiply prop-drilling and re-renders without a clarity win.
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

  useEffect(() => {
    if (!stepHealth) return;
    setNodes((nds: Node[]) => nds.map((n) => {
      const health = stepHealth[n.id];
      if (!health && !('__health' in (n.data as Record<string, unknown>))) return n;
      if (!health) {
        const { __health: _h, ...rest } = n.data as Record<string, unknown>;
        void _h;
        return { ...n, data: rest };
      }
      if ((n.data as Record<string, unknown>).__health === health) return n;
      return { ...n, data: { ...n.data, __health: health } };
    }));
  }, [stepHealth, setNodes]);

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

  useEffect(() => {
    if (!stepStats) return;
    setNodes((nds: Node[]) => nds.map((n) => {
      const stats = stepStats[n.id];
      const had = '__stats' in (n.data as Record<string, unknown>);
      if (!stats && !had) return n;
      if (!stats) {
        const { __stats: _s, ...rest } = n.data as Record<string, unknown>;
        void _s;
        return { ...n, data: rest };
      }
      if ((n.data as Record<string, unknown>).__stats === stats) return n;
      return { ...n, data: { ...n.data, __stats: stats } };
    }));
  }, [stepStats, setNodes]);

  // ---- __workflowEnabled: false marks the workflow disabled — custom nodes pause their
  // live-ticking indicators (e.g. the scheduleTrigger countdown) based on this flag. Enabled
  // is the default, so we remove the annotation again on true instead of setting it everywhere.
  //
  // The deps array must include `nodes`: after `lock`/`unlock`/`publish`/`disable` the editor
  // refetches the workflow definition and rebuilds `nodes` from `definitionJson`, which would
  // otherwise wipe out the annotation. The stable-ref pattern (return the original array when
  // nothing changed) avoids the render loop that would otherwise result. ----
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
  const sortedMachineIds = useMemo(
    () => [...new Set(
      nodes
        .filter((n) => n.type === 'activity' && REMOTE_ACTIVITY_TYPES.has((n.data as Record<string, unknown>).activityType as string))
        .map((n) => (n.data as Record<string, unknown>).targetMachineId as string | null)
        .filter((id): id is string => !!id && !id.startsWith('{{'))
    )].sort((a, b) => a.localeCompare(b)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [nodes.map((n) => (n.data as Record<string, unknown>).targetMachineId).join(',')],
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

  // Edge-topology key (source→target only): stable across data-only edge updates so the
  // path memo doesn't recompute when __varFlowHighlighted itself flips.
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
      // Return original ref when nothing changed — prevents React Flow from emitting a new
      // edges array, which would retrigger varFlowEdgeIds via the topology-key memo.
      return anyChanged ? next : eds;
    });
  }, [varFlowEdgeIds, setEdges]);

  return { legendMachines, handleVarHover };
}
