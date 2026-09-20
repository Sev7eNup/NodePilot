import { TRIGGER_ACTIVITY_TYPES } from './activityCatalog.generated';

/** Minimal node shape the walk needs, so callers outside React Flow can use it too. */
export interface SimulationNode {
  id: string;
  data?: Record<string, unknown>;
}

/** Minimal edge shape the walk needs. */
export interface SimulationEdge {
  source: string;
  target: string;
  data?: Record<string, unknown>;
}

export type SimulationResult = {
  reachable: Set<string>;
  skipped: Set<string>;
  order: string[];
};

/**
 * Forward walk of a workflow graph from its trigger nodes, matching the engine's rules:
 * roots are trigger nodes only, disabled nodes and disabled edges are not traversed, and a
 * graph without an enabled trigger reaches nothing. Failure edges are left out because the
 * walk models a successful run.
 *
 * Pure, so the designer preview and the demo backend share one definition of "what runs".
 */
export function simulateWorkflow(nodes: SimulationNode[], edges: SimulationEdge[]): SimulationResult {
  const activeEdges = edges.filter((e) => {
    const d = e.data ?? {};
    if (d.disabled) return false;
    const cond = (d.condition as string) || '';
    if (cond.endsWith('.failed')) return false;
    return true;
  });
  const outgoing = new Map<string, string[]>();
  const liveNodes = nodes.filter((n) => n.data?.activityType !== 'note');
  for (const n of liveNodes) outgoing.set(n.id, []);
  const liveIds = new Set(liveNodes.map((n) => n.id));
  for (const e of activeEdges) {
    if (!liveIds.has(e.source) || !liveIds.has(e.target)) continue;
    outgoing.get(e.source)!.push(e.target);
  }
  const disabledIds = new Set(liveNodes.filter((n) => n.data?.disabled).map((n) => n.id));
  const queue: string[] = liveNodes
    .filter((n) => {
      const d = n.data ?? {};
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
  return { reachable, skipped, order };
}
