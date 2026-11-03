import { useState, useEffect, useRef, useCallback, type Dispatch, type SetStateAction } from 'react';
import { type Node, type Edge } from '@xyflow/react';

export type HistorySnapshot = { nodes: Node[]; edges: Edge[]; label?: string };

/**
 * The editor's own graph state plus its setters. Anything that snapshots or rewrites the
 * graph takes this instead of reaching into React Flow's store, which holds the projected
 * graph rather than the source of truth.
 */
export interface WorkflowGraphSource {
  nodes: Node[];
  edges: Edge[];
  setNodes: Dispatch<SetStateAction<Node[]>>;
  setEdges: Dispatch<SetStateAction<Edge[]>>;
}

/**
 * Undo/redo over the editor's graph state.
 *
 * Snapshots come from that state and never from React Flow's store. The store holds the
 * projected graph, and a collapsed group is projected without its child nodes — snapshotting
 * that and undoing it would delete the children for real. The projection also carries
 * render-only fields (in-degree, lint counts, hidden, className) that would become
 * authoritative on undo and end up in the saved definition.
 */
export function useWorkflowHistory(
  workflowId: string | undefined,
  source: WorkflowGraphSource,
  markDirty: () => void,
) {
  const [historyPast, setHistoryPast] = useState<HistorySnapshot[]>([]);
  const [historyFuture, setHistoryFuture] = useState<HistorySnapshot[]>([]);
  const propertyBurst = useRef<ReturnType<typeof setTimeout> | null>(null);
  const endPropertyBurst = useCallback(() => {
    if (propertyBurst.current !== null) clearTimeout(propertyBurst.current);
    propertyBurst.current = null;
  }, []);

  // Held in a ref so commitHistory keeps one identity: it is a dependency of most graph
  // callbacks, and a drag replaces the node array on every frame.
  const sourceRef = useRef(source);
  useEffect(() => { sourceRef.current = source; });

  useEffect(() => {
    endPropertyBurst();
    setHistoryPast([]);
    setHistoryFuture([]);
    return endPropertyBurst;
  }, [workflowId, endPropertyBurst]);

  const commitHistory = useCallback((label?: string) => {
    endPropertyBurst();
    const { nodes, edges } = sourceRef.current;
    setHistoryPast((p) => {
      const next = [...p, { nodes, edges, label }];
      if (next.length > 50) next.shift();
      return next;
    });
    setHistoryFuture([]);
  }, [endPropertyBurst]);

  // Capture before the first mutation. Only the end of the typing burst is delayed;
  // the snapshot must already exist if the author immediately presses Undo.
  const commitPropertyEdit = useCallback(() => {
    if (propertyBurst.current === null) commitHistory('Edit property');
    else clearTimeout(propertyBurst.current);
    propertyBurst.current = setTimeout(() => { propertyBurst.current = null; }, 800);
  }, [commitHistory]);

  // Undo and redo replace the graph wholesale through the setters, which does not emit
  // onNodesChange. Without markDirty the restored graph would never reach save or autosave.
  const undo = useCallback(() => {
    endPropertyBurst();
    if (historyPast.length === 0) return;
    const last = historyPast[historyPast.length - 1];
    const { nodes, edges, setNodes, setEdges } = sourceRef.current;
    setHistoryFuture((f) => [...f, { nodes, edges }]);
    setHistoryPast((p) => p.slice(0, -1));
    markDirty();
    setNodes(last.nodes);
    setEdges(last.edges);
  }, [historyPast, markDirty, endPropertyBurst]);

  const redo = useCallback(() => {
    endPropertyBurst();
    if (historyFuture.length === 0) return;
    const next = historyFuture[historyFuture.length - 1];
    const { nodes, edges, setNodes, setEdges } = sourceRef.current;
    setHistoryPast((p) => [...p, { nodes, edges }]);
    setHistoryFuture((f) => f.slice(0, -1));
    markDirty();
    setNodes(next.nodes);
    setEdges(next.edges);
  }, [historyFuture, markDirty, endPropertyBurst]);

  return { historyPast, historyFuture, commitHistory, commitPropertyEdit, undo, redo };
}
