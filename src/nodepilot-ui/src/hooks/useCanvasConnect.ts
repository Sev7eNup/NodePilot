import { useCallback, useState, type RefObject } from 'react';
import { addEdge, type Node, type Edge, type FinalConnectionState } from '@xyflow/react';
import { DEFAULT_SOURCE_PORT, edgeSourcePort, edgeTargetPort, normalizePort, oppositePort } from '../lib/edgePorts';
import { randomUuid } from '../lib/uuid';

type SelectedItem = { type: 'node' | 'edge'; id: string } | null;

export type QuickConnectState = {
  fromNodeId: string;
  fromHandleId: string | null;
  screenX: number;
  screenY: number;
  flowPosition: { x: number; y: number };
} | null;

export type EdgeInsertState = { edgeId: string; x: number; y: number } | null;

export interface CanvasConnectApi {
  /** Quick-connect overlay state. Set when a connection drag ends in empty space. */
  quickConnect: QuickConnectState;
  setQuickConnect: (s: QuickConnectState) => void;
  /** Pass to ReactFlow's onConnectEnd. Opens quick-connect when the drag ends without a target. */
  handleConnectEnd: (event: MouseEvent | TouchEvent, connectionState: FinalConnectionState) => void;
  /** Pick handler for the quick-connect overlay. Creates the node and edge, selects the node. */
  handleQuickConnectPick: (type: string, label: string) => void;

  /** Inline-insert overlay state. Set when the `+` on an edge is clicked. */
  insertAt: EdgeInsertState;
  setInsertAt: (s: EdgeInsertState) => void;
  /** Pass through EdgeInsertContext so labeled edges can request the picker. */
  requestInsert: (edgeId: string, x: number, y: number) => void;
  /** Splits the target edge so the new node sits between its source and target. The condition
   *  stays on the second hop. */
  insertOnEdge: (type: string, label: string) => void;
}

/**
 * Quick-connect (drag a handle into empty canvas, pick a type, get a new node and edge) and
 * inline insert on an edge (click the `+` on an edge, pick a type, and the edge is split so the
 * new node sits between its source and target). Both flows share the same state shape and the
 * same `setNodes`, `setEdges` and history callbacks.
 */
export function useCanvasConnect({
  edges,
  setNodes,
  setEdges,
  setSelected,
  commitHistory,
  canvasRef,
  screenToFlowPosition,
}: {
  edges: Edge[];
  setNodes: React.Dispatch<React.SetStateAction<Node[]>>;
  setEdges: React.Dispatch<React.SetStateAction<Edge[]>>;
  setSelected: (s: SelectedItem) => void;
  commitHistory: (label?: string) => void;
  canvasRef: RefObject<HTMLElement | null>;
  screenToFlowPosition: (pos: { x: number; y: number }) => { x: number; y: number };
}): CanvasConnectApi {
  const [quickConnect, setQuickConnect] = useState<QuickConnectState>(null);

  const handleConnectEnd = useCallback((event: MouseEvent | TouchEvent, connectionState: FinalConnectionState) => {
    if (connectionState.toHandle !== null || connectionState.toNode !== null) return;
    if (!connectionState.fromNode) return;
    const clientX = 'clientX' in event ? (event as MouseEvent).clientX : (event as TouchEvent).touches[0]?.clientX ?? 0;
    const clientY = 'clientY' in event ? (event as MouseEvent).clientY : (event as TouchEvent).touches[0]?.clientY ?? 0;
    // QuickConnectPicker is portaled to document.body and positioned `fixed`, so it needs
    // viewport coordinates (clientX/clientY), not canvas-relative offsets.
    if (!canvasRef.current) return;
    setQuickConnect({
      fromNodeId: connectionState.fromNode.id,
      fromHandleId: connectionState.fromHandle?.id ?? null,
      screenX: clientX,
      screenY: clientY,
      flowPosition: screenToFlowPosition({ x: clientX, y: clientY }),
    });
  }, [canvasRef, screenToFlowPosition]);

  const handleQuickConnectPick = useCallback((type: string, label: string) => {
    if (!quickConnect) return;
    const newNodeId = `step-${randomUuid()}`;
    const newNode: Node = {
      id: newNodeId,
      type: 'activity',
      position: quickConnect.flowPosition,
      data: { label, activityType: type, targetMachineId: null, credentialId: null, config: {} },
    };
    const newEdge: Edge = {
      id: `edge-${randomUuid()}`,
      source: quickConnect.fromNodeId,
      target: newNodeId,
      sourceHandle: normalizePort(quickConnect.fromHandleId, DEFAULT_SOURCE_PORT),
      targetHandle: oppositePort(normalizePort(quickConnect.fromHandleId, DEFAULT_SOURCE_PORT)),
      type: 'labeled',
      data: { label: '', condition: '', disabled: false },
    };
    commitHistory('Add node');
    setNodes((nds: Node[]) => [...nds, newNode]);
    setEdges((eds: Edge[]) => addEdge(newEdge, eds));
    setSelected({ type: 'node', id: newNodeId });
    setQuickConnect(null);
  }, [quickConnect, commitHistory, setNodes, setEdges, setSelected]);

  const [insertAt, setInsertAt] = useState<EdgeInsertState>(null);

  const requestInsert = useCallback((edgeId: string, x: number, y: number) => {
    setInsertAt({ edgeId, x, y });
  }, []);

  const insertOnEdge = useCallback((type: string, label: string) => {
    if (!insertAt) return;
    const target = edges.find((e) => e.id === insertAt.edgeId);
    if (!target) { setInsertAt(null); return; }
    commitHistory('Insert node');
    const newNodeId = `step-${randomUuid()}`;
    // Both edge halves share the same UUID prefix so they stay recognizable as a pair.
    const edgePairId = randomUuid();
    const newNode: Node = {
      id: newNodeId,
      type: 'activity',
      position: { x: insertAt.x - 100, y: insertAt.y - 40 },
      data: { label, activityType: type, targetMachineId: null, credentialId: null, config: {} },
    };
    // The first half is always unconditional, because the original condition belongs to what
    // runs after the new node. The second half inherits label and condition, so a path such as
    // "On Success" keeps its meaning.
    const firstHalf: Edge = {
      id: `edge-${edgePairId}-a`,
      source: target.source,
      target: newNodeId,
      sourceHandle: edgeSourcePort(target),
      targetHandle: oppositePort(edgeSourcePort(target)),
      type: 'labeled',
      data: { label: '', condition: '', disabled: false },
    };
    const secondHalf: Edge = {
      ...target,
      id: `edge-${edgePairId}-b`,
      source: newNodeId,
      sourceHandle: oppositePort(edgeTargetPort(target)),
      target: target.target,
      targetHandle: edgeTargetPort(target),
    };
    setNodes((nds: Node[]) => [...nds, newNode]);
    setEdges((eds: Edge[]) => eds.filter((e) => e.id !== target.id).concat([firstHalf, secondHalf]));
    setInsertAt(null);
    setSelected({ type: 'node', id: newNodeId });
  }, [insertAt, edges, setNodes, setEdges, setSelected, commitHistory]);

  return {
    quickConnect, setQuickConnect, handleConnectEnd, handleQuickConnectPick,
    insertAt, setInsertAt, requestInsert, insertOnEdge,
  };
}
