import { useState, useEffect, useCallback } from 'react';
import { type Node, type Edge } from '@xyflow/react';
import { TRIGGER_ACTIVITY_TYPES } from '../lib/activityCatalog.generated';

export type SimulationResult = {
  reachable: Set<string>;
  skipped: Set<string>;
  order: string[];
};

export function useWorkflowSimulation(nodes: Node[], edges: Edge[]) {
  const [simulation, setSimulation] = useState<SimulationResult | null>(null);
  const [revealIndex, setRevealIndex] = useState(0);

  useEffect(() => {
    if (!simulation) { setRevealIndex(0); return; }
    if (revealIndex >= simulation.order.length) return;
    // 180 ms/step — long enough to follow the flow, short enough not to annoy.
    const t = globalThis.setTimeout(() => setRevealIndex((i) => i + 1), 180);
    return () => globalThis.clearTimeout(t);
  }, [simulation, revealIndex]);

  const runSimulation = useCallback(() => {
    if (nodes.length === 0) return;
    const activeEdges = edges.filter((e) => {
      const d = (e.data as Record<string, unknown>) ?? {};
      if (d.disabled) return false;
      const cond = (d.condition as string) || '';
      if (cond.endsWith('.failed')) return false;
      return true;
    });
    const outgoing = new Map<string, string[]>();
    const liveNodes = nodes.filter((n) => (n.data as Record<string, unknown>)?.activityType !== 'note');
    for (const n of liveNodes) outgoing.set(n.id, []);
    const liveIds = new Set(liveNodes.map((n) => n.id));
    for (const e of activeEdges) {
      if (!liveIds.has(e.source) || !liveIds.has(e.target)) continue;
      outgoing.get(e.source)!.push(e.target);
    }
    const disabledIds = new Set(liveNodes.filter((n) => (n.data as Record<string, unknown>)?.disabled).map((n) => n.id));
    // Roots are trigger-only, exactly like the engine: a graph without an (enabled) trigger has no
    // entry point, so the preview shows "nothing runs" instead of falsely promoting an inDegree-0
    // activity to a start node.
    const queue: string[] = liveNodes
      .filter((n) => {
        const d = (n.data as Record<string, unknown>) ?? {};
        return d.disabled !== true && TRIGGER_ACTIVITY_TYPES.has((d.activityType as string) ?? '');
      })
      .map((n) => n.id);
    const reachable = new Set<string>();
    const order: string[] = [];
    while (queue.length > 0) {
      const cur = queue.shift()!;
      if (reachable.has(cur) || disabledIds.has(cur)) continue;
      reachable.add(cur);
      order.push(cur);
      for (const succ of outgoing.get(cur) ?? []) queue.push(succ);
    }
    const skipped = new Set<string>(liveNodes.filter((n) => !reachable.has(n.id)).map((n) => n.id));
    setSimulation({ reachable, skipped, order });
  }, [nodes, edges]);

  const clearSimulation = useCallback(() => setSimulation(null), []);

  return { simulation, revealIndex, runSimulation, clearSimulation };
}
