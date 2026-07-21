import { useEffect, useMemo } from 'react';
import { type Node, type Edge, useReactFlow } from '@xyflow/react';

interface CriticalPathAnnotation {
  isCritical: boolean;
  slack: number;       // ms of slack (0 = on critical path)
  earliestStart: number; // ms from workflow start
  duration: number;    // p95 duration in ms
}

/**
 * Computes the critical path through the workflow DAG using CPM (Critical Path Method).
 * Uses p95 duration from __stats as edge weights. Nodes without stats default to 0ms.
 * Stamps `__criticalPath` onto activity nodes.
 * Only active when `criticalPathEnabled` is true in the design store.
 */
export function useCriticalPath(
  _workflowId: string | undefined,
  enabled: boolean,
  nodes: Node[],
  edges: Edge[],
) {
  const { setNodes } = useReactFlow();

  const stats = useMemo(() => {
    const map = new Map<string, { p95DurationMs: number }>();
    for (const n of nodes) {
      const s = (n.data as Record<string, unknown>)?.__stats as { p95DurationMs: number } | undefined;
      if (s && typeof s.p95DurationMs === 'number') {
        map.set(n.id, { p95DurationMs: s.p95DurationMs });
      }
    }
    return map;
  }, [nodes]);

  const result = useMemo(() => {
    if (!enabled) return null;

    // Build adjacency over executable nodes only. Disabled nodes are treated as removed
    // from the CPM graph; otherwise their outgoing edges keep successors' in-degree above
    // zero and the successor never reaches the topological queue.
    const adj = new Map<string, string[]>();       // nodeId -> downstream nodeIds
    const reverseAdj = new Map<string, string[]>(); // nodeId -> upstream nodeIds
    const nodeById = new Map(nodes.map((n) => [n.id, n]));
    const isDisabled = (id: string): boolean => {
      const n = nodeById.get(id);
      return !!(n?.data as Record<string, unknown>)?.disabled;
    };
    const activeNodeIds = new Set(nodes.filter((n) => !isDisabled(n.id)).map((n) => n.id));

    for (const id of activeNodeIds) {
      adj.set(id, []);
      reverseAdj.set(id, []);
    }

    for (const e of edges) {
      // Skip disabled edges and edges touching disabled/missing nodes.
      const edgeData = e.data as Record<string, unknown> | undefined;
      if (edgeData?.disabled) continue;
      if (!activeNodeIds.has(e.source) || !activeNodeIds.has(e.target)) continue;
      adj.get(e.source)?.push(e.target);
      reverseAdj.get(e.target)?.push(e.source);
    }

    // Get duration for a node (p95, or 0 if no stats)
    const getDuration = (id: string): number => {
      const s = stats.get(id);
      return s ? Math.max(0, s.p95DurationMs) : 0;
    };

    // Topological sort (Kahn's algorithm)
    const inDegree = new Map<string, number>();
    for (const id of activeNodeIds) {
      inDegree.set(id, 0);
    }
    for (const e of edges) {
      const edgeData = e.data as Record<string, unknown> | undefined;
      if (edgeData?.disabled) continue;
      if (!activeNodeIds.has(e.source) || !activeNodeIds.has(e.target)) continue;
      inDegree.set(e.target, (inDegree.get(e.target) ?? 0) + 1);
    }

    const topo: string[] = [];
    const queue: string[] = [];
    for (const id of activeNodeIds) {
      if ((inDegree.get(id) ?? 0) === 0) {
        queue.push(id);
      }
    }

    while (queue.length > 0) {
      const id = queue.shift()!;
      topo.push(id);
      for (const next of (adj.get(id) ?? [])) {
        const deg = (inDegree.get(next) ?? 1) - 1;
        inDegree.set(next, deg);
        if (deg === 0) queue.push(next);
      }
    }

    if (topo.length === 0) return new Map<string, CriticalPathAnnotation>();

    // Forward pass: earliest start
    const earliestStart = new Map<string, number>();
    for (const id of topo) {
      let maxPredFinish = 0;
      for (const pred of (reverseAdj.get(id) ?? [])) {
        const predFinish = (earliestStart.get(pred) ?? 0) + getDuration(pred);
        if (predFinish > maxPredFinish) maxPredFinish = predFinish;
      }
      earliestStart.set(id, maxPredFinish);
    }

    // Find the overall end time
    let maxFinish = 0;
    for (const id of topo) {
      const finish = (earliestStart.get(id) ?? 0) + getDuration(id);
      if (finish > maxFinish) maxFinish = finish;
    }

    // Backward pass: latest start
    const latestStart = new Map<string, number>();
    // Initialize all nodes to maxFinish - duration
    for (const id of topo) {
      latestStart.set(id, maxFinish - getDuration(id));
    }

    // Reverse topological order
    const revTopo = [...topo].reverse();
    for (const id of revTopo) {
      let minSuccStart = maxFinish;
      const successors = adj.get(id) ?? [];
      if (successors.length === 0) {
        // Terminal node: latest finish = maxFinish
        latestStart.set(id, maxFinish - getDuration(id));
      } else {
        for (const succ of successors) {
          const succStart = latestStart.get(succ) ?? maxFinish;
          if (succStart < minSuccStart) minSuccStart = succStart;
        }
        latestStart.set(id, minSuccStart - getDuration(id));
      }
    }

    // Compute slack and critical path
    const annotations = new Map<string, CriticalPathAnnotation>();
    for (const id of topo) {
      const es = earliestStart.get(id) ?? 0;
      const ls = latestStart.get(id) ?? 0;
      const dur = getDuration(id);
      const slack = ls - es;
      annotations.set(id, {
        isCritical: Math.abs(slack) < 1, // sub-ms tolerance for floating point
        slack: Math.max(0, Math.round(slack)),
        earliestStart: Math.round(es),
        duration: Math.round(dur),
      });
    }

    return annotations;
  }, [enabled, nodes, edges, stats]);

  // Stamp annotations onto nodes (or clear when disabled)
  useEffect(() => {
    if (!enabled || !result) {
      // Clear __criticalPath from all nodes
      setNodes((nds: Node[]) => {
        let changed = false;
        const updated = nds.map((n) => {
          if ((n.data as Record<string, unknown>)?.__criticalPath) {
            changed = true;
            const { __criticalPath, ...rest } = n.data as Record<string, unknown>;
            return { ...n, data: rest };
          }
          return n;
        });
        return changed ? updated : nds;
      });
      return;
    }

    setNodes((nds: Node[]) => {
      let changed = false;
      const updated = nds.map((n) => {
        const annotation = result.get(n.id);
        const current = (n.data as Record<string, unknown>)?.__criticalPath as CriticalPathAnnotation | undefined;
        if (annotation) {
          if (!current || current.isCritical !== annotation.isCritical || current.slack !== annotation.slack) {
            changed = true;
            return { ...n, data: { ...n.data, __criticalPath: annotation } };
          }
        } else if (current) {
          changed = true;
          const { __criticalPath, ...rest } = n.data as Record<string, unknown>;
          return { ...n, data: rest };
        }
        return n;
      });
      return changed ? updated : nds;
    });
  }, [enabled, result, setNodes]);
}
