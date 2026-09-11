import { useEffect, useState } from 'react';
import type { Node, Edge } from '@xyflow/react';

export interface SettledGraph {
  nodes: Node[];
  edges: Edge[];
}

/**
 * The graph as it stood after the last pause in editing.
 *
 * The editor runs React Flow controlled, so a drag replaces the node array on every mouse
 * move. Anything that walks the whole graph belongs on this value instead of on the live
 * one: a continuous drag keeps resetting the timer, so the work runs once after the drop
 * rather than once per frame.
 *
 * Nodes and edges settle together — a lint or layout pass that saw edges from one frame and
 * nodes from another would be reading a graph that never existed.
 */
export function useSettledGraph(nodes: Node[], edges: Edge[], delayMs = 250): SettledGraph {
  const [settled, setSettled] = useState<SettledGraph>({ nodes, edges });

  useEffect(() => {
    const timer = globalThis.setTimeout(() => setSettled({ nodes, edges }), delayMs);
    return () => globalThis.clearTimeout(timer);
  }, [nodes, edges, delayMs]);

  return settled;
}
