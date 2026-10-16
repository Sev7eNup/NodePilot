import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node, Edge } from '@xyflow/react';
import { classifyReattachTarget, resolveDockTarget } from '../lib/edgeDetach';
import { edgeTargetPort } from '../lib/edgePorts';
import type { WorkflowGraphSource } from './useWorkflowHistory';

type SelectedItem = { type: 'node'; id: string } | { type: 'edge'; id: string } | null;

interface UseEdgeDetachArgs {
  graph: WorkflowGraphSource;
  canWrite: boolean;
  setSelected: (selected: SelectedItem) => void;
  commitHistory: (label?: string) => void;
  markDirty: () => void;
  showConnectionNotice: (message: string) => void;
}

/**
 * Detaching an edge's target end (edge context menu -> "Detach target") and reattaching it with a
 * click on another node.
 *
 * The state is transient: the edge only changes on a successful click on the new target, so a
 * cancel just resets the state, with no history entry and no dirty flag.
 */
export function useEdgeDetach({
  graph, canWrite, setSelected, commitHistory, markDirty, showConnectionNotice,
}: UseEdgeDetachArgs) {
  const { t } = useTranslation(['editor']);
  const { nodes, edges, setEdges } = graph;
  const [detachedEdgeId, setDetachedEdgeId] = useState<string | null>(null);
  // Node the preview line is currently docking to. Changes only when the pointer crosses a
  // node boundary, which is when EdgeDetachPreview reports, not on every mouse move.
  const [dockTargetNodeId, setDockTargetNodeId] = useState<string | null>(null);
  const cancelEdgeDetach = useCallback(() => {
    setDetachedEdgeId(null);
    setDockTargetNodeId(null);
  }, []);

  /**
   * A right-click cancels a running detach anywhere on the canvas, whether the cursor is over
   * a node, an edge or empty space. The click is consumed: neither the app context menu nor
   * the browser one opens, since right-click is the usual gesture for cancelling a pending
   * operation and opening a menu would force the user to dismiss it again.
   *
   * Returns true when the click was consumed as a cancel.
   */
  const consumeContextMenuAsDetachCancel = useCallback((e: React.MouseEvent | MouseEvent): boolean => {
    if (!detachedEdgeId) return false;
    e.preventDefault();
    cancelEdgeDetach();
    return true;
  }, [detachedEdgeId, cancelEdgeDetach]);

  const beginEdgeDetach = useCallback((edgeId: string) => {
    // Keep the edge selected so EdgePropertiesPanel shows the condition that moves with it.
    setSelected({ type: 'edge', id: edgeId });
    setDetachedEdgeId(edgeId);
  }, [setSelected]);

  /**
   * Handles a node click while an edge target is detached and reports whether it consumed the
   * click. Invalid targets show a notice and keep the detach active.
   *
   * The current target remains valid so users can change only the target port.
   */
  const completeEdgeReattach = useCallback((event: React.MouseEvent, node: Node): boolean => {
    if (!detachedEdgeId) return false;
    const edge = edges.find((e) => e.id === detachedEdgeId);
    if (!edge) { cancelEdgeDetach(); return true; }
    const verdict = classifyReattachTarget({
      edges,
      edgeId: edge.id,
      sourceId: edge.source,
      candidate: { id: node.id, type: node.type },
    });
    if (verdict !== 'ok') {
      showConnectionNotice(t(`editor:edgeDetach.${verdict}`));
      return true;
    }
    // Use the preview resolver so the committed port matches the click position. Preserve the
    // current targetHandle if no handle can be resolved.
    const dock = resolveDockTarget(event.target, event.clientX, event.clientY, (id) => id === node.id);
    const nextPort = dock?.port ?? edgeTargetPort(edge);
    // Returning to the same node and port must not create an empty undo step or dirty state.
    if (node.id === edge.target && nextPort === edgeTargetPort(edge)) {
      cancelEdgeDetach();
      return true;
    }
    // Drag and click reconnection share the same history action.
    commitHistory('Move edge');
    markDirty();
    setEdges((eds: Edge[]) => eds.map((e) => (
      e.id === edge.id ? { ...e, target: node.id, targetHandle: nextPort } : e
    )));
    cancelEdgeDetach();
    return true;
  }, [detachedEdgeId, edges, cancelEdgeDetach, commitHistory, markDirty, setEdges, showConnectionNotice, t]);

  // Preview and hover accept the same targets as the click, including the current target.
  const canDockTo = useCallback((nodeId: string): boolean => {
    if (!detachedEdgeId) return false;
    const edge = edges.find((e) => e.id === detachedEdgeId);
    const candidate = nodes.find((n) => n.id === nodeId);
    if (!edge || !candidate) return false;
    return classifyReattachTarget({
      edges,
      edgeId: edge.id,
      sourceId: edge.source,
      candidate: { id: candidate.id, type: candidate.type },
    }) === 'ok';
  }, [detachedEdgeId, edges, nodes]);

  // Cancel detachment when write access is lost or the edge no longer exists.
  useEffect(() => {
    if (!detachedEdgeId) return;
    if (!canWrite || !edges.some((e) => e.id === detachedEdgeId)) cancelEdgeDetach();
  }, [detachedEdgeId, canWrite, edges, cancelEdgeDetach]);

  // The raw detached edge supplies source, ports, and label to the preview and notice.
  const detachedEdge = detachedEdgeId ? edges.find((e) => e.id === detachedEdgeId) ?? null : null;

  return {
    detachedEdgeId,
    detachedEdge,
    dockTargetNodeId,
    setDockTargetNodeId,
    cancelEdgeDetach,
    consumeContextMenuAsDetachCancel,
    beginEdgeDetach,
    completeEdgeReattach,
    canDockTo,
  };
}
