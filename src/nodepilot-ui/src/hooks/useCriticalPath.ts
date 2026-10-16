import { useMemo } from 'react';
import type { Node, Edge } from '@xyflow/react';

export interface CriticalPathAnnotation {
  isCritical: boolean;
  slack: number;       // ms of slack (0 = on critical path)
  earliestStart: number; // ms from workflow start
  duration: number;    // p95 duration in ms
}

/** Step stats per node id, as served by /step-stats. Only the p95 duration is read. */
export type CriticalPathStats = Readonly<Record<string, { p95DurationMs?: unknown }>> | undefined;

/** What the CPM pass reads: active node ids with their p95 duration, and active edges. */
interface CpmInput {
  nodes: [id: string, p95DurationMs: number][];
  edges: [source: string, target: string][];
}

/**
 * Reduces the graph to the CPM input. Disabled nodes are treated as removed, together with
 * their edges; otherwise their outgoing edges keep successors' in-degree above zero and the
 * successor never reaches the topological queue. Nodes without stats default to 0 ms.
 */
function buildCpmInput(nodes: Node[], edges: Edge[], stats: CriticalPathStats): CpmInput {
  const active = new Set<string>();
  const cpmNodes: CpmInput['nodes'] = [];
  for (const n of nodes) {
    const data = n.data as Record<string, unknown> | undefined;
    if (data?.disabled) continue;
    active.add(n.id);
    const p95Ms = stats?.[n.id]?.p95DurationMs;
    const p95 = typeof p95Ms === 'number' ? Math.max(0, p95Ms) : 0;
    cpmNodes.push([n.id, p95]);
  }
  const cpmEdges: CpmInput['edges'] = [];
  for (const e of edges) {
    if ((e.data as Record<string, unknown> | undefined)?.disabled) continue;
    if (!active.has(e.source) || !active.has(e.target)) continue;
    cpmEdges.push([e.source, e.target]);
  }
  return { nodes: cpmNodes, edges: cpmEdges };
}

/** Critical Path Method over the reduced graph: forward pass, backward pass, slack. */
function computeCriticalPath({ nodes, edges }: CpmInput): ReadonlyMap<string, CriticalPathAnnotation> {
  const duration = new Map(nodes);
  const adj = new Map<string, string[]>();        // nodeId -> downstream nodeIds
  const reverseAdj = new Map<string, string[]>(); // nodeId -> upstream nodeIds
  const inDegree = new Map<string, number>();
  for (const [id] of nodes) {
    adj.set(id, []);
    reverseAdj.set(id, []);
    inDegree.set(id, 0);
  }
  for (const [source, target] of edges) {
    adj.get(source)?.push(target);
    reverseAdj.get(target)?.push(source);
    inDegree.set(target, (inDegree.get(target) ?? 0) + 1);
  }
  const getDuration = (id: string): number => duration.get(id) ?? 0;

  // Topological sort (Kahn's algorithm)
  const topo: string[] = [];
  const queue: string[] = [];
  for (const [id] of nodes) {
    if ((inDegree.get(id) ?? 0) === 0) queue.push(id);
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

  const annotations = new Map<string, CriticalPathAnnotation>();
  if (topo.length === 0) return annotations;

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

  // Backward pass in reverse topological order: latest start
  const latestStart = new Map<string, number>();
  for (const id of [...topo].reverse()) {
    const successors = adj.get(id) ?? [];
    let minSuccStart = maxFinish;
    for (const succ of successors) {
      const succStart = latestStart.get(succ) ?? maxFinish;
      if (succStart < minSuccStart) minSuccStart = succStart;
    }
    // A terminal node may finish as late as the overall end.
    latestStart.set(id, minSuccStart - getDuration(id));
  }

  // Compute slack and critical path
  for (const id of topo) {
    const es = earliestStart.get(id) ?? 0;
    const ls = latestStart.get(id) ?? 0;
    const slack = ls - es;
    annotations.set(id, {
      isCritical: Math.abs(slack) < 1, // sub-ms tolerance for floating point
      slack: Math.max(0, Math.round(slack)),
      earliestStart: Math.round(es),
      duration: Math.round(getDuration(id)),
    });
  }
  return annotations;
}

/**
 * Critical-path annotations per node id, or null while the overlay is off. A pure computation:
 * useDisplayedGraph merges the result into the projection, so the overlay never writes into
 * the authored graph.
 *
 * The memo is keyed on the serialized CPM input rather than on `nodes`, which changes on every
 * frame of a drag. A move keeps the same key, so the annotation objects stay the same and the
 * node components' memo comparators hold.
 */
export function useCriticalPath(
  enabled: boolean,
  nodes: Node[],
  edges: Edge[],
  stats: CriticalPathStats,
): ReadonlyMap<string, CriticalPathAnnotation> | null {
  const inputKey = enabled ? JSON.stringify(buildCpmInput(nodes, edges, stats)) : null;
  return useMemo(() => {
    if (inputKey === null) return null;
    const input: CpmInput = JSON.parse(inputKey);
    return computeCriticalPath(input);
  }, [inputKey]);
}
