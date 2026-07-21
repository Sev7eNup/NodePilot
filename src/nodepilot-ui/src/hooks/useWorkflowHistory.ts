import { useState, useEffect, useCallback } from 'react';
import { type Node, type Edge, useReactFlow } from '@xyflow/react';

export type HistorySnapshot = { nodes: Node[]; edges: Edge[]; label?: string };

export function useWorkflowHistory(workflowId: string | undefined) {
  const { getNodes, getEdges, setNodes, setEdges } = useReactFlow();
  const [historyPast, setHistoryPast] = useState<HistorySnapshot[]>([]);
  const [historyFuture, setHistoryFuture] = useState<HistorySnapshot[]>([]);

  useEffect(() => { setHistoryPast([]); setHistoryFuture([]); }, [workflowId]);

  const commitHistory = useCallback((label?: string) => {
    const snap: HistorySnapshot = { nodes: getNodes(), edges: getEdges(), label };
    setHistoryPast((p) => {
      const next = [...p, snap];
      if (next.length > 50) next.shift();
      return next;
    });
    setHistoryFuture([]);
  }, [getNodes, getEdges]);

  const undo = useCallback(() => {
    setHistoryPast((past) => {
      if (past.length === 0) return past;
      const last = past[past.length - 1];
      setHistoryFuture((f) => [...f, { nodes: getNodes(), edges: getEdges() }]);
      setNodes(last.nodes);
      setEdges(last.edges);
      return past.slice(0, -1);
    });
  }, [getNodes, getEdges, setNodes, setEdges]);

  const redo = useCallback(() => {
    setHistoryFuture((future) => {
      if (future.length === 0) return future;
      const next = future[future.length - 1];
      setHistoryPast((p) => [...p, { nodes: getNodes(), edges: getEdges() }]);
      setNodes(next.nodes);
      setEdges(next.edges);
      return future.slice(0, -1);
    });
  }, [getNodes, getEdges, setNodes, setEdges]);

  return { historyPast, historyFuture, commitHistory, undo, redo };
}
