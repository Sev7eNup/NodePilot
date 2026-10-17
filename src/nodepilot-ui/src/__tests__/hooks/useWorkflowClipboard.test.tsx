import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { Node, Edge } from '@xyflow/react';

/**
 * useWorkflowClipboard works on the editor's own graph state, which a small in-test store
 * stands in for. This keeps the tests on the hook's clipboard semantics: what a copy contains,
 * where a paste lands, ID and reference re-mapping, and duplicating without the clipboard.
 */

const store: { nodes: Node[]; edges: Edge[] } = { nodes: [], edges: [] };

// Getters so the hook always sees the current store contents, the way it sees current
// React state in the editor.
const graphSource = {
  get nodes() { return store.nodes; },
  get edges() { return store.edges; },
  setNodes: ((updater: ((ns: Node[]) => Node[]) | Node[]) => {
    store.nodes = typeof updater === 'function' ? updater(store.nodes) : updater;
  }) as React.Dispatch<React.SetStateAction<Node[]>>,
  setEdges: ((updater: ((es: Edge[]) => Edge[]) | Edge[]) => {
    store.edges = typeof updater === 'function' ? updater(store.edges) : updater;
  }) as React.Dispatch<React.SetStateAction<Edge[]>>,
};

import { useWorkflowClipboard } from '../../hooks/useWorkflowClipboard';

const ORIGIN = { x: 500, y: 300 };

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

function makeEdge(id: string, source: string, target: string, data: Record<string, unknown> = { label: 'On Success' }): Edge {
  return { id, source, target, type: 'labeled', data, selected: false };
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
  const markDirty = vi.fn();
  const hook = renderHook(() => useWorkflowClipboard(commitHistory, markDirty, graphSource));
  return { ...hook, commitHistory, markDirty };
}

/** Selects the given nodes and returns the copied text. */
function copy(result: { current: ReturnType<typeof useWorkflowClipboard> }, nodeIds: string[]): string {
  act(() => result.current.updateSelection({ nodeIds, edgeIds: [] }));
  let text: string | null = null;
  act(() => { text = result.current.copySelection(); });
  if (!text) throw new Error('nothing copied');
  return text;
}

const pastedNodes = () => store.nodes.filter((n) => n.selected);
const pastedEdges = () => store.edges.filter((e) => e.selected);

describe('useWorkflowClipboard', () => {
  beforeEach(() => {
    sessionStorage.clear();
    store.nodes = [];
    store.edges = [];
  });

  describe('copySelection', () => {
    it('returnsSelectionWithEdgesBetweenItAsJson', () => {
      const { result } = setup([makeNode('a'), makeNode('b'), makeNode('c')], [makeEdge('eAB', 'a', 'b'), makeEdge('eBC', 'b', 'c')]);

      const parsed = JSON.parse(copy(result, ['a', 'b']));

      expect(parsed.nodes.map((n: Node) => n.id)).toEqual(['a', 'b']);
      expect(parsed.edges.map((e: Edge) => e.id)).toEqual(['eAB']);
    });

    it('selectedGroup_includesTheNodesInside', () => {
      const group = makeNode('g', 'group');
      const child = makeNode('c', 'activity', { parentId: 'g' });
      const { result } = setup([group, child, makeNode('other')], []);

      const parsed = JSON.parse(copy(result, ['g']));

      expect(parsed.nodes.map((n: Node) => n.id)).toEqual(['g', 'c']);
    });

    it('nothingSelected_returnsNull', () => {
      const { result } = setup([makeNode('a')], []);

      act(() => result.current.updateSelection({ nodeIds: [], edgeIds: [] }));

      expect(result.current.copySelection()).toBeNull();
    });

    it('keepsNodesOutOfBrowserStorage', () => {
      // Inline credentials must not end up in storage the next user of the tab could read.
      const { result } = setup([makeNode('secret', 'activity', {
        data: { activityType: 'restApi', config: { apiKey: 'inline-secret' } },
      })], []);

      copy(result, ['secret']);

      expect(sessionStorage.length).toBe(0);
      expect(localStorage.getItem('np_clipboard')).toBeNull();
    });
  });

  describe('pasteText', () => {
    it('ownCopy_landsNextToTheOriginal_andFurtherOnEachPaste', () => {
      const a = makeNode('a', 'activity', { position: { x: 0, y: 0 } });
      const { result } = setup([a], []);
      const text = copy(result, ['a']);

      act(() => { result.current.pasteText(text, ORIGIN); });
      act(() => { result.current.pasteText(text, ORIGIN); });

      const offsets = store.nodes.filter((n) => n.id !== 'a').map((n) => n.position.x).sort((p, q) => p - q);
      expect(offsets).toEqual([40, 80]);
    });

    it('ownCopyWithWindowsLineEndings_isStillRecognised', () => {
      // The system clipboard may hand the text back with CRLF.
      const a = makeNode('a', 'activity', { position: { x: 0, y: 0 } });
      const { result } = setup([a], []);
      const text = copy(result, ['a']);

      act(() => { result.current.pasteText(text.replaceAll('\n', '\r\n'), ORIGIN); });

      expect(pastedNodes()[0].position).toEqual({ x: 40, y: 40 });
    });

    it('resetPasteCount_restartsTheOffset', () => {
      // The editor resets it when another workflow loads.
      const a = makeNode('a', 'activity', { position: { x: 0, y: 0 } });
      const { result } = setup([a], []);
      const text = copy(result, ['a']);

      act(() => { result.current.pasteText(text, ORIGIN); });
      act(() => { result.current.pasteText(text, ORIGIN); });
      act(() => result.current.resetPasteCount());
      act(() => { result.current.pasteText(text, ORIGIN); });

      expect(store.nodes.slice(-1)[0].position).toEqual({ x: 40, y: 40 });
    });

    it('textFromElsewhere_landsWithItsTopLeftNodeAtOrigin', () => {
      const { result } = setup([makeNode('existing')], []);
      const text = JSON.stringify({
        nodes: [
          makeNode('x', 'activity', { position: { x: 300, y: 200 } }),
          makeNode('y', 'activity', { position: { x: 500, y: 260 } }),
        ],
        edges: [makeEdge('xy', 'x', 'y')],
      });

      act(() => { result.current.pasteText(text, ORIGIN); });

      expect(pastedNodes().map((n) => n.position)).toEqual([{ x: 500, y: 300 }, { x: 700, y: 360 }]);
      expect(pastedEdges()).toHaveLength(1);
    });

    it('givesFreshIds_commitsHistoryAndMarksDirty', () => {
      const { result, commitHistory, markDirty } = setup([makeNode('a'), makeNode('b')], [makeEdge('eAB', 'a', 'b')]);
      const text = copy(result, ['a', 'b']);
      expect(markDirty).not.toHaveBeenCalled();

      act(() => { result.current.pasteText(text, ORIGIN); });

      expect(store.nodes).toHaveLength(4);
      pastedNodes().forEach((n) => expect(n.id).toMatch(/^step-/));
      expect(pastedEdges()[0].id).toMatch(/^edge-/);
      // Paste writes through the setters, so onNodesChange never fires; without these the
      // pasted nodes would reach neither undo nor save.
      expect(commitHistory).toHaveBeenCalledWith('Paste');
      expect(markDirty).toHaveBeenCalledOnce();
    });

    it('remapsTheConditionOfACopiedEdge', () => {
      const { result } = setup(
        [makeNode('step-a'), makeNode('step-b')],
        [makeEdge('eAB', 'step-a', 'step-b', { label: 'On Success', condition: 'step-a.success' })],
      );
      const text = copy(result, ['step-a', 'step-b']);

      act(() => { result.current.pasteText(text, ORIGIN); });

      const edge = pastedEdges()[0];
      expect(edge.source).not.toBe('step-a');
      expect(edge.data?.condition).toBe(`${edge.source}.success`);
    });

    it('groupChild_followsTheNewGroup', () => {
      const group = makeNode('group-1', 'group');
      const child = makeNode('child-1', 'activity', { parentId: 'group-1' });
      const { result } = setup([group, child], []);
      const text = copy(result, ['group-1']);

      act(() => { result.current.pasteText(text, ORIGIN); });

      const pastedGroup = pastedNodes().find((n) => n.type === 'group')!;
      const pastedChild = pastedNodes().find((n) => n.type === 'activity')!;
      expect(pastedChild.parentId).toBe(pastedGroup.id);
      expect(pastedChild.parentId).not.toBe('group-1');
    });

    it('textThatIsNoWorkflow_changesNothing', () => {
      const { result, commitHistory } = setup([makeNode('a')], []);

      let ok = true;
      act(() => { ok = result.current.pasteText('hello world', ORIGIN).ok; });

      expect(ok).toBe(false);
      expect(store.nodes).toHaveLength(1);
      expect(commitHistory).not.toHaveBeenCalled();
    });
  });

  describe('duplicateSelection', () => {
    it('insertsACopyNextToTheSelection', () => {
      const a = makeNode('a', 'activity', { position: { x: 0, y: 0 } });
      const { result, commitHistory } = setup([a], []);
      act(() => result.current.updateSelection({ nodeIds: ['a'], edgeIds: [] }));

      act(() => result.current.duplicateSelection());

      expect(pastedNodes()).toHaveLength(1);
      expect(pastedNodes()[0].position).toEqual({ x: 40, y: 40 });
      expect(commitHistory).toHaveBeenCalledOnce();
    });

    it('nothingSelected_isNoOp', () => {
      const { result, commitHistory } = setup([makeNode('a')], []);

      act(() => result.current.duplicateSelection());

      expect(store.nodes).toHaveLength(1);
      expect(commitHistory).not.toHaveBeenCalled();
    });
  });
});
