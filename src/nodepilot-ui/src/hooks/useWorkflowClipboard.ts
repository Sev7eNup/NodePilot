import { useRef, useCallback } from 'react';
import { type Node, type Edge, useReactFlow } from '@xyflow/react';

export function useWorkflowClipboard(commitHistory: (label?: string) => void) {
  const { getNodes, getEdges, setNodes, setEdges } = useReactFlow();
  const pasteCountRef = useRef(0);
  const selectionRef = useRef<{ nodeIds: string[]; edgeIds: string[] }>({ nodeIds: [], edgeIds: [] });

  const resetPasteCount = useCallback(() => { pasteCountRef.current = 0; }, []);

  const updateSelection = useCallback((sel: { nodeIds: string[]; edgeIds: string[] }) => {
    selectionRef.current = sel;
  }, []);

  const copySelection = useCallback(() => {
    const { nodeIds, edgeIds } = selectionRef.current;
    if (nodeIds.length === 0 && edgeIds.length === 0) return;
    const nodes = getNodes();
    const edges = getEdges();
    const nodeIdSet = new Set(nodeIds);
    const selectedNodes = nodes.filter((n) => nodeIdSet.has(n.id));
    // Edges only copied when both endpoints are in the selection.
    const selectedEdges = edges.filter((e) =>
      (edgeIds.includes(e.id) || (nodeIdSet.has(e.source) && nodeIdSet.has(e.target)))
      && nodeIdSet.has(e.source) && nodeIdSet.has(e.target)
    );
    if (selectedNodes.length === 0) return;
    sessionStorage.setItem('np_clipboard', JSON.stringify({
      nodes: selectedNodes.map((n) => ({ ...n, data: { ...(n.data as object) } as Node['data'] })),
      edges: selectedEdges.map((e) => ({ ...e, data: e.data ? { ...(e.data as object) } : undefined })),
    }));
    pasteCountRef.current = 0;
  }, [getNodes, getEdges]);

  const pasteBuffer = useCallback(() => {
    const raw = sessionStorage.getItem('np_clipboard');
    const buf: { nodes: Node[]; edges: Edge[] } | null = raw ? JSON.parse(raw) : null;
    if (!buf || buf.nodes.length === 0) return;
    pasteCountRef.current += 1;
    const offset = 40 * pasteCountRef.current;
    const idMap = new Map<string, string>();
    // Groups first, so children's parentId references can be remapped to the new group IDs.
    const sorted = [...buf.nodes].sort((a) => a.type === 'group' ? -1 : 1);
    const newNodes: Node[] = sorted.map((n) => {
      const prefix = n.type === 'group' ? 'group' : 'step';
      const newId = `${prefix}-${crypto.randomUUID().slice(0, 8)}`;
      idMap.set(n.id, newId);
      return {
        ...n,
        id: newId,
        parentId: n.parentId ? (idMap.get(n.parentId) ?? n.parentId) : undefined,
        position: { x: (n.position?.x ?? 0) + offset, y: (n.position?.y ?? 0) + offset },
        selected: true,
        data: { ...(n.data as Record<string, unknown>), __liveStatus: undefined } as Node['data'],
      };
    });
    const newEdges: Edge[] = buf.edges.map((e) => ({
      ...e,
      id: `edge-${crypto.randomUUID().slice(0, 8)}`,
      source: idMap.get(e.source)!,
      target: idMap.get(e.target)!,
      selected: true,
    }));
    commitHistory('Paste');
    setNodes((nds: Node[]) => [...nds.map((n) => ({ ...n, selected: false })), ...newNodes]);
    setEdges((eds: Edge[]) => [...eds.map((e) => ({ ...e, selected: false })), ...newEdges]);
  }, [commitHistory, setNodes, setEdges]);

  return { copySelection, pasteBuffer, resetPasteCount, updateSelection };
}
