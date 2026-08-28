import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';

/**
 * useWorkflowClipboard reads and writes the React Flow store via `useReactFlow()`.
 * The provider alone does not wire up getNodes/setNodes without a mounted <ReactFlow>,
 * so @xyflow/react is mocked and backed by a small in-test store. This keeps the tests
 * on the hook's clipboard semantics: selection rules, in-memory lifetime, paste offsets
 * and group/parent re-mapping.
 */

const store: { nodes: Node[]; edges: Edge[] } = { nodes: [], edges: [] };

vi.mock('@xyflow/react', () => ({
  useReactFlow: () => ({
    getNodes: () => store.nodes,
    getEdges: () => store.edges,
    setNodes: (updater: ((ns: Node[]) => Node[]) | Node[]) => {
      store.nodes = typeof updater === 'function' ? updater(store.nodes) : updater;
    },
    setEdges: (updater: ((es: Edge[]) => Edge[]) | Edge[]) => {
      store.edges = typeof updater === 'function' ? updater(store.edges) : updater;
    },
  }),
}));

import { useWorkflowClipboard } from '../../hooks/useWorkflowClipboard';

function makeNode(id: string, type: 'activity' | 'group' = 'activity', extra: Partial<Node> = {}): Node {
  return {
    id,
    type,
    position: { x: 100, y: 100 },
    data: { label: id, activityType: 'runScript' },
    selected: false,
    ...extra,
  };
}

function makeEdge(id: string, source: string, target: string): Edge {
  return { id, source, target, type: 'labeled', data: { label: 'On Success' }, selected: false };
}

function setup(initialNodes: Node[], initialEdges: Edge[]) {
  store.nodes = initialNodes;
  store.edges = initialEdges;
  // Stub crypto.randomUUID so ids of pasted nodes and edges are deterministic.
  let idCounter = 0;
  vi.spyOn(globalThis.crypto, 'randomUUID').mockImplementation(() => {
    idCounter += 1;
    return `00000000-0000-0000-0000-${String(idCounter).padStart(12, '0')}` as `${string}-${string}-${string}-${string}-${string}`;
  });

  const commitHistory = vi.fn();
  const hook = renderHook(() => useWorkflowClipboard(commitHistory));
  return { ...hook, commitHistory };
}

describe('useWorkflowClipboard', () => {
  beforeEach(() => {
    sessionStorage.clear();
    store.nodes = [];
    store.edges = [];
  });

  it('copySelection_keepsSelectedNodesOutOfBrowserStorage', () => {
    const a = makeNode('a');
    const b = makeNode('b');
    const c = makeNode('c');
    const eAB = makeEdge('eAB', 'a', 'b');
    const eBC = makeEdge('eBC', 'b', 'c');
    const { result } = setup([a, b, c], [eAB, eBC]);

    act(() => result.current.updateSelection({ nodeIds: ['a', 'b'], edgeIds: [] }));
    act(() => result.current.copySelection());

    expect(sessionStorage.getItem('np_clipboard')).toBeNull();

    act(() => result.current.pasteBuffer());
    const pastedNodes = store.nodes.filter((n) => n.selected);
    expect(pastedNodes).toHaveLength(2);
    expect(store.edges.filter((e) => e.selected)).toHaveLength(1);
  });

  it('copySelection_dropsEdgesWithEndpointsOutsideSelection', () => {
    // An edge is copied only when both endpoints are selected, so a paste cannot create
    // a dangling edge pointing at a node outside the cloned subgraph.
    const a = makeNode('a');
    const b = makeNode('b');
    const c = makeNode('c');
    const eBC = makeEdge('eBC', 'b', 'c');
    const { result } = setup([a, b, c], [eBC]);

    act(() => result.current.updateSelection({ nodeIds: ['b'], edgeIds: ['eBC'] }));
    act(() => result.current.copySelection());

    act(() => result.current.pasteBuffer());
    expect(store.edges).toHaveLength(1);
    expect(store.edges.filter((edge) => edge.selected)).toHaveLength(0);
  });

  it('copySelection_emptySelection_doesNotWrite', () => {
    const a = makeNode('a');
    const { result } = setup([a], []);

    act(() => result.current.updateSelection({ nodeIds: [], edgeIds: [] }));
    act(() => result.current.copySelection());

    expect(sessionStorage.getItem('np_clipboard')).toBeNull();
  });

  it('pasteBuffer_appendsClonedNodesWithFreshIds_andCommitsHistory', () => {
    const a = makeNode('a');
    const b = makeNode('b');
    const eAB = makeEdge('eAB', 'a', 'b');
    const { result, commitHistory } = setup([a, b], [eAB]);

    act(() => result.current.updateSelection({ nodeIds: ['a', 'b'], edgeIds: [] }));
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());

    expect(commitHistory).toHaveBeenCalledOnce();

    expect(store.nodes).toHaveLength(4); // 2 originals + 2 pasted
    const pastedNodes = store.nodes.filter((n) => n.selected);
    expect(pastedNodes).toHaveLength(2);
    pastedNodes.forEach((n) => expect(n.id).toMatch(/^step-/));

    const pastedEdges = store.edges.filter((e) => e.selected);
    expect(pastedEdges).toHaveLength(1);
    expect(pastedEdges[0].id).toMatch(/^edge-/);
  });

  it('pasteBuffer_repeatedPaste_offsetsEachCloneFurther', () => {
    // Paste offsets accumulate: the first paste shifts by 40px, the second by 80px.
    const a = makeNode('a', 'activity');
    a.position = { x: 0, y: 0 };
    const { result } = setup([a], []);

    act(() => result.current.updateSelection({ nodeIds: ['a'], edgeIds: [] }));
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());
    act(() => result.current.pasteBuffer());

    const cloneOffsets = store.nodes
      .filter((n) => n.id !== 'a')
      .map((n) => n.position?.x ?? 0)
      .sort((p, q) => p - q);

    expect(cloneOffsets).toEqual([40, 80]);
  });

  it('pasteBuffer_resetsPasteCount_onCopy', () => {
    const a = makeNode('a', 'activity');
    a.position = { x: 0, y: 0 };
    const { result } = setup([a], []);

    act(() => result.current.updateSelection({ nodeIds: ['a'], edgeIds: [] }));
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());
    act(() => result.current.pasteBuffer());

    // Re-copy resets the offset baseline back to 40.
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());

    const lastClone = store.nodes.slice(-1)[0];
    expect(lastClone.position?.x).toBe(40);
  });

  it('pasteBuffer_emptyClipboard_isNoOp', () => {
    const { result, commitHistory } = setup([makeNode('a')], []);

    act(() => result.current.pasteBuffer());

    expect(commitHistory).not.toHaveBeenCalled();
    expect(store.nodes).toHaveLength(1);
  });

  it('clipboard_doesNotSurviveEditorUnmount', () => {
    const first = setup([makeNode('secret-node', 'activity', {
      data: { activityType: 'restApi', config: { apiKey: 'inline-secret' } },
    })], []);
    act(() => first.result.current.updateSelection({ nodeIds: ['secret-node'], edgeIds: [] }));
    act(() => first.result.current.copySelection());
    first.unmount();

    const second = setup([makeNode('viewer-node')], []);
    act(() => second.result.current.pasteBuffer());

    expect(store.nodes).toHaveLength(1);
    expect(sessionStorage.getItem('np_clipboard')).toBeNull();
  });

  it('pasteBuffer_groupNode_idMapPropagatesToChildParentId', () => {
    // The child's parentId references the group, so after a paste it must point at the
    // new group id. The hook sorts groups first so the id map is filled before children.
    const group = makeNode('group-1', 'group');
    const child = makeNode('child-1', 'activity', { parentId: 'group-1' });
    const { result } = setup([group, child], []);

    act(() => result.current.updateSelection({ nodeIds: ['group-1', 'child-1'], edgeIds: [] }));
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());

    const pastedGroup = store.nodes.find((n) => n.selected && n.type === 'group');
    const pastedChild = store.nodes.find((n) => n.selected && n.type === 'activity');
    expect(pastedGroup).toBeDefined();
    expect(pastedChild).toBeDefined();
    expect(pastedChild!.parentId).toBe(pastedGroup!.id);
    expect(pastedChild!.parentId).not.toBe('group-1');
  });

  it('resetPasteCount_makesNextPasteUseFreshOffset', () => {
    // resetPasteCount() lets the editor clear the offset chain when a new workflow loads.
    const a = makeNode('a', 'activity');
    a.position = { x: 0, y: 0 };
    const { result } = setup([a], []);

    act(() => result.current.updateSelection({ nodeIds: ['a'], edgeIds: [] }));
    act(() => result.current.copySelection());
    act(() => result.current.pasteBuffer());
    act(() => result.current.pasteBuffer()); // offset 80

    act(() => result.current.resetPasteCount());
    act(() => result.current.pasteBuffer());

    // After resetPasteCount, the next paste restarts the offset chain at 40.
    const lastClone = store.nodes.slice(-1)[0];
    expect(lastClone.position?.x).toBe(40);
  });
});
