import { useState, useEffect, useCallback } from 'react';
import { type Node, type Edge } from '@xyflow/react';
import { simulateWorkflow, type SimulationResult } from '../lib/workflowSimulation';

export type { SimulationResult };

export function useWorkflowSimulation(nodes: Node[], edges: Edge[]) {
  const [simulation, setSimulation] = useState<SimulationResult | null>(null);
  const [revealIndex, setRevealIndex] = useState(0);

  useEffect(() => {
    if (!simulation) { setRevealIndex(0); return; }
    if (revealIndex >= simulation.order.length) return;
    // Reveal one step every 180 ms so the flow stays readable.
    const t = globalThis.setTimeout(() => setRevealIndex((i) => i + 1), 180);
    return () => globalThis.clearTimeout(t);
  }, [simulation, revealIndex]);

  const runSimulation = useCallback(() => {
    if (nodes.length === 0) return;
    setSimulation(simulateWorkflow(nodes, edges));
  }, [nodes, edges]);

  const clearSimulation = useCallback(() => setSimulation(null), []);

  return { simulation, revealIndex, runSimulation, clearSimulation };
}
