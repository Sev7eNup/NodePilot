import { useCallback, type RefObject } from 'react';
import type { Node, Edge } from '@xyflow/react';
import { WORKFLOW_SNIPPETS, insertSnippet } from '../lib/workflowSnippets';
import { getSmartDefaults } from '../lib/lastSimilarNode';
import { getCustomActivityFacts } from '../lib/customActivities';
import { randomUuid } from '../lib/uuid';

type SelectedItem = { type: 'node' | 'edge'; id: string } | null;

export interface NodeOperationsApi {
  /** Add a single activity (or stickyNote) at viewport top-left + jitter. */
  addNode: (type: string, label: string) => void;
  /** Insert a multi-node snippet centered in the viewport. */
  addSnippet: (snippetId: string) => void;
  /** Clone an existing node: same data, slightly offset position, selected. */
  duplicateNode: (nodeId: string) => void;
  /** Remove a node and any edges touching it. Clears selection if the deleted node was selected. */
  deleteNodeById: (nodeId: string) => void;
  /** Wrap currently selected (non-group) nodes in a new group node (Ctrl+G). */
  groupSelection: () => void;
}

/**
 * Hosts the create, clone, delete and group operations the editor exposes through toolbar,
 * context menu and shortcuts. Every operation commits history before mutating, so Ctrl+Z
 * reverts the whole action.
 */
export function useNodeOperations({
  nodes,
  setNodes,
  edges,
  setEdges,
  selected,
  setSelected,
  commitHistory,
  canvasRef,
  screenToFlowPosition,
}: {
  nodes: Node[];
  setNodes: React.Dispatch<React.SetStateAction<Node[]>>;
  edges: Edge[];
  setEdges: React.Dispatch<React.SetStateAction<Edge[]>>;
  selected: SelectedItem;
  setSelected: (s: SelectedItem) => void;
  commitHistory: (label?: string) => void;
  canvasRef: RefObject<HTMLElement | null>;
  screenToFlowPosition: (pos: { x: number; y: number }) => { x: number; y: number };
}): NodeOperationsApi {
  const addNode = useCallback((type: string, label: string) => {
    let position = { x: 100, y: 100 };
    const rect = canvasRef.current?.getBoundingClientRect();
    if (rect) {
      const base = screenToFlowPosition({ x: rect.left + 60, y: rect.top + 60 });
      const jitter = (nodes.length % 6) * 24;
      position = { x: base.x + jitter, y: base.y + jitter };
    }
    // Sticky notes use their own node type (own styling, no connection handles) and the engine
    // skips them via data.disabled=true, even if an imported JSON attaches an edge to one.
    const isNote = type === 'note';
    // Copies machine and credential from the last sibling of the same type (or base URL for
    // restApi, provider and connectionRef for sql, isHtml for emailNotification). Empty when no
    // sibling exists, so the first node of a type still gets full defaults.
    const smart = isNote ? {} : getSmartDefaults(type, nodes);
    // Custom activities (custom:<key>) carry their definition reference in the node config:
    // __customDefinitionId is the authoritative link the executor loads, __customKey is the drift
    // cross-check. Declared input defaults are seeded so the node is runnable right away.
    const customFacts = isNote ? undefined : getCustomActivityFacts(type);
    const activityConfig: Record<string, unknown> = { ...(smart.config ?? {}) };
    if (customFacts) {
      activityConfig.__customDefinitionId = customFacts.id;
      activityConfig.__customKey = customFacts.key;
      for (const inp of customFacts.inputs) {
        if (inp.default != null && activityConfig[inp.name] === undefined) activityConfig[inp.name] = inp.default;
      }
    }
    const newNode: Node = isNote
      ? {
          id: `note-${randomUuid()}`,
          type: 'stickyNote',
          position,
          // Default dimensions give NodeResizer a concrete frame from the start, so a new note
          // can be dragged and resized from its corners and edges right away.
          style: { width: 220, height: 120 },
          data: { label: 'Note', activityType: 'note', text: 'Double-click to edit…', disabled: true },
        }
      : {
          id: `step-${randomUuid()}`,
          type: 'activity',
          position,
          data: {
            label,
            activityType: type,
            targetMachineId: smart.targetMachineId ?? null,
            credentialId: smart.credentialId ?? null,
            config: activityConfig,
          },
        };
    commitHistory('Add node');
    setNodes((nds: Node[]) => [...nds, newNode]);
    setSelected({ type: 'node', id: newNode.id });
  }, [canvasRef, screenToFlowPosition, nodes, commitHistory, setNodes, setSelected]);

  /** Origin is the viewport center minus half a snippet width, so the pattern is not glued to
   *  the top-left. New nodes land selected so the group can be dragged straight away. */
  const addSnippet = useCallback((snippetId: string) => {
    const snippet = WORKFLOW_SNIPPETS.find((s) => s.id === snippetId);
    if (!snippet) return;
    let origin = { x: 100, y: 100 };
    const rect = canvasRef.current?.getBoundingClientRect();
    if (rect) {
      origin = screenToFlowPosition({ x: rect.left + rect.width / 2 - 200, y: rect.top + rect.height / 2 - 100 });
    }
    commitHistory('Insert snippet');
    const result = insertSnippet(snippet, origin, nodes, edges);
    setNodes(result.nodes);
    setEdges(result.edges);
    if (result.newNodeIds.length > 0) {
      setSelected({ type: 'node', id: result.newNodeIds[0] });
    }
  }, [canvasRef, screenToFlowPosition, nodes, edges, commitHistory, setNodes, setEdges, setSelected]);

  const duplicateNode = useCallback((nodeId: string) => {
    const source = nodes.find((n) => n.id === nodeId);
    if (!source) return;
    const newId = `step-${randomUuid()}`;
    const newNode: Node = {
      ...source,
      id: newId,
      position: { x: source.position.x + 40, y: source.position.y + 40 },
      selected: false,
      data: { ...source.data as Record<string, unknown> },
    };
    commitHistory('Duplicate node');
    setNodes((nds: Node[]) => [...nds, newNode]);
    setSelected({ type: 'node', id: newId });
  }, [nodes, commitHistory, setNodes, setSelected]);

  const deleteNodeById = useCallback((nodeId: string) => {
    commitHistory('Delete');
    setNodes((nds: Node[]) => nds.filter((n) => n.id !== nodeId));
    setEdges((eds: Edge[]) => eds.filter((e) => e.source !== nodeId && e.target !== nodeId));
    if (selected?.id === nodeId) setSelected(null);
  }, [commitHistory, setNodes, setEdges, selected, setSelected]);

  /** The group node goes first in the array: React Flow renders in array order, so the group
   *  ends up beneath its children. */
  const groupSelection = useCallback(() => {
    const sel = nodes.filter((n) => n.selected && n.type !== 'group');
    if (sel.length === 0) return;
    const PAD_X = 28;
    const PAD_TOP = 52;
    const PAD_BOT = 24;
    const minX = Math.min(...sel.map((n) => n.position.x)) - PAD_X;
    const minY = Math.min(...sel.map((n) => n.position.y)) - PAD_TOP;
    const maxX = Math.max(...sel.map((n) => n.position.x + ((n.measured?.width as number) ?? 220))) + PAD_X;
    const maxY = Math.max(...sel.map((n) => n.position.y + ((n.measured?.height as number) ?? 60))) + PAD_BOT;
    const groupId = `group-${randomUuid()}`;
    const groupNode: Node = {
      id: groupId,
      type: 'group',
      position: { x: minX, y: minY },
      style: { width: maxX - minX, height: maxY - minY },
      data: {
        label: 'Group',
        color: 'blue',
        collapsed: false,
        expandedSize: { width: maxX - minX, height: maxY - minY },
        disabled: true,
        activityType: 'group',
      },
      selected: false,
    };
    const selIds = new Set(sel.map((n) => n.id));
    commitHistory('Group');
    setNodes((nds: Node[]) => [
      groupNode,
      ...nds.map((n) => {
        if (!selIds.has(n.id)) return { ...n, selected: false };
        return {
          ...n, selected: false, parentId: groupId,
          position: { x: n.position.x - minX, y: n.position.y - minY },
        };
      }),
    ]);
  }, [nodes, setNodes, commitHistory]);

  return { addNode, addSnippet, duplicateNode, deleteNodeById, groupSelection };
}
